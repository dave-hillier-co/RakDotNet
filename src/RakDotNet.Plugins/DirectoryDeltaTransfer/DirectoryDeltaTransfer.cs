using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// DirectoryDeltaTransfer (DirectoryDeltaTransfer.cpp): a simple patcher. A system offers the files under subdirectories
/// of its application directory; another downloads the files of one of them that it lacks or holds different, which are
/// written under a directory of its own. The files travel through a <see cref="Plugins.FileListTransfer"/> attached to
/// the same peer.
/// </summary>
/// <remarks>
/// <para>
/// The downloader sends ID_DDT_DOWNLOAD_REQUEST: the set ID its <see cref="Plugins.FileListTransfer.SetupReceive"/>
/// returned, the subdirectory to download and the output subdirectory through the StringCompressor, then a
/// <see cref="FileList"/> of the hashes of the files it already has (<see cref="FileList.Serialize"/>). The uploader
/// sends back, through its FileListTransfer, every offered file in that subdirectory that the list lacks or holds with a
/// different length or hash.
/// </para>
/// <para>
/// A received file is written to the output directory followed by its name less the downloaded subdirectory. A name that
/// would land outside the output directory is not written, though the handler still gets the file; RakNet writes it
/// wherever the name points.
/// </para>
/// <para>Members may be called from any thread; the request is answered on the dispatch step.</para>
/// </remarks>
public sealed class DirectoryDeltaTransfer : IRakPlugin
{
    // The string limit of the request's two subdirectories (DirectoryDeltaTransfer.cpp:186-187, 213-214).
    private const int MaxSubdirectoryLength = 256;

    private readonly Lock _gate = new();
    private readonly FileList _availableUploads = new();
    private string _applicationDirectory = string.Empty;
    private FileListTransfer? _fileListTransfer;
    private Priority _priority = Priority.High;
    private byte _orderingChannel;
    private IIncrementalReader? _incrementalReader;
    private uint _chunkSize = FileListTransfer.DefaultChunkSize;
    private IPluginHost? _host;

    /// <summary>
    /// SetFileListTransferPlugin (DirectoryDeltaTransfer.cpp:126-151): the attached <see cref="Plugins.FileListTransfer"/> that
    /// sends and receives the files. Its progress callbacks are given the offered files' list.
    /// </summary>
    public FileListTransfer? FileListTransfer
    {
        get
        {
            lock (_gate)
                return _fileListTransfer;
        }
        set
        {
            lock (_gate)
            {
                if (_fileListTransfer is { } old)
                {
                    foreach (var callback in old.GetCallbacks())
                        _availableUploads.RemoveCallback(callback);
                }
                _fileListTransfer = value;
                if (value is not null)
                {
                    foreach (var callback in value.GetCallbacks())
                        _availableUploads.AddCallback(callback);
                }
                else
                {
                    _availableUploads.ClearCallbacks();
                }
            }
        }
    }

    /// <summary>
    /// SetApplicationDirectory (DirectoryDeltaTransfer.cpp:152-163): the local directory offered and downloaded
    /// subdirectories are under; '/' is appended unless it ends in a slash.
    /// </summary>
    public string ApplicationDirectory
    {
        get
        {
            lock (_gate)
                return _applicationDirectory;
        }
        set
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(value))
                    _applicationDirectory = string.Empty;
                else
                    _applicationDirectory = FileOperations.IsSlash(value[^1]) ? value : value + "/";
            }
        }
    }

    /// <summary>GetNumberOfFilesForUpload</summary>
    public int NumberOfFilesForUpload
    {
        get
        {
            lock (_gate)
                return _availableUploads.Files.Count;
        }
    }

    /// <summary>SetUploadSendParameters: the priority and ordering channel of the files this system uploads.</summary>
    public void SetUploadSendParameters(Priority priority, byte orderingChannel)
    {
        lock (_gate)
        {
            _priority = priority;
            _orderingChannel = orderingChannel;
        }
    }

    /// <summary>
    /// AddUploadsFromSubdirectory (DirectoryDeltaTransfer.cpp:169-172): offers every file under
    /// <paramref name="subdirectory"/> of the application directory, recursively, by name and hash.
    /// </summary>
    public void AddUploadsFromSubdirectory(string subdirectory)
    {
        ArgumentNullException.ThrowIfNull(subdirectory);
        lock (_gate)
            _availableUploads.AddFilesFromDirectory(_applicationDirectory, subdirectory, writeHash: true, writeData: false, recursive: true, default);
    }

    /// <summary>
    /// DownloadFromSubdirectory, hashing the local files first (DirectoryDeltaTransfer.cpp:220-226). Blocks while they
    /// are hashed.
    /// </summary>
    /// <inheritdoc cref="DownloadFromSubdirectory(FileList, string, string, bool, SystemAddress, IFileListTransferHandler?, Priority, byte, IFileListProgress?)"/>
    public ushort DownloadFromSubdirectory(string subdirectory, string outputSubdirectory, bool prependAppDirToOutputSubdir, SystemAddress host,
                                           IFileListTransferHandler? onFileCallback, Priority priority, byte orderingChannel, IFileListProgress? progress)
    {
        var localFiles = new FileList();
        GenerateHashes(localFiles, outputSubdirectory, prependAppDirToOutputSubdir);
        return DownloadFromSubdirectory(localFiles, subdirectory, outputSubdirectory, prependAppDirToOutputSubdir, host, onFileCallback, priority, orderingChannel, progress);
    }

    /// <summary>
    /// DownloadFromSubdirectory with the local files already hashed (DirectoryDeltaTransfer.cpp:173-219): asks
    /// <paramref name="host"/> for the files of <paramref name="subdirectory"/> that <paramref name="localFiles"/> lacks.
    /// </summary>
    /// <param name="localFiles">The files already here, from <see cref="GenerateHashes"/>.</param>
    /// <param name="subdirectory">A subdirectory <paramref name="host"/> offers, or one below it.</param>
    /// <param name="outputSubdirectory">Where to write the files; usually <paramref name="subdirectory"/>.</param>
    /// <param name="prependAppDirToOutputSubdir">Whether <paramref name="outputSubdirectory"/> is under the application directory.</param>
    /// <param name="host">The system to download from.</param>
    /// <param name="onFileCallback">Gets each file and the progress, after it is written.</param>
    /// <param name="priority">The request's priority.</param>
    /// <param name="orderingChannel">The request's ordering channel.</param>
    /// <param name="progress">Added to <paramref name="localFiles"/>' progress callbacks.</param>
    /// <returns>The set ID, or <see cref="Plugins.FileListTransfer.InvalidSetId"/> when not connected to <paramref name="host"/>; the request is sent either way, as RakNet does.</returns>
    /// <exception cref="InvalidOperationException">No <see cref="FileListTransfer"/> is set, or the plugin is not attached.</exception>
    public ushort DownloadFromSubdirectory(FileList localFiles, string subdirectory, string outputSubdirectory, bool prependAppDirToOutputSubdir, SystemAddress host,
                                           IFileListTransferHandler? onFileCallback, Priority priority, byte orderingChannel, IFileListProgress? progress)
    {
        ArgumentNullException.ThrowIfNull(localFiles);
        ArgumentNullException.ThrowIfNull(subdirectory);
        ArgumentNullException.ThrowIfNull(outputSubdirectory);
        lock (_gate)
        {
            var transfer = _fileListTransfer ?? throw new InvalidOperationException("Set FileListTransfer before downloading.");
            var pluginHost = _host ?? throw new InvalidOperationException("The DirectoryDeltaTransfer is not attached to a peer.");
            localFiles.AddCallback(progress);

            var subdirLength = Encoding.UTF8.GetByteCount(subdirectory);
            if (subdirLength > 0 && !FileOperations.IsSlash(subdirectory[^1]))
                subdirLength++;
            var output = (prependAppDirToOutputSubdir ? _applicationDirectory : string.Empty) + outputSubdirectory;
            if (output.Length == 0 || !FileOperations.IsSlash(output[^1]))
                output += "/";

            var setId = transfer.SetupReceive(new DownloadHandler(subdirLength, output, onFileCallback), host);

            using var request = new BitStream();
            var writer = request.BeginWrite(64);
            writer.WriteByte((byte)MessageId.DdtDownloadRequest);
            writer.WriteUInt16(setId);
            request.EndWrite(writer);
            WriteCompressedString(request, subdirectory);
            WriteCompressedString(request, outputSubdirectory);
            localFiles.Serialize(request);
            pluginHost.Send(request, host, Reliability.ReliableOrdered, priority, orderingChannel);
            return setId;
        }
    }

    /// <summary>
    /// GenerateHashes (DirectoryDeltaTransfer.cpp:227-230): adds the hashes of the files already under
    /// <paramref name="outputSubdirectory"/> to <paramref name="localFiles"/>, for <see cref="DownloadFromSubdirectory(FileList, string, string, bool, SystemAddress, IFileListTransferHandler?, Priority, byte, IFileListProgress?)"/>.
    /// </summary>
    public void GenerateHashes(FileList localFiles, string outputSubdirectory, bool prependAppDirToOutputSubdir)
    {
        ArgumentNullException.ThrowIfNull(localFiles);
        string? applicationDirectory;
        lock (_gate)
            applicationDirectory = prependAppDirToOutputSubdir ? _applicationDirectory : null;
        localFiles.AddFilesFromDirectory(applicationDirectory, outputSubdirectory, writeHash: true, writeData: false, recursive: true, default);
    }

    /// <summary>ClearUploads: offers no files.</summary>
    public void ClearUploads()
    {
        lock (_gate)
            _availableUploads.Clear();
    }

    /// <summary>
    /// SetDownloadRequestIncrementalReadInterface: reads uploaded files a chunk of <paramref name="chunkSize"/> bytes at a
    /// time with <paramref name="reader"/>, rather than loading each whole; null loads them whole.
    /// </summary>
    public void SetDownloadRequestIncrementalReader(IIncrementalReader? reader, uint chunkSize)
    {
        lock (_gate)
        {
            _incrementalReader = reader;
            _chunkSize = chunkSize;
        }
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnReceive (DirectoryDeltaTransfer.cpp:232-242).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || data[0] != (byte)MessageId.DdtDownloadRequest)
            return PluginResult.Continue;
        OnDownloadRequest(e.GetReader(), e.Address);
        return PluginResult.Consume;
    }

    // OnDownloadRequest (DirectoryDeltaTransfer.cpp:204-231).
    private void OnDownloadRequest(BitStreamReader reader, SystemAddress sender)
    {
        reader.IgnoreBits(8);
        if (!reader.TryReadUInt16(out var setId) || !reader.TryReadCompressedString(MaxSubdirectoryLength, out var subdirectory)
            || !reader.TryReadCompressedString(MaxSubdirectoryLength, out var remoteSubdirectory))
            return;
        var remoteFileHashes = new FileList();
        if (!remoteFileHashes.TryDeserialize(ref reader))
            return;

        lock (_gate)
        {
            if (_fileListTransfer is not { } transfer || _chunkSize == 0)
                return;
            var delta = new FileList();
            _availableUploads.GetDeltaToCurrent(remoteFileHashes, delta, subdirectory, remoteSubdirectory);
            if (_incrementalReader is null)
                delta.PopulateDataFromDisk(_applicationDirectory, writeFileData: true, writeFileHash: false, removeUnknownFiles: true);
            else
                delta.FlagFilesAsReferences();
            // This calls the handler passed to SetupReceive on the remote system.
            transfer.Send(delta, sender, setId, _priority, _orderingChannel, _incrementalReader, _chunkSize);
        }
    }

    private static void WriteCompressedString(BitStream stream, string value)
    {
        var writer = stream.BeginWrite(8 + Math.Min(Encoding.UTF8.GetByteCount(value), MaxSubdirectoryLength) * 32);
        writer.WriteCompressedString(value.AsSpan(), MaxSubdirectoryLength);
        stream.EndWrite(writer);
    }

    /// <summary>
    /// DDTCallback (DirectoryDeltaTransfer.cpp:32-77): writes each received file under the output directory, then passes
    /// it on.
    /// </summary>
    private sealed class DownloadHandler(int subdirLength, string outputSubdirectory, IFileListTransferHandler? onFileCallback) : IFileListTransferHandler
    {
        public void OnFile(ReceivedFile file)
        {
            var name = Encoding.UTF8.GetBytes(file.FileName);
            if (file.FileData is { } data && subdirLength < name.Length
                && FileOperations.TryGetContainedPath(outputSubdirectory, Encoding.UTF8.GetString(name.AsSpan(subdirLength)), out var path))
                FileOperations.WriteFileWithDirectories(path, data.AsSpan(0, (int)Math.Min(file.ByteLengthOfThisFile, (uint)data.Length)));
            onFileCallback?.OnFile(file);
        }

        public void OnFileProgress(FileProgress progress) => onFileCallback?.OnFileProgress(progress);

        public bool OnDownloadComplete(DownloadComplete complete) => onFileCallback?.OnDownloadComplete(complete) ?? false;
    }
}
