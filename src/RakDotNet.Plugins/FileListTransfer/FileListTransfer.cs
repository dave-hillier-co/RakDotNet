using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// FileListTransfer (FileListTransfer.cpp): sends a <see cref="FileList"/> to another system, which receives it into an
/// <see cref="IFileListTransferHandler"/> it set up with <see cref="SetupReceive"/>.
/// </summary>
/// <remarks>
/// <para>
/// The receiver calls <see cref="SetupReceive"/> for the sender and passes the set ID it returns to the sender, which
/// calls <see cref="Send"/> with it. The sender sends ID_FILE_LIST_TRANSFER_HEADER (the set ID, then whether the set
/// has files, their count and total data length compressed), then each file with data as one
/// ID_FILE_LIST_TRANSFER_FILE: the context, the set ID, the name through the StringCompressor, the file's index and data
/// length compressed, and from the next byte the data. Every message is RELIABLE_ORDERED at the priority and on the
/// channel given to <see cref="Send"/>. RakNet 4.081 sends the data uncompressed; nothing on the wire marks
/// compression.
/// </para>
/// <para>
/// A file that is a reference (<see cref="FileListNode.IsAReference"/>), sent with an <see cref="IIncrementalReader"/>,
/// is read and sent a chunk at a time as ID_FILE_LIST_REFERENCE_PUSH, which adds the chunk's offset and length
/// compressed and a last-chunk bit before the data; the receiver answers each with ID_FILE_LIST_REFERENCE_PUSH_ACK
/// (HIGH_PRIORITY, RELIABLE, channel 0) and the sender then sends the next. Referenced files whose first chunk is the
/// whole file are sent together as ID_FILE_LIST_TRANSFER_FILE while their total is under the chunk size, one always
/// being kept for a push so an acknowledgement follows (FileListTransfer.cpp:986-1025).
/// </para>
/// <para>
/// A receiver also turns ID_DOWNLOAD_PROGRESS for either message into <see cref="IFileListTransferHandler.OnFileProgress"/>,
/// so a large file reports progress when the peer's <see cref="RakPeer.SplitMessageProgressInterval"/> is set.
/// </para>
/// <para>
/// Messages are accepted only from the system a set was set up for. A file length, offset or chunk length that does not
/// fit the file or the message drops the message, where RakNet would read or write past its buffers. So that a sender
/// cannot exhaust the receiver's memory, a chunk of a file sent incrementally is dropped unless the set header has
/// arrived, the file's index is within the set's file count and the files given a buffer, this one included, are no
/// longer together than the set; RakNet allocates whatever length a chunk states.
/// </para>
/// <para>
/// Members may be called from any thread; handlers and progress callbacks run on the dispatch step, on the caller of
/// <see cref="Send"/>, or on an incremental read worker.
/// </para>
/// </remarks>
public sealed class FileListTransfer : IRakPlugin, IDisposable
{
    /// <summary>The set ID <see cref="SetupReceive"/> returns on failure: (unsigned short)-1.</summary>
    public const ushort InvalidSetId = ushort.MaxValue;

    /// <summary>Send's default _chunkSize: 262144*4*16.</summary>
    public const uint DefaultChunkSize = 262144 * 4 * 16;

    // The string limit FileListTransfer passes to EncodeString and DecodeString (FileListTransfer.cpp:211, 389).
    private const int MaxFileNameLength = 512;

    // ID_DOWNLOAD_PROGRESS: the ID, three host-order u32s, then the first part (ReliabilityLayer.cpp:3159-3172).
    private const int DownloadProgressHeaderLength = 1 + 3 * sizeof(uint);

    private readonly Lock _gate = new();
    private readonly SortedList<ushort, FileListReceiver> _receivers = [];
    private readonly List<IFileListProgress> _callbacks = [];
    private readonly List<FileToPushRecipient> _recipients = [];
    private readonly IncrementalReadWorkers _workers;
    private ushort _setId;
    private IPluginHost? _host;

    public FileListTransfer() => _workers = new IncrementalReadWorkers(SendIriToAddressCallback);

    /// <summary>
    /// StartIncrementalReadThreads: reads and sends the chunks of referenced files on <paramref name="threadCount"/>
    /// worker threads instead of the thread that asked for them.
    /// </summary>
    public void StartIncrementalReadThreads(int threadCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadCount);
        _workers.Start(threadCount);
    }

    /// <summary>
    /// SetupReceive (FileListTransfer.cpp:117-146): allows one set from <paramref name="allowedSender"/> to arrive into
    /// <paramref name="handler"/>, and returns its set ID for the sender's <see cref="Send"/>. Returns
    /// <see cref="InvalidSetId"/> when attached to a peer that is not connected to the sender. A handler already holding
    /// the next set ID is dereferenced first.
    /// </summary>
    public ushort SetupReceive(IFileListTransferHandler handler, SystemAddress allowedSender)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_host?.Peer is { } peer && peer.GetConnectionState(allowedSender) != ConnectionState.Connected)
                return InvalidSetId;
            if (_receivers.Remove(_setId, out var previous))
                previous.Handler.OnDereference();
            _receivers[_setId] = new FileListReceiver(handler, allowedSender, _setId);
            var setId = _setId;
            if (++_setId == ushort.MaxValue)
                _setId = 0;
            return setId;
        }
    }

    /// <summary>
    /// Send (FileListTransfer.cpp:148-278): sends <paramref name="fileList"/> to <paramref name="recipient"/>, which set
    /// up <paramref name="setId"/> with <see cref="SetupReceive"/>.
    /// </summary>
    /// <param name="fileList">The files. Each progress callback of this transfer is added to it.</param>
    /// <param name="recipient">The system to send to.</param>
    /// <param name="setId">The set ID the recipient's <see cref="SetupReceive"/> returned.</param>
    /// <param name="priority">The priority of every message.</param>
    /// <param name="orderingChannel">The ordering channel of every message.</param>
    /// <param name="incrementalReader">Reads the files that are references, a chunk at a time; without it they are sent as they are.</param>
    /// <param name="chunkSize">How many bytes of a referenced file to read and send at once.</param>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a peer.</exception>
    public void Send(FileList fileList, SystemAddress recipient, ushort setId, Priority priority, byte orderingChannel,
                     IIncrementalReader? incrementalReader = null, uint chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(fileList);
        ArgumentOutOfRangeException.ThrowIfZero(chunkSize);
        lock (_gate)
        {
            var host = Host;
            foreach (var callback in _callbacks)
                fileList.AddCallback(callback);

            var files = fileList.Files;
            var totalLength = 0u;
            foreach (var node in files)
                totalLength = unchecked(totalLength + node.DataLengthBytes);

            Span<byte> header = stackalloc byte[1 + sizeof(ushort) + 1 + 2 * (sizeof(uint) + 1)];
            var writer = new BitStreamWriter(header);
            writer.WriteByte((byte)MessageId.FileListTransferHeader);
            writer.WriteUInt16(setId);
            var anythingToWrite = files.Count > 0;
            writer.WriteBool(anythingToWrite);
            if (!anythingToWrite)
            {
                foreach (var callback in _callbacks.ToArray())
                    callback.OnFilePushesComplete(recipient, setId);
                host.Send(writer, recipient, Reliability.ReliableOrdered, priority, orderingChannel);
                return;
            }

            writer.WriteCompressed((uint)files.Count);
            writer.WriteCompressed(totalLength);
            host.Send(writer, recipient, Reliability.ReliableOrdered, priority, orderingChannel);

            var filesToPush = new LinkedList<FileToPush>();
            for (var i = 0; i < files.Count; i++)
            {
                var node = files[i];
                if (node.IsAReference && incrementalReader is not null)
                {
                    filesToPush.AddLast(new FileToPush(node.Filename, node.FullPathToFile, node.FileLengthBytes, node.DataLengthBytes, node.Context,
                                                       priority, orderingChannel, (uint)i, incrementalReader, chunkSize));
                }
                else
                {
                    SendFile(host, MessageId.FileListTransferFile, node.Context, setId, node.Filename, (uint)i, node.DataLengthBytes, node.SentData(), recipient, priority, orderingChannel);
                }
            }

            if (filesToPush.Count == 0)
            {
                foreach (var callback in _callbacks.ToArray())
                    callback.OnFilePushesComplete(recipient, setId);
                return;
            }
            _recipients.Add(new FileToPushRecipient(recipient, setId, filesToPush));
        }
        SendIriToAddress(recipient, setId);
    }

    /// <summary>GetPendingFilesToAddress: files still to be sent incrementally to the first set for <paramref name="recipient"/>.</summary>
    public uint GetPendingFilesToAddress(SystemAddress recipient)
    {
        lock (_gate)
        {
            foreach (var r in _recipients)
            {
                if (r.Address == recipient)
                    return (uint)r.FilesToPush.Count;
            }
            return 0;
        }
    }

    /// <summary>CancelReceive: stops the set's download and dereferences its handler.</summary>
    public void CancelReceive(ushort setId)
    {
        lock (_gate)
        {
            if (_receivers.Remove(setId, out var receiver))
                receiver.Handler.OnDereference();
        }
    }

    /// <summary>
    /// RemoveReceiver (FileListTransfer.cpp:580-633): drops every set from and every send to
    /// <paramref name="systemAddress"/>, reporting each send as aborted.
    /// </summary>
    public void RemoveReceiver(SystemAddress systemAddress)
    {
        _workers.RemoveInput(systemAddress);
        lock (_gate)
        {
            var i = 0;
            while (i < _receivers.Count)
            {
                var receiver = _receivers.GetValueAtIndex(i);
                if (receiver.AllowedSender == systemAddress)
                {
                    receiver.Handler.OnDereference();
                    _receivers.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            i = 0;
            while (i < _recipients.Count)
            {
                if (_recipients[i].Address == systemAddress)
                {
                    foreach (var callback in _callbacks.ToArray())
                        callback.OnSendAborted(_recipients[i].Address);
                    _recipients.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }
    }

    /// <summary>IsHandlerActive: whether the set's handler is still in use.</summary>
    public bool IsHandlerActive(ushort setId)
    {
        lock (_gate)
            return _receivers.ContainsKey(setId);
    }

    /// <summary>AddCallback: adds a progress callback once; it is added to every list sent after.</summary>
    public void AddCallback(IFileListProgress? callback)
    {
        if (callback is null)
            return;
        lock (_gate)
        {
            if (!_callbacks.Contains(callback))
                _callbacks.Add(callback);
        }
    }

    /// <summary>RemoveCallback</summary>
    public void RemoveCallback(IFileListProgress callback)
    {
        lock (_gate)
            _callbacks.Remove(callback);
    }

    /// <summary>ClearCallbacks</summary>
    public void ClearCallbacks()
    {
        lock (_gate)
            _callbacks.Clear();
    }

    /// <summary>GetCallbacks: a copy of the progress callbacks.</summary>
    public IReadOnlyList<IFileListProgress> GetCallbacks()
    {
        lock (_gate)
            return [.. _callbacks];
    }

    /// <summary>Stops the incremental read workers, as RakNet's destructor does.</summary>
    public void Dispose() => _workers.Dispose();

    private IPluginHost Host => _host ?? throw new InvalidOperationException("The FileListTransfer is not attached to a peer.");

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnRakPeerShutdown (FileListTransfer.cpp:527-532).</summary>
    void IRakPlugin.OnShutdown()
    {
        _workers.Stop();
        _workers.ClearInput();
        lock (_gate)
        {
            foreach (var receiver in _receivers.Values)
                receiver.Handler.OnDereference();
            _receivers.Clear();
            _recipients.Clear();
        }
    }

    /// <summary>OnClosedConnection: RemoveReceiver.</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason) => RemoveReceiver(connection.Address);

    /// <summary>Update (FileListTransfer.cpp:661-678): ends each handler whose Update returns false.</summary>
    void IRakPlugin.OnUpdate()
    {
        lock (_gate)
        {
            var i = 0;
            while (i < _receivers.Count)
            {
                var receiver = _receivers.GetValueAtIndex(i);
                if (!receiver.Handler.Update())
                {
                    receiver.Handler.OnDereference();
                    var index = _receivers.IndexOfValue(receiver);
                    if (index >= 0)
                        _receivers.RemoveAt(index);
                }
                else
                {
                    i++;
                }
            }
        }
    }

    /// <summary>OnReceive (FileListTransfer.cpp:492-526).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        switch ((MessageId)data[0])
        {
            case MessageId.FileListTransferHeader:
                DecodeSetHeader(data, e.Address, e.RakNetGuid);
                return PluginResult.Consume;
            case MessageId.FileListTransferFile:
                DecodeFile(data, e.Address, e.RakNetGuid, isTheFullFile: true);
                return PluginResult.Consume;
            case MessageId.FileListReferencePush:
                OnReferencePush(data, e.Address, e.RakNetGuid, isTheFullFile: true);
                return PluginResult.Consume;
            case MessageId.FileListReferencePushAck:
                OnReferencePushAck(data, e.Address);
                return PluginResult.Consume;
            case MessageId.DownloadProgress when data.Length > DownloadProgressHeaderLength:
                if (data[DownloadProgressHeaderLength] == (byte)MessageId.FileListTransferFile)
                {
                    DecodeFile(data, e.Address, e.RakNetGuid, isTheFullFile: false);
                    return PluginResult.Consume;
                }
                if (data[DownloadProgressHeaderLength] == (byte)MessageId.FileListReferencePush)
                {
                    OnReferencePush(data, e.Address, e.RakNetGuid, isTheFullFile: false);
                    return PluginResult.Consume;
                }
                return PluginResult.Continue;
            default:
                return PluginResult.Continue;
        }
    }

    // DecodeSetHeader (FileListTransfer.cpp:280-344).
    private void DecodeSetHeader(ReadOnlySpan<byte> data, SystemAddress sender, RakNetGuid senderGuid)
    {
        var reader = new BitStreamReader(data);
        reader.IgnoreBits(8);
        if (!reader.TryReadUInt16(out var setId))
            return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(setId, out var receiver) || receiver.AllowedSender != sender)
                return;
            if (!reader.TryReadBool(out var anythingToWrite))
                return;
            if (anythingToWrite)
            {
                if (reader.TryReadCompressed(out uint setCount))
                    receiver.SetCount = setCount;
                if (reader.TryReadCompressed(out uint setTotalFinalLength))
                {
                    receiver.SetTotalFinalLength = setTotalFinalLength;
                    receiver.SetTotalCompressedTransmissionLength = setTotalFinalLength;
                    receiver.GotSetHeader = true;
                }
                return;
            }
            Complete(receiver, sender, senderGuid);
        }
    }

    // DecodeFile (FileListTransfer.cpp:346-491).
    private void DecodeFile(ReadOnlySpan<byte> data, SystemAddress sender, RakNetGuid senderGuid, bool isTheFullFile)
    {
        uint partCount = 0, partTotal = 0, partLength = 0;
        var start = 1;
        if (!isTheFullFile)
        {
            // Read in host order: ReliabilityLayer.cpp writes these with memcpy.
            partCount = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            partTotal = BinaryPrimitives.ReadUInt32LittleEndian(data[5..]);
            partLength = BinaryPrimitives.ReadUInt32LittleEndian(data[9..]);
            start = DownloadProgressHeaderLength + 1;
        }
        var reader = new BitStreamReader(data[start..]);
        if (!FileListNodeContext.TryRead(ref reader, out var context) || !reader.TryReadUInt16(out var setId))
            return;
        lock (_gate)
        {
            if (!_receivers.TryGetValue(setId, out var receiver) || receiver.AllowedSender != sender)
                return;
            if (!reader.TryReadCompressedString(MaxFileNameLength, out var fileName)
                || !reader.TryReadCompressed(out uint fileIndex) || !reader.TryReadCompressed(out uint byteLengthOfThisFile))
                return;

            var file = new ReceivedFile
            {
                SenderAddress = sender,
                SenderGuid = senderGuid,
                Context = context,
                SetId = setId,
                FileName = fileName,
                FileIndex = fileIndex,
                ByteLengthOfThisFile = byteLengthOfThisFile,
                NumberOfFilesInThisSet = receiver.SetCount,
                ByteLengthOfThisSet = receiver.SetTotalFinalLength,
            };
            reader.AlignToByte();
            var remaining = reader.RemainingAlignedBytes;

            if (!isTheFullFile)
            {
                file.BytesDownloadedForThisFile = unchecked(partLength * partCount);
                file.BytesDownloadedForThisSet = unchecked(receiver.SetTotalDownloadedLength + file.BytesDownloadedForThisFile);
                var first = remaining.ToArray();
                // Remote system is sending a complete file, but the file is large enough that we get progress from the
                // transport layer.
                receiver.Handler.OnFileProgress(new FileProgress(file)
                {
                    PartCount = partCount,
                    PartTotal = partTotal,
                    DataChunkLength = (uint)first.Length,
                    FirstDataChunk = first,
                    SenderAddress = sender,
                    SenderGuid = senderGuid,
                });
                return;
            }

            if ((uint)remaining.Length < byteLengthOfThisFile)
                return;
            var fileData = remaining[..(int)byteLengthOfThisFile].ToArray();
            file.FileData = fileData;
            file.BytesDownloadedForThisFile = byteLengthOfThisFile;
            receiver.SetTotalDownloadedLength = unchecked(receiver.SetTotalDownloadedLength + byteLengthOfThisFile);
            file.BytesDownloadedForThisSet = receiver.SetTotalDownloadedLength;

            receiver.Handler.OnFileProgress(new FileProgress(file)
            {
                PartCount = 1,
                PartTotal = 1,
                DataChunkLength = byteLengthOfThisFile,
                FirstDataChunk = fileData,
                IriDataChunk = fileData,
                HasIriDataChunk = true,
                SenderAddress = sender,
                SenderGuid = senderGuid,
            });
            receiver.Handler.OnFile(file);
            FileReceived(receiver, sender, senderGuid);
        }
    }

    // OnReferencePush (FileListTransfer.cpp:679-915).
    private void OnReferencePush(ReadOnlySpan<byte> data, SystemAddress sender, RakNetGuid senderGuid, bool isTheFullFile)
    {
        uint partCount = 0, partTotal = 1, partLength = 0;
        var start = 1;
        if (!isTheFullFile)
        {
            partCount = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
            partTotal = BinaryPrimitives.ReadUInt32LittleEndian(data[5..]);
            partLength = BinaryPrimitives.ReadUInt32LittleEndian(data[9..]);
            start = DownloadProgressHeaderLength + 1;
        }
        var reader = new BitStreamReader(data[start..]);
        if (!FileListNodeContext.TryRead(ref reader, out var context) || !reader.TryReadUInt16(out var setId))
            return;

        // This is not a progress notification, it is the entire packet.
        if (isTheFullFile)
        {
            Span<byte> ack = stackalloc byte[1 + sizeof(ushort)];
            ack[0] = (byte)MessageId.FileListReferencePushAck;
            BinaryPrimitives.WriteUInt16BigEndian(ack[1..], setId);
            if (_host is { } host)
                host.Send(ack, sender, Reliability.Reliable, Priority.High, 0);
        }

        lock (_gate)
        {
            if (!_receivers.TryGetValue(setId, out var receiver) || receiver.AllowedSender != sender)
                return;
            if (!reader.TryReadCompressedString(MaxFileNameLength, out var fileName)
                || !reader.TryReadCompressed(out uint fileIndex) || !reader.TryReadCompressed(out uint byteLengthOfThisFile)
                || !reader.TryReadCompressed(out uint offset) || !reader.TryReadCompressed(out uint chunkLength)
                || !reader.TryReadBool(out var lastChunk))
                return;
            if (byteLengthOfThisFile > (uint)Array.MaxLength)
                return;
            var finished = lastChunk && isTheFullFile;

            // BITS_TO_BYTES of the unread bits, counted before the read is aligned (FileListTransfer.cpp:770-771).
            var unreadBytes = (uint)((reader.BitsRemaining + 7) / 8);
            reader.AlignToByte();
            var remaining = reader.RemainingAlignedBytes;
            var amountToRead = isTheFullFile ? chunkLength : unreadBytes;
            if (isTheFullFile && ((ulong)offset + chunkLength > byteLengthOfThisFile || (uint)remaining.Length < chunkLength))
                return;

            if (!isTheFullFile)
                receiver.PartLength = partLength;

            if (receiver.PushedFiles.TryGetValue(fileIndex, out var block))
            {
                // A later chunk must fit the buffer the first one allocated.
                if (isTheFullFile && block is not null && (ulong)offset + chunkLength > (uint)block.Length)
                    return;
            }
            else
            {
                // RakNet allocates whatever length the sender states (FileListTransfer.cpp:760-764). Here the files must be
                // in the set the header announced, and their lengths together within its length.
                if (!receiver.GotSetHeader || fileIndex >= receiver.SetCount
                    || receiver.PushedFileBytes + byteLengthOfThisFile > receiver.SetTotalFinalLength)
                    return;
                receiver.PushedFileBytes += byteLengthOfThisFile;
                block = new byte[byteLengthOfThisFile];
                receiver.PushedFiles[fileIndex] = block;
            }

            var file = new ReceivedFile
            {
                Context = context,
                SetId = setId,
                FileName = fileName,
                FileIndex = fileIndex,
                ByteLengthOfThisFile = byteLengthOfThisFile,
            };
            ReadOnlyMemory<byte> iriDataChunk;
            if (isTheFullFile)
            {
                if (block is not null)
                {
                    remaining[..(int)chunkLength].CopyTo(block.AsSpan((int)offset));
                    iriDataChunk = block.AsMemory((int)offset, (int)chunkLength);
                }
                else
                {
                    // The first block explicitly declined the memory, so no blocks are held.
                    iriDataChunk = remaining[..(int)chunkLength].ToArray();
                }
                file.BytesDownloadedForThisFile = unchecked(offset + chunkLength);
                receiver.SetTotalDownloadedLength = unchecked(receiver.SetTotalDownloadedLength + chunkLength);
                file.BytesDownloadedForThisSet = receiver.SetTotalDownloadedLength;
            }
            else
            {
                file.BytesDownloadedForThisFile = unchecked(offset + partLength * partCount);
                file.BytesDownloadedForThisSet = unchecked(receiver.SetTotalDownloadedLength + partCount * partLength);
                iriDataChunk = remaining.ToArray();
            }

            file.NumberOfFilesInThisSet = receiver.SetCount;
            file.ByteLengthOfThisSet = receiver.SetTotalFinalLength;
            file.FileData = block;
            file.SenderAddress = sender;
            file.SenderGuid = senderGuid;

            var totalNotifications = chunkLength == 0 || chunkLength == byteLengthOfThisFile ? 1 : byteLengthOfThisFile / chunkLength + 1;
            var currentNotificationIndex = chunkLength == 0 ? 0 : offset / chunkLength;
            var progress = new FileProgress(file)
            {
                PartCount = currentNotificationIndex,
                PartTotal = totalNotifications,
                DataChunkLength = amountToRead,
                FirstDataChunk = block,
                IriDataChunk = iriDataChunk,
                HasIriDataChunk = true,
                IriWriteOffset = offset,
                SenderAddress = sender,
                SenderGuid = senderGuid,
            };

            if (finished)
            {
                if (progress.PartCount == 0)
                    progress.FirstDataChunk = iriDataChunk;
                if (progress.PartTotal == 1)
                    file.FileData = iriDataChunk.ToArray();
                receiver.Handler.OnFileProgress(progress);
                // The last chunk of a file sent incrementally: the file is done.
                receiver.Handler.OnFile(file);
                receiver.PushedFiles.Remove(fileIndex);
                FileReceived(receiver, sender, senderGuid);
                return;
            }

            if (isTheFullFile)
            {
                receiver.Handler.OnFileProgress(progress);
                if (!progress.AllocateIriDataChunkAutomatically)
                    receiver.PushedFiles[fileIndex] = null;
                return;
            }

            // A progress notification for a chunk not yet whole.
            if (receiver.PartLength == 0)
                return;
            progress.PartTotal = byteLengthOfThisFile / receiver.PartLength + 1;
            progress.PartCount = unchecked(offset + partCount * receiver.PartLength) / receiver.PartLength;
            progress.IriDataChunk = default;
            progress.HasIriDataChunk = false;
            receiver.Handler.OnFileProgress(progress);
        }
    }

    // OnReferencePushAck (FileListTransfer.cpp:1120-1127).
    private void OnReferencePushAck(ReadOnlySpan<byte> data, SystemAddress sender)
    {
        if (data.Length < 1 + sizeof(ushort))
            return;
        SendIriToAddress(sender, BinaryPrimitives.ReadUInt16BigEndian(data[1..]));
    }

    // SendIRIToAddress (FileListTransfer.cpp:1103-1119).
    private void SendIriToAddress(SystemAddress address, ushort setId)
    {
        if (_workers.WasStarted)
            _workers.Add(address, setId);
        else
            SendIriToAddressCallback(address, setId);
    }

    // SendIRIToAddressCB (FileListTransfer.cpp:936-1101).
    private void SendIriToAddressCallback(SystemAddress address, ushort setId)
    {
        lock (_gate)
        {
            if (_host is not { } host)
                return;
            var recipient = _recipients.Find(r => r.Address == address && r.SetId == setId);
            if (recipient is null || recipient.FilesToPush.Count == 0)
                return;

            var ftp = recipient.FilesToPush.First!.Value;
            recipient.FilesToPush.RemoveFirst();
            var chunkSize = (int)Math.Min(ftp.ChunkSize, (uint)Array.MaxLength);
            var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
            try
            {
                var bytesRead = Read(ftp, buffer, chunkSize);
                var done = ftp.DataLengthBytes == unchecked(ftp.CurrentOffset + bytesRead);
                var smallFileTotalSize = 0u;
                while (done && ftp.CurrentOffset == 0 && smallFileTotalSize < ftp.ChunkSize)
                {
                    // Two, because a push gets an acknowledgement, without which this would not run again.
                    if (recipient.FilesToPush.Count < 2)
                        break;
                    // Send all small files at once, rather than wait for an acknowledgement each.
                    SendFile(host, MessageId.FileListTransferFile, ftp.Context, setId, ftp.Filename, ftp.SetIndex, ftp.DataLengthBytes,
                             buffer.AsSpan(0, (int)bytesRead), address, ftp.Priority, ftp.OrderingChannel);
                    smallFileTotalSize = unchecked(smallFileTotalSize + bytesRead);
                    ftp = recipient.FilesToPush.First!.Value;
                    recipient.FilesToPush.RemoveFirst();
                    bytesRead = Read(ftp, buffer, chunkSize);
                    done = ftp.DataLengthBytes == unchecked(ftp.CurrentOffset + bytesRead);
                }

                var pushOffset = ftp.CurrentOffset;
                ftp.CurrentOffset = unchecked(ftp.CurrentOffset + bytesRead);
                foreach (var callback in _callbacks.ToArray())
                    callback.OnFilePush(ftp.Filename, ftp.FileLengthBytes, pushOffset, bytesRead, done, address, setId);

                if (done)
                {
                    if (recipient.FilesToPush.Count == 0)
                    {
                        foreach (var callback in _callbacks.ToArray())
                            callback.OnFilePushesComplete(address, setId);
                        _recipients.Remove(recipient);
                    }
                }
                else
                {
                    recipient.FilesToPush.AddFirst(ftp);
                }

                // Sent after the bookkeeping above, as RakNet moved it (FileListTransfer.cpp:1084-1086).
                SendReferencePush(host, ftp, setId, pushOffset, bytesRead, done, buffer.AsSpan(0, (int)bytesRead), address);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        static uint Read(FileToPush ftp, byte[] buffer, int chunkSize) =>
            Math.Min(ftp.Reader.GetFilePart(ftp.FullPathToFile, ftp.CurrentOffset, buffer.AsSpan(0, chunkSize), ftp.Context), (uint)chunkSize);
    }

    // ID_FILE_LIST_TRANSFER_FILE (FileListTransfer.cpp:206-222, 998-1012): the header, aligned, then the data.
    private static void SendFile(IPluginHost host, MessageId id, FileListNodeContext context, ushort setId, string fileName, uint setIndex,
                                 uint dataLengthBytes, ReadOnlySpan<byte> data, SystemAddress recipient, Priority priority, byte orderingChannel)
    {
        var headerBound = HeaderBound(fileName);
        var message = ArrayPool<byte>.Shared.Rent(headerBound + data.Length);
        try
        {
            var writer = new BitStreamWriter(message);
            writer.WriteByte((byte)id);
            context.Write(ref writer);
            writer.WriteUInt16(setId);
            writer.WriteCompressedString(fileName.AsSpan(), MaxFileNameLength);
            writer.WriteCompressed(setIndex);
            writer.WriteCompressed(dataLengthBytes);
            writer.AlignToByte();
            var headerLength = writer.BytesWritten;
            data.CopyTo(message.AsSpan(headerLength));
            host.Send(message.AsSpan(0, headerLength + data.Length), recipient, Reliability.ReliableOrdered, priority, orderingChannel);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    // ID_FILE_LIST_REFERENCE_PUSH (FileListTransfer.cpp:1028-1047): the header in whole bytes, then the chunk.
    private static void SendReferencePush(IPluginHost host, FileToPush ftp, ushort setId, uint offset, uint bytesRead, bool done,
                                          ReadOnlySpan<byte> chunk, SystemAddress recipient)
    {
        var message = ArrayPool<byte>.Shared.Rent(HeaderBound(ftp.Filename) + 2 * (sizeof(uint) + 1) + 1 + chunk.Length);
        try
        {
            var writer = new BitStreamWriter(message);
            writer.WriteByte((byte)MessageId.FileListReferencePush);
            ftp.Context.Write(ref writer);
            writer.WriteUInt16(setId);
            writer.WriteCompressedString(ftp.Filename.AsSpan(), MaxFileNameLength);
            writer.WriteCompressed(ftp.SetIndex);
            writer.WriteCompressed(ftp.DataLengthBytes);
            writer.WriteCompressed(offset);
            writer.WriteCompressed(bytesRead);
            writer.WriteBool(done);
            var headerLength = writer.BytesWritten;
            chunk.CopyTo(message.AsSpan(headerLength));
            host.Send(message.AsSpan(0, headerLength + chunk.Length), recipient, Reliability.ReliableOrdered, ftp.Priority, ftp.OrderingChannel);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(message);
        }
    }

    // The ID, the context, the set ID, the name (at most 255 bits a byte, plus its compressed length) and two compressed u32s.
    private static int HeaderBound(string fileName) =>
        1 + FileListNodeContext.SerializedLength + sizeof(ushort) + 8 + Math.Min(Encoding.UTF8.GetByteCount(fileName), MaxFileNameLength) * 32 + 2 * (sizeof(uint) + 1) + 1;

    // A file of the set is done: count it, and complete the set when it was the last (FileListTransfer.cpp:441-461).
    private void FileReceived(FileListReceiver receiver, SystemAddress sender, RakNetGuid senderGuid)
    {
        receiver.FilesReceived++;
        if ((int)receiver.SetCount == receiver.FilesReceived)
            Complete(receiver, sender, senderGuid);
    }

    private void Complete(FileListReceiver receiver, SystemAddress sender, RakNetGuid senderGuid)
    {
        var complete = new DownloadComplete(receiver.SetId, receiver.SetCount, receiver.SetTotalFinalLength, sender, senderGuid);
        if (receiver.Handler.OnDownloadComplete(complete))
            return;
        receiver.Handler.OnDereference();
        if (_receivers.TryGetValue(receiver.SetId, out var current) && current == receiver)
            _receivers.Remove(receiver.SetId);
    }

    /// <summary>FileListReceiver (FileListTransfer.cpp:40-60).</summary>
    private sealed class FileListReceiver(IFileListTransferHandler handler, SystemAddress allowedSender, ushort setId)
    {
        public IFileListTransferHandler Handler { get; } = handler;

        public SystemAddress AllowedSender { get; } = allowedSender;

        public ushort SetId { get; } = setId;

        public uint SetCount { get; set; }

        public uint SetTotalCompressedTransmissionLength { get; set; }

        public uint SetTotalFinalLength { get; set; }

        public uint SetTotalDownloadedLength { get; set; }

        public bool GotSetHeader { get; set; }

        public int FilesReceived { get; set; }

        // pushedFiles: the buffer each file sent incrementally is assembled in; null once the handler declined it.
        public SortedList<uint, byte[]?> PushedFiles { get; } = [];

        // The lengths of every file given a buffer in PushedFiles, kept within SetTotalFinalLength.
        public ulong PushedFileBytes { get; set; }

        public uint PartLength { get; set; } = 1;
    }

    /// <summary>FileToPush (FileListTransfer.h:140-150).</summary>
    private sealed class FileToPush(string filename, string fullPathToFile, uint fileLengthBytes, uint dataLengthBytes, FileListNodeContext context,
                                    Priority priority, byte orderingChannel, uint setIndex, IIncrementalReader reader, uint chunkSize)
    {
        public string Filename { get; } = filename;

        public string FullPathToFile { get; } = fullPathToFile;

        public uint FileLengthBytes { get; } = fileLengthBytes;

        public uint DataLengthBytes { get; } = dataLengthBytes;

        public FileListNodeContext Context { get; } = context;

        public Priority Priority { get; } = priority;

        public byte OrderingChannel { get; } = orderingChannel;

        public uint SetIndex { get; } = setIndex;

        public IIncrementalReader Reader { get; } = reader;

        public uint ChunkSize { get; } = chunkSize;

        public uint CurrentOffset { get; set; }
    }

    /// <summary>FileToPushRecipient (FileListTransfer.h:151-164); the list holds it, so no reference count is needed.</summary>
    private sealed class FileToPushRecipient(SystemAddress address, ushort setId, LinkedList<FileToPush> filesToPush)
    {
        public SystemAddress Address { get; } = address;

        public ushort SetId { get; } = setId;

        public LinkedList<FileToPush> FilesToPush { get; } = filesToPush;
    }

    /// <summary>
    /// ThreadPool&lt;ThreadData, int&gt; as FileListTransfer uses it: worker threads taking (address, set ID) inputs in
    /// order and running the send for each.
    /// </summary>
    private sealed class IncrementalReadWorkers(Action<SystemAddress, ushort> work) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly LinkedList<(SystemAddress Address, ushort SetId)> _input = new();
        private readonly List<Thread> _threads = [];
        private SemaphoreSlim? _signal;
        private bool _stopping;

        public bool WasStarted
        {
            get
            {
                lock (_gate)
                    return _threads.Count > 0;
            }
        }

        public void Start(int threadCount)
        {
            lock (_gate)
            {
                if (_threads.Count > 0)
                    return;
                _stopping = false;
                _signal = new SemaphoreSlim(0);
                for (var i = 0; i < threadCount; i++)
                {
                    var thread = new Thread(Run) { IsBackground = true, Name = "FileListTransfer incremental read" };
                    _threads.Add(thread);
                    thread.Start(_signal);
                }
            }
        }

        public void Add(SystemAddress address, ushort setId)
        {
            lock (_gate)
            {
                _input.AddLast((address, setId));
                _signal?.Release();
            }
        }

        public void RemoveInput(SystemAddress address)
        {
            lock (_gate)
            {
                var node = _input.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (node.Value.Address == address)
                        _input.Remove(node);
                    node = next;
                }
            }
        }

        public void ClearInput()
        {
            lock (_gate)
                _input.Clear();
        }

        public void Dispose() => Stop();

        public void Stop()
        {
            Thread[] threads;
            SemaphoreSlim? signal;
            lock (_gate)
            {
                threads = [.. _threads];
                signal = _signal;
                _threads.Clear();
                _signal = null;
                _stopping = true;
            }
            if (signal is null)
                return;
            signal.Release(threads.Length);
            foreach (var thread in threads)
            {
                if (thread != Thread.CurrentThread)
                    thread.Join();
            }
            signal.Dispose();
        }

        private void Run(object? state)
        {
            var signal = (SemaphoreSlim)state!;
            while (true)
            {
                try
                {
                    signal.Wait();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                (SystemAddress Address, ushort SetId) input;
                lock (_gate)
                {
                    if (_stopping || _signal != signal)
                        return;
                    if (_input.First is not { } first)
                        continue;
                    input = first.Value;
                    _input.RemoveFirst();
                }
                work(input.Address, input.SetId);
            }
        }
    }
}
