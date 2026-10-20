using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// FileListTransferCBInterface (FileListTransferCBInterface.h): receives the files of one set that
/// <see cref="FileListTransfer.SetupReceive"/> allowed. Every method runs on the dispatch step.
/// </summary>
public interface IFileListTransferHandler
{
    /// <summary>
    /// OnFile: a whole file arrived. The last file of a set has <see cref="ReceivedFile.FileIndex"/> + 1 equal to
    /// <see cref="ReceivedFile.NumberOfFilesInThisSet"/> when files arrive in order. RakNet's return value only decides
    /// who frees the memory, and has no counterpart here.
    /// </summary>
    void OnFile(ReceivedFile file);

    /// <summary>
    /// OnFileProgress: part of a file arrived, either a chunk read on the other system by an
    /// <see cref="IIncrementalReader"/> (<see cref="FileProgress.IriDataChunk"/> holds it) or a part of a large message
    /// reported by the peer's split message progress (<see cref="RakPeer.SplitMessageProgressInterval"/>); a file sent
    /// whole also reports one progress of one part before <see cref="OnFile"/>.
    /// </summary>
    void OnFileProgress(FileProgress progress);

    /// <summary>Update: runs on every plugin update while the handler is active; false to end it.</summary>
    bool Update() => true;

    /// <summary>OnDownloadComplete: every file of the set arrived. False, the default, ends the handler; true keeps it.</summary>
    bool OnDownloadComplete(DownloadComplete complete) => false;

    /// <summary>OnDereference: the handler is no longer used by the <see cref="FileListTransfer"/>.</summary>
    void OnDereference() { }
}

/// <summary>OnFileStruct (FileListTransferCBInterface.h:35-74): a file, or the part of one that has arrived.</summary>
public sealed class ReceivedFile
{
    internal ReceivedFile() { }

    /// <summary>fileIndex: the file's index in its set.</summary>
    public uint FileIndex { get; internal set; }

    /// <summary>fileName</summary>
    public string FileName { get; internal set; } = string.Empty;

    /// <summary>
    /// fileData: the file's bytes; while a file sent incrementally arrives, the buffer it is assembled in. Null when the
    /// handler declined that buffer (<see cref="FileProgress.AllocateIriDataChunkAutomatically"/>).
    /// </summary>
    public byte[]? FileData { get; internal set; }

    /// <summary>byteLengthOfThisFile</summary>
    public uint ByteLengthOfThisFile { get; internal set; }

    /// <summary>bytesDownloadedForThisFile</summary>
    public uint BytesDownloadedForThisFile { get; internal set; }

    /// <summary>setID</summary>
    public ushort SetId { get; internal set; }

    /// <summary>numberOfFilesInThisSet</summary>
    public uint NumberOfFilesInThisSet { get; internal set; }

    /// <summary>byteLengthOfThisSet</summary>
    public uint ByteLengthOfThisSet { get; internal set; }

    /// <summary>bytesDownloadedForThisSet</summary>
    public uint BytesDownloadedForThisSet { get; internal set; }

    /// <summary>context: the user data the sender stored with the file.</summary>
    public FileListNodeContext Context { get; internal set; }

    /// <summary>senderSystemAddress</summary>
    public SystemAddress SenderAddress { get; internal set; }

    /// <summary>senderGuid</summary>
    public RakNetGuid SenderGuid { get; internal set; }
}

/// <summary>FileProgressStruct (FileListTransferCBInterface.h:77-99). Valid for the callback only.</summary>
public sealed class FileProgress
{
    internal FileProgress(ReceivedFile file) => File = file;

    /// <summary>onFileStruct: the file this part belongs to.</summary>
    public ReceivedFile File { get; }

    /// <summary>partCount: the zero-based index of this part; the file is 100 * (PartCount + 1) / PartTotal percent done.</summary>
    public uint PartCount { get; internal set; }

    /// <summary>partTotal</summary>
    public uint PartTotal { get; internal set; }

    /// <summary>
    /// dataChunkLength: the length RakNet reports for the chunk. For a part reported by split message progress of a file
    /// sent incrementally it counts a partly read byte, so it can be one more than the chunk's bytes.
    /// </summary>
    public uint DataChunkLength { get; internal set; }

    /// <summary>firstDataChunk: the start of the file as far as it is known, or empty.</summary>
    public ReadOnlyMemory<byte> FirstDataChunk { get; internal set; }

    /// <summary>iriDataChunk: the chunk of a file sent incrementally that just arrived, or empty.</summary>
    public ReadOnlyMemory<byte> IriDataChunk { get; internal set; }

    /// <summary>Whether <see cref="IriDataChunk"/> is set, where RakNet's pointer is non-null.</summary>
    public bool HasIriDataChunk { get; internal set; }

    /// <summary>iriWriteOffset: where <see cref="IriDataChunk"/> starts in the file.</summary>
    public uint IriWriteOffset { get; internal set; }

    /// <summary>senderSystemAddress</summary>
    public SystemAddress SenderAddress { get; internal set; }

    /// <summary>senderGuid</summary>
    public RakNetGuid SenderGuid { get; internal set; }

    /// <summary>
    /// allocateIrIDataChunkAutomatically: true, the default, to have the chunks of a file sent incrementally assembled and
    /// passed to <see cref="IFileListTransferHandler.OnFile"/>; set false on the first chunk to keep each chunk yourself.
    /// </summary>
    public bool AllocateIriDataChunkAutomatically { get; set; } = true;
}

/// <summary>DownloadCompleteStruct (FileListTransferCBInterface.h:101-118).</summary>
public readonly record struct DownloadComplete(ushort SetId, uint NumberOfFilesInThisSet, uint ByteLengthOfThisSet, SystemAddress SenderAddress, RakNetGuid SenderGuid);
