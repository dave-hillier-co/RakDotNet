using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The fileListTransfer* and ddt* commands on a <see cref="FileListTransfer"/> and a <see cref="DirectoryDeltaTransfer"/>,
/// with interop-peer's results, records and errors.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IFileTransferDriver
{
    // Files up to this size are reported with their bytes as well as their hash, as interop-peer does.
    private const uint MaxReportedDataBytes = 1024;

    private readonly FileRecords _fileRecords = new();
    private FileListTransfer? _fileListTransfer;
    private DirectoryDeltaTransfer? _directoryDeltaTransfer;
    private RecordingFileHandler? _downloadHandler;

    public async Task AttachFileListTransferAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("fileListTransferAttach");
        if (_fileListTransfer is not null)
            return;
        _fileListTransfer = new FileListTransfer();
        _fileListTransfer.AddCallback(new RecordingSendProgress(_fileRecords));
        await AttachAsync(peer, _fileListTransfer, cancellationToken).ConfigureAwait(false);
    }

    public Task StartFileListTransferThreadsAsync(int count, CancellationToken cancellationToken = default)
    {
        RequireFileListTransfer().StartIncrementalReadThreads(count);
        return Task.CompletedTask;
    }

    public Task<ushort> SetupFileReceiveAsync(ulong sender, bool allocateIriDataChunk = true, CancellationToken cancellationToken = default)
    {
        var transfer = RequireFileListTransfer();
        var handler = new RecordingFileHandler(_fileRecords, allocateIriDataChunk);
        var setId = transfer.SetupReceive(handler, Started("fileListTransferSetupReceive").GetAddressFromGuid(new RakNetGuid(sender)));
        handler.SetId = setId == FileListTransfer.InvalidSetId ? null : setId;
        return Task.FromResult(setId);
    }

    public Task SendFilesAsync(ulong target, ushort setId, IReadOnlyList<FileToSend> files, Priority priority = Priority.High, byte channel = 0,
                               bool incremental = false, uint? chunkSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var transfer = RequireFileListTransfer();
        var address = Connection("fileListTransferSend", target).Address;
        var list = new FileList();
        foreach (var file in files)
        {
            var path = file.Path ?? file.Name;
            if (file.Reference)
            {
                // Samples/FileListTransfer: a reference with its length, read when sent.
                var length = file.FileLength ?? FileOperations.GetFileLength(path);
                list.AddFile(file.Name, path, [], length, file.Context, isAReference: true, dataLength: length);
            }
            else
            {
                list.AddFile(file.Name, path, file.Data.Span, file.FileLength ?? (uint)file.Data.Length, file.Context);
            }
        }
        transfer.Send(list, address, setId, priority, channel, incremental ? new FileIncrementalReader() : null, chunkSize ?? FileListTransfer.DefaultChunkSize);
        return Task.CompletedTask;
    }

    public Task CancelFileReceiveAsync(ushort setId, CancellationToken cancellationToken = default)
    {
        RequireFileListTransfer().CancelReceive(setId);
        return Task.CompletedTask;
    }

    public Task<FileTransferInfo> GetFileTransferInfoAsync(ushort? setId = null, ulong? target = null, CancellationToken cancellationToken = default)
    {
        var transfer = RequireFileListTransfer();
        bool? active = setId is { } id ? transfer.IsHandlerActive(id) : null;
        uint? pending = target is { } guid ? transfer.GetPendingFilesToAddress(Started("fileListTransferInfo").GetAddressFromGuid(new RakNetGuid(guid))) : null;
        lock (_fileRecords)
        {
            return Task.FromResult(new FileTransferInfo(
                [.. _fileRecords.Files], [.. _fileRecords.Progress], [.. _fileRecords.Completed], [.. _fileRecords.Dereferenced],
                [.. _fileRecords.Pushes], [.. _fileRecords.PushesComplete], [.. _fileRecords.SendAborted], active, pending));
        }
    }

    public async Task AttachDirectoryDeltaTransferAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("ddtAttach");
        if (_directoryDeltaTransfer is not null)
            return;
        await AttachFileListTransferAsync(cancellationToken).ConfigureAwait(false);
        _directoryDeltaTransfer = new DirectoryDeltaTransfer();
        await AttachAsync(peer, _directoryDeltaTransfer, cancellationToken).ConfigureAwait(false);
        _directoryDeltaTransfer.FileListTransfer = _fileListTransfer;
    }

    public Task SetDdtApplicationDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        RequireDirectoryDeltaTransfer().ApplicationDirectory = path;
        return Task.CompletedTask;
    }

    public Task<int> AddDdtUploadsAsync(string subdirectory, CancellationToken cancellationToken = default)
    {
        var ddt = RequireDirectoryDeltaTransfer();
        ddt.AddUploadsFromSubdirectory(subdirectory);
        return Task.FromResult(ddt.NumberOfFilesForUpload);
    }

    public Task ClearDdtUploadsAsync(CancellationToken cancellationToken = default)
    {
        RequireDirectoryDeltaTransfer().ClearUploads();
        return Task.CompletedTask;
    }

    public Task SetDdtIncrementalReadAsync(uint chunkSize, CancellationToken cancellationToken = default)
    {
        RequireDirectoryDeltaTransfer().SetDownloadRequestIncrementalReader(chunkSize == 0 ? null : new FileIncrementalReader(), chunkSize);
        return Task.CompletedTask;
    }

    public Task<ushort> DdtDownloadAsync(string subdirectory, string outputSubdirectory, bool prependAppDir, ulong target, Priority priority = Priority.High,
                                         byte channel = 0, CancellationToken cancellationToken = default)
    {
        var ddt = RequireDirectoryDeltaTransfer();
        var host = Connection("ddtDownload", target).Address;
        _downloadHandler ??= new RecordingFileHandler(_fileRecords, allocateIriDataChunk: true);
        return Task.FromResult(ddt.DownloadFromSubdirectory(subdirectory, outputSubdirectory, prependAppDir, host, _downloadHandler, priority, channel, null));
    }

    // interop-peer's RequireFileListTransfer and RequireDirectoryDeltaTransfer.
    private FileListTransfer RequireFileListTransfer() =>
        _fileListTransfer ?? throw new PeerCommandException("fileListTransfer", "no FileListTransfer is attached; send fileListTransferAttach first");

    private DirectoryDeltaTransfer RequireDirectoryDeltaTransfer() =>
        _directoryDeltaTransfer ?? throw new PeerCommandException("ddt", "no DirectoryDeltaTransfer is attached; send ddtAttach first");

    /// <summary>What the handlers and the progress callback reported; locked on itself, as the pump thread writes it.</summary>
    private sealed class FileRecords
    {
        public List<FileRecord> Files { get; } = [];

        public List<ProgressRecord> Progress { get; } = [];

        public List<CompletedRecord> Completed { get; } = [];

        public List<ushort> Dereferenced { get; } = [];

        public List<PushRecord> Pushes { get; } = [];

        public List<ushort> PushesComplete { get; } = [];

        public List<string> SendAborted { get; } = [];
    }

    private sealed class RecordingFileHandler(FileRecords records, bool allocateIriDataChunk) : IFileListTransferHandler
    {
        public ushort? SetId { get; set; }

        public void OnFile(ReceivedFile file)
        {
            uint? hash = null;
            byte[]? data = null;
            if (file.FileData is { } fileData)
            {
                var bytes = fileData.AsSpan(0, (int)Math.Min(file.ByteLengthOfThisFile, (uint)fileData.Length));
                hash = SuperFastHash.Hash(bytes);
                if (file.ByteLengthOfThisFile <= MaxReportedDataBytes)
                    data = bytes.ToArray();
            }
            var record = new FileRecord(file.SetId, file.FileIndex, file.FileName, file.ByteLengthOfThisFile, file.BytesDownloadedForThisFile,
                                        file.NumberOfFilesInThisSet, file.ByteLengthOfThisSet, file.BytesDownloadedForThisSet, file.Context,
                                        file.SenderGuid.Value, file.FileData is not null, hash, data);
            lock (records)
                records.Files.Add(record);
        }

        public void OnFileProgress(FileProgress progress)
        {
            var file = progress.File;
            uint? iriHash = null;
            if (progress.HasIriDataChunk)
            {
                var chunk = progress.IriDataChunk.Span;
                iriHash = SuperFastHash.Hash(chunk[..(int)Math.Min(progress.DataChunkLength, (uint)chunk.Length)]);
            }
            progress.AllocateIriDataChunkAutomatically = allocateIriDataChunk;
            var record = new ProgressRecord(file.SetId, file.FileIndex, file.FileName, progress.PartCount, progress.PartTotal, progress.DataChunkLength,
                                            progress.IriWriteOffset, file.BytesDownloadedForThisFile, file.BytesDownloadedForThisSet, progress.HasIriDataChunk, iriHash);
            lock (records)
                records.Progress.Add(record);
        }

        public bool OnDownloadComplete(DownloadComplete complete)
        {
            lock (records)
                records.Completed.Add(new CompletedRecord(complete.SetId, complete.NumberOfFilesInThisSet, complete.ByteLengthOfThisSet, complete.SenderGuid.Value));
            return false;
        }

        public void OnDereference()
        {
            if (SetId is not { } setId)
                return;
            lock (records)
                records.Dereferenced.Add(setId);
        }
    }

    private sealed class RecordingSendProgress(FileRecords records) : IFileListProgress
    {
        public void OnFilePush(string fileName, uint fileLengthBytes, uint offset, uint bytesBeingSent, bool done, SystemAddress targetSystem, ushort setId)
        {
            lock (records)
                records.Pushes.Add(new PushRecord(fileName, fileLengthBytes, offset, bytesBeingSent, done, setId));
        }

        public void OnFilePushesComplete(SystemAddress systemAddress, ushort setId)
        {
            lock (records)
                records.PushesComplete.Add(setId);
        }

        public void OnSendAborted(SystemAddress systemAddress)
        {
            lock (records)
                records.SendAborted.Add(systemAddress.ToString(writePort: true, ':'));
        }
    }
}
