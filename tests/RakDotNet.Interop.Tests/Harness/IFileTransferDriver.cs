using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a FileListTransfer and a DirectoryDeltaTransfer on a peer: the fileListTransfer* and ddt* commands
/// of docs/interop-protocol.md. Both drivers implement it; a command fails until its plugin is attached. What the
/// receive handlers and the send progress callback report is read with <see cref="GetFileTransferInfoAsync"/>.
/// </summary>
public interface IFileTransferDriver
{
    /// <summary>AttachPlugin of a FileListTransfer with a FileListProgress that records the sends.</summary>
    Task AttachFileListTransferAsync(CancellationToken cancellationToken = default);

    /// <summary>StartIncrementalReadThreads</summary>
    Task StartFileListTransferThreadsAsync(int count, CancellationToken cancellationToken = default);

    /// <summary>SetupReceive for the system with the GUID, with a recording handler.</summary>
    Task<ushort> SetupFileReceiveAsync(ulong sender, bool allocateIriDataChunk = true, CancellationToken cancellationToken = default);

    /// <summary>Send of a FileList built from <paramref name="files"/>.</summary>
    Task SendFilesAsync(ulong target, ushort setId, IReadOnlyList<FileToSend> files, Priority priority = Priority.High, byte channel = 0,
                        bool incremental = false, uint? chunkSize = null, CancellationToken cancellationToken = default);

    /// <summary>CancelReceive</summary>
    Task CancelFileReceiveAsync(ushort setId, CancellationToken cancellationToken = default);

    /// <summary>Everything recorded so far, with IsHandlerActive for <paramref name="setId"/> and GetPendingFilesToAddress for <paramref name="target"/>.</summary>
    Task<FileTransferInfo> GetFileTransferInfoAsync(ushort? setId = null, ulong? target = null, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a DirectoryDeltaTransfer, and of the FileListTransfer it uses when none is attached.</summary>
    Task AttachDirectoryDeltaTransferAsync(CancellationToken cancellationToken = default);

    /// <summary>SetApplicationDirectory</summary>
    Task SetDdtApplicationDirectoryAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>AddUploadsFromSubdirectory, returning GetNumberOfFilesForUpload.</summary>
    Task<int> AddDdtUploadsAsync(string subdirectory, CancellationToken cancellationToken = default);

    /// <summary>ClearUploads</summary>
    Task ClearDdtUploadsAsync(CancellationToken cancellationToken = default);

    /// <summary>SetDownloadRequestIncrementalReadInterface with RakNet's file reader, or none for a chunk size of 0.</summary>
    Task SetDdtIncrementalReadAsync(uint chunkSize, CancellationToken cancellationToken = default);

    /// <summary>DownloadFromSubdirectory from the system with the GUID, with a recording handler.</summary>
    Task<ushort> DdtDownloadAsync(string subdirectory, string outputSubdirectory, bool prependAppDir, ulong target, Priority priority = Priority.High,
                                  byte channel = 0, CancellationToken cancellationToken = default);
}

/// <summary>A file of a list to send: its bytes, or with <paramref name="Reference"/> the file at <paramref name="Path"/>.</summary>
public sealed record FileToSend(string Name, ReadOnlyMemory<byte> Data = default, string? Path = null, bool Reference = false,
                                uint? FileLength = null, FileListNodeContext Context = default);

/// <summary>What OnFile reported; <see cref="Data"/> only for files of up to 1024 bytes.</summary>
public sealed record FileRecord(ushort SetId, uint FileIndex, string FileName, uint ByteLengthOfThisFile, uint BytesDownloadedForThisFile,
                                uint NumberOfFilesInThisSet, uint ByteLengthOfThisSet, uint BytesDownloadedForThisSet, FileListNodeContext Context,
                                ulong SenderGuid, bool HasData, uint? Hash, byte[]? Data);

/// <summary>What OnFileProgress reported.</summary>
public sealed record ProgressRecord(ushort SetId, uint FileIndex, string FileName, uint PartCount, uint PartTotal, uint DataChunkLength,
                                    uint IriWriteOffset, uint BytesDownloadedForThisFile, uint BytesDownloadedForThisSet, bool HasIriDataChunk, uint? IriHash);

/// <summary>What OnDownloadComplete reported.</summary>
public sealed record CompletedRecord(ushort SetId, uint NumberOfFilesInThisSet, uint ByteLengthOfThisSet, ulong SenderGuid);

/// <summary>What OnFilePush reported.</summary>
public sealed record PushRecord(string FileName, uint FileLengthBytes, uint Offset, uint BytesBeingSent, bool Done, ushort SetId);

/// <summary>The fileListTransferInfo reply.</summary>
public sealed record FileTransferInfo(
    IReadOnlyList<FileRecord> Files,
    IReadOnlyList<ProgressRecord> Progress,
    IReadOnlyList<CompletedRecord> Completed,
    IReadOnlyList<ushort> Dereferenced,
    IReadOnlyList<PushRecord> Pushes,
    IReadOnlyList<ushort> PushesComplete,
    IReadOnlyList<string> SendAborted,
    bool? HandlerActive,
    uint? PendingFiles);
