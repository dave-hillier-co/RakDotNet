using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.FileListTransfer;

/// <summary>What a handler saw of a file, copied when it was called.</summary>
internal sealed record FileSeen(ushort SetId, uint FileIndex, string FileName, byte[]? FileData, uint ByteLengthOfThisFile, uint BytesDownloadedForThisFile,
                                uint NumberOfFilesInThisSet, uint ByteLengthOfThisSet, uint BytesDownloadedForThisSet, FileListNodeContext Context, SystemAddress Sender)
{
    public static FileSeen From(ReceivedFile f) => new(f.SetId, f.FileIndex, f.FileName, f.FileData?.ToArray(), f.ByteLengthOfThisFile, f.BytesDownloadedForThisFile,
                                                        f.NumberOfFilesInThisSet, f.ByteLengthOfThisSet, f.BytesDownloadedForThisSet, f.Context, f.SenderAddress);
}

/// <summary>What a handler saw of a progress report, copied when it was called.</summary>
internal sealed record ProgressSeen(FileSeen File, uint PartCount, uint PartTotal, uint DataChunkLength, byte[] FirstDataChunk, byte[]? IriDataChunk, uint IriWriteOffset);

/// <summary>An <see cref="IFileListTransferHandler"/> that records every call.</summary>
internal sealed class RecordingHandler : IFileListTransferHandler
{
    public List<FileSeen> Files { get; } = [];

    public List<ProgressSeen> Progress { get; } = [];

    public List<DownloadComplete> Completed { get; } = [];

    public int Dereferenced { get; private set; }

    /// <summary>What <see cref="OnDownloadComplete"/> returns.</summary>
    public bool KeepAfterComplete { get; set; }

    /// <summary>What <see cref="Update"/> returns.</summary>
    public bool KeepUpdating { get; set; } = true;

    /// <summary>Set on every progress report.</summary>
    public bool AllocateIriDataChunkAutomatically { get; set; } = true;

    public void OnFile(ReceivedFile file) => Files.Add(FileSeen.From(file));

    public void OnFileProgress(FileProgress progress)
    {
        Progress.Add(new ProgressSeen(FileSeen.From(progress.File), progress.PartCount, progress.PartTotal, progress.DataChunkLength,
                                      progress.FirstDataChunk.ToArray(), progress.HasIriDataChunk ? progress.IriDataChunk.ToArray() : null, progress.IriWriteOffset));
        progress.AllocateIriDataChunkAutomatically = AllocateIriDataChunkAutomatically;
    }

    public bool Update() => KeepUpdating;

    public bool OnDownloadComplete(DownloadComplete complete)
    {
        Completed.Add(complete);
        return KeepAfterComplete;
    }

    public void OnDereference() => Dereferenced++;
}

/// <summary>An <see cref="IFileListProgress"/> that records a sender's reports.</summary>
internal sealed class RecordingSendProgress : IFileListProgress
{
    public List<(string FileName, uint FileLength, uint Offset, uint Bytes, bool Done, ushort SetId)> Pushes { get; } = [];

    public List<(SystemAddress Address, ushort SetId)> PushesComplete { get; } = [];

    public List<SystemAddress> Aborted { get; } = [];

    public void OnFilePush(string fileName, uint fileLengthBytes, uint offset, uint bytesBeingSent, bool done, SystemAddress targetSystem, ushort setId) =>
        Pushes.Add((fileName, fileLengthBytes, offset, bytesBeingSent, done, setId));

    public void OnFilePushesComplete(SystemAddress systemAddress, ushort setId) => PushesComplete.Add((systemAddress, setId));

    public void OnSendAborted(SystemAddress systemAddress) => Aborted.Add(systemAddress);
}
