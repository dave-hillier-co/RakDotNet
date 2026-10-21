using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// FileListProgress (FileList.h:69-130): progress reports from <see cref="FileList.AddFilesFromDirectory"/> and from a
/// <see cref="FileListTransfer"/> sending files. Every method is optional.
/// </summary>
/// <remarks>
/// A sender's reports run where its sends run: on the caller of <see cref="FileListTransfer.Send"/>, on the dispatch
/// step when an acknowledgement asks for the next chunk, or on an incremental read worker once
/// <see cref="FileListTransfer.StartIncrementalReadThreads"/> has started them.
/// </remarks>
public interface IFileListProgress
{
    /// <summary>OnAddFilesFromDirectoryStarted: the first report of <see cref="FileList.AddFilesFromDirectory"/>.</summary>
    void OnAddFilesFromDirectoryStarted(FileList fileList, string directory) { }

    /// <summary>OnDirectory: a directory, given as its path then "*", begins; <paramref name="directoriesRemaining"/> are queued.</summary>
    void OnDirectory(FileList fileList, string directory, uint directoriesRemaining) { }

    /// <summary>OnFile: a file of <paramref name="directory"/> begins.</summary>
    void OnFile(FileList fileList, string directory, string fileName, uint fileSize) { }

    /// <summary>
    /// OnFilePush: a chunk of a file sent incrementally, <paramref name="bytesBeingSent"/> bytes from
    /// <paramref name="offset"/>; <paramref name="done"/> on its last chunk.
    /// </summary>
    void OnFilePush(string fileName, uint fileLengthBytes, uint offset, uint bytesBeingSent, bool done, SystemAddress targetSystem, ushort setId) { }

    /// <summary>OnFilePushesComplete: every file of the set has been read and handed to the peer.</summary>
    void OnFilePushesComplete(SystemAddress systemAddress, ushort setId) { }

    /// <summary>OnSendAborted: the files still to send to a system were dropped, as when it disconnected.</summary>
    void OnSendAborted(SystemAddress systemAddress) { }
}

/// <summary>
/// FLP_Printf (FileList.cpp:84-106): writes RakNet's progress lines to a <see cref="TextWriter"/>, standard output by
/// default.
/// </summary>
public sealed class FileListProgressPrinter(TextWriter? writer = null) : IFileListProgress
{
    private readonly TextWriter _writer = writer ?? Console.Out;

    public void OnAddFilesFromDirectoryStarted(FileList fileList, string directory) =>
        _writer.WriteLine($"Adding files from directory {directory}");

    public void OnDirectory(FileList fileList, string directory, uint directoriesRemaining) =>
        _writer.WriteLine($"Adding {directory}. {directoriesRemaining} remaining.");

    public void OnFilePushesComplete(SystemAddress systemAddress, ushort setId) =>
        _writer.WriteLine($"File pushes complete to {systemAddress.ToString(writePort: true)}");

    public void OnSendAborted(SystemAddress systemAddress) =>
        _writer.WriteLine($"Send aborted to {systemAddress.ToString(writePort: true)}");
}
