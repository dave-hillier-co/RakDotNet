using System.Buffers.Binary;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>FileListNode (FileList.h:44-67): one file of a <see cref="FileList"/>.</summary>
/// <remarks>
/// <see cref="Data"/> may hold more bytes than <see cref="DataLengthBytes"/>, or none while it is non-zero, as RakNet's
/// buffer can: a reference (<see cref="IsAReference"/>) has a length and no data, and
/// <see cref="FileList.PopulateDataFromDisk"/> with both data and hash leaves the length as it was.
/// </remarks>
public sealed class FileListNode
{
    internal FileListNode(string filename, string fullPathToFile, byte[]? data, uint dataLengthBytes, uint fileLengthBytes, FileListNodeContext context, bool isAReference)
    {
        Filename = filename;
        FullPathToFile = fullPathToFile;
        Data = data;
        DataLengthBytes = dataLengthBytes;
        FileLengthBytes = fileLengthBytes;
        Context = context;
        IsAReference = isAReference;
    }

    /// <summary>filename: the name, optionally prefixed with a partial path.</summary>
    public string Filename { get; }

    /// <summary>fullPathToFile: where the file is on disk, which may differ from <see cref="Filename"/>.</summary>
    public string FullPathToFile { get; }

    /// <summary>data: the contents, a hash, or a hash followed by the contents; null when not loaded.</summary>
    public byte[]? Data { get; internal set; }

    /// <summary>dataLengthBytes: the length of <see cref="Data"/> as sent; more than the file's when a hash is prepended.</summary>
    public uint DataLengthBytes { get; internal set; }

    /// <summary>fileLengthBytes: the length of the file.</summary>
    public uint FileLengthBytes { get; internal set; }

    /// <summary>context: user data sent with the file.</summary>
    public FileListNodeContext Context { get; internal set; }

    /// <summary>isAReference: only the name is held, and the contents are read when sent.</summary>
    public bool IsAReference { get; internal set; }

    /// <summary>The first <see cref="DataLengthBytes"/> bytes of <see cref="Data"/>, zero-filled past its end.</summary>
    internal byte[] SentData()
    {
        var data = Data ?? [];
        if (data.Length == DataLengthBytes)
            return data;
        var sent = new byte[DataLengthBytes];
        data.AsSpan(0, (int)Math.Min(DataLengthBytes, (uint)data.Length)).CopyTo(sent);
        return sent;
    }
}

/// <summary>
/// FileList (FileList.cpp): a set of files, each with its contents, a hash of them, or only its name and length.
/// <see cref="FileListTransfer"/> sends one, and <see cref="DirectoryDeltaTransfer"/> compares two.
/// </summary>
/// <remarks>
/// <para>
/// Names are text, sent as UTF-8 through the StringCompressor; RakNet's C strings are bytes, so a name ends at its first
/// NUL, and lengths, limits and comparisons are on the UTF-8 bytes. Paths use '/' between the directories this class
/// adds, '\' on Windows, as FixEndingSlash does, and either is accepted.
/// </para>
/// <para>
/// A hash is SuperFastHash written big-endian, which is what RakNet writes on every little-endian host
/// (BitStream::DoEndianSwap, FileList.cpp:315-318). Not thread-safe.
/// </para>
/// </remarks>
public sealed class FileList
{
    /// <summary>MAX_FILENAME_LENGTH (FileList.cpp:46).</summary>
    public const int MaxFilenameLength = 512;

    /// <summary>HASH_LENGTH (FileList.cpp:47).</summary>
    public const int HashLength = 4;

    // FileList::Deserialize's sanity limits (FileList.cpp:423, 438).
    private const uint MaxDeserializedFiles = 10000;
    private const uint MaxDeserializedDataLength = 2_000_000_000;

    private readonly List<FileListNode> _files = [];
    private readonly List<IFileListProgress> _callbacks = [];

    /// <summary>fileList: the files, in the order they were added.</summary>
    public IReadOnlyList<FileListNode> Files => _files;

    /// <summary>
    /// AddFile with data in memory (FileList.cpp:163-224). A file of the same name is replaced unless its lengths match
    /// and its data does, or either has none. Names longer than <see cref="MaxFilenameLength"/> bytes are ignored.
    /// </summary>
    /// <param name="filename">The name to store, optionally prefixed with a partial path.</param>
    /// <param name="fullPathToFile">Where the file is on disk.</param>
    /// <param name="data">The contents, possibly prefixed with a hash; empty for none.</param>
    /// <param name="fileLength">The length of the file, which may differ from the data's.</param>
    /// <param name="context">User data stored and sent with the file.</param>
    /// <param name="isAReference">The file is only referenced; it holds no data, and <paramref name="dataLength"/> is what will be read.</param>
    /// <param name="dataLength">The data length to record; the length of <paramref name="data"/> when omitted.</param>
    public void AddFile(string filename, string fullPathToFile, ReadOnlySpan<byte> data, uint fileLength, FileListNodeContext context, bool isAReference = false, uint? dataLength = null)
    {
        ArgumentNullException.ThrowIfNull(filename);
        ArgumentNullException.ThrowIfNull(fullPathToFile);
        filename = CString(filename);
        if (Encoding.UTF8.GetByteCount(filename) > MaxFilenameLength)
            return;
        var length = dataLength ?? (uint)data.Length;
        for (var i = 0; i < _files.Count; i++)
        {
            var existing = _files[i];
            if (!string.Equals(existing.Filename, filename, StringComparison.Ordinal))
                continue;
            if (existing.FileLengthBytes == fileLength && existing.DataLengthBytes == length
                && (length == 0 || existing.Data is null || data.IsEmpty || SameBytes(existing.Data, data, length)))
                return;
            _files.RemoveAt(i);
            break;
        }
        var copy = data.IsEmpty || length == 0 ? null : data.ToArray();
        _files.Add(new FileListNode(filename, fullPathToFile, copy, length, fileLength, context, isAReference));
    }

    /// <summary>
    /// AddFile reading from disk (FileList.cpp:114-162): the whole file at <paramref name="filepath"/>, stored as
    /// <paramref name="filename"/>. Nothing is added when it cannot be read.
    /// </summary>
    public void AddFile(string filepath, string filename, FileListNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(filepath);
        ArgumentNullException.ThrowIfNull(filename);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(FileOperations.Normalize(filepath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return;
        }
        AddFile(filename, filepath, data, (uint)data.Length, context);
    }

    /// <summary>
    /// AddFilesFromDirectory (FileList.cpp:225-378): every file under <paramref name="applicationDirectory"/> followed by
    /// <paramref name="subDirectory"/>, breadth first in the file system's order, stored with names relative to
    /// <paramref name="applicationDirectory"/>.
    /// </summary>
    /// <remarks>
    /// With <paramref name="writeHash"/> and <paramref name="writeData"/> each file holds its hash then its contents; with
    /// only one, that one; with neither, only its name and length. As in RakNet, a directory that cannot be opened ends
    /// the walk, directories still queued included, and hidden and system entries are skipped where the platform marks
    /// them.
    /// </remarks>
    public void AddFilesFromDirectory(string? applicationDirectory, string? subDirectory, bool writeHash, bool writeData, bool recursive, FileListNodeContext context)
    {
        var root = applicationDirectory ?? string.Empty;
        var rootLength = root.Length;
        string start;
        if (rootLength > 0)
        {
            start = FixEndingSlash(root);
            if (start.Length != root.Length)
                rootLength++;
        }
        else
        {
            start = string.Empty;
        }
        if (!string.IsNullOrEmpty(subDirectory))
            start = FixEndingSlash(start + subDirectory);

        foreach (var callback in _callbacks.ToArray())
            callback.OnAddFilesFromDirectoryStarted(this, start);

        Span<byte> hash = stackalloc byte[HashLength];
        var directories = new Queue<string>();
        directories.Enqueue(start);
        while (directories.Count > 0)
        {
            var directory = directories.Dequeue();
            var entries = FindFirst.Find(directory);
            if (entries is null)
                return;
            foreach (var callback in _callbacks.ToArray())
                callback.OnDirectory(this, directory + "*", (uint)directories.Count);

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory && !entry.IsHiddenOrSystem)
                {
                    var fullPath = directory + entry.Name;
                    var size = unchecked((uint)entry.Size);
                    foreach (var callback in _callbacks.ToArray())
                        callback.OnFile(this, directory, entry.Name, size);
                    var name = fullPath[Math.Min(rootLength, fullPath.Length)..];
                    if (writeData && writeHash)
                    {
                        if (TryReadPrefix(fullPath, size, HashLength, out var fileData))
                        {
                            BinaryPrimitives.WriteUInt32BigEndian(fileData, SuperFastHash.Hash(fileData.AsSpan(HashLength)));
                            AddFile(name, fullPath, fileData, size, context);
                        }
                    }
                    else if (writeHash)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(hash, SuperFastHash.HashFile(FileOperations.Normalize(fullPath)));
                        AddFile(name, fullPath, hash, size, context);
                    }
                    else if (writeData)
                    {
                        if (TryReadPrefix(fullPath, size, 0, out var fileData))
                            AddFile(name, fullPath, fileData, size, context);
                    }
                    else
                    {
                        AddFile(name, fullPath, [], size, context);
                    }
                }
                else if (entry.IsDirectory && !entry.IsHiddenOrSystem && recursive)
                {
                    directories.Enqueue(directory + entry.Name + "/");
                }
            }
        }
    }

    /// <summary>Clear: removes every file.</summary>
    public void Clear() => _files.Clear();

    /// <summary>
    /// Serialize (FileList.cpp:388-411): the file count compressed, then per file the context's op and first two extra
    /// values compressed, the name through the StringCompressor, a bit and the data when there is any (its length
    /// compressed, then the bytes), and a bit, followed by the file length compressed when it differs from the data's.
    /// </summary>
    public void Serialize(BitStream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var bound = 8;
        foreach (var node in _files)
            bound += 32 + MaxEncodedNameBytes(node.Filename) + (int)Math.Min(node.DataLengthBytes, int.MaxValue / 4);
        var writer = output.BeginWrite(bound);
        writer.WriteCompressed((uint)_files.Count);
        foreach (var node in _files)
        {
            writer.WriteCompressed(node.Context.Op);
            writer.WriteCompressed(node.Context.ExtraData1);
            writer.WriteCompressed(node.Context.ExtraData2);
            writer.WriteCompressedString(node.Filename.AsSpan(), MaxFilenameLength);
            var writeFileData = node.DataLengthBytes > 0;
            writer.WriteBool(writeFileData);
            if (writeFileData)
            {
                writer.WriteCompressed(node.DataLengthBytes);
                writer.WriteBytes(node.SentData());
            }
            writer.WriteBool(node.FileLengthBytes == node.DataLengthBytes);
            if (node.FileLengthBytes != node.DataLengthBytes)
                writer.WriteCompressed(node.FileLengthBytes);
        }
        output.EndWrite(writer);
    }

    /// <summary>
    /// Deserialize (FileList.cpp:412-474): replaces the files with those <see cref="Serialize"/> wrote. False, with the
    /// list cleared, when the stream is truncated or names more than 10000 files or a data length over 2000000000 bytes;
    /// the third extra value of each context is 0, as it is not sent.
    /// </summary>
    public bool TryDeserialize(ref BitStreamReader input)
    {
        if (!input.TryReadCompressed(out uint count) || count > MaxDeserializedFiles)
            return false;
        Clear();
        for (var i = 0u; i < count; i++)
        {
            if (!input.TryReadCompressed(out byte op) || !input.TryReadCompressed(out uint extra1) || !input.TryReadCompressed(out uint extra2)
                || !input.TryReadCompressedString(MaxFilenameLength, out var filename) || !input.TryReadBool(out var dataLengthNonZero))
                return Fail();
            byte[]? data = null;
            uint dataLength = 0;
            if (dataLengthNonZero)
            {
                if (!input.TryReadCompressed(out dataLength) || dataLength > MaxDeserializedDataLength || (uint)input.BitsRemaining / 8 < dataLength)
                    return Fail();
                data = new byte[dataLength];
                if (!input.TryReadBytes(data))
                    return Fail();
            }
            if (!input.TryReadBool(out var fileLengthMatchesDataLength))
                return Fail();
            var fileLength = dataLength;
            if (!fileLengthMatchesDataLength && !input.TryReadCompressed(out fileLength))
                return Fail();
            _files.Add(new FileListNode(filename, filename, data, dataLength, fileLength, new FileListNodeContext(op, extra1, extra2, 0), isAReference: false));
        }
        return true;

        bool Fail()
        {
            Clear();
            return false;
        }
    }

    /// <summary>
    /// GetDeltaToCurrent (FileList.cpp:475-543): adds to <paramref name="output"/>, by name and length only, each file of
    /// this list that <paramref name="input"/> lacks or holds with different lengths or data.
    /// </summary>
    /// <param name="input">The other system's files.</param>
    /// <param name="output">Receives the files this list has that <paramref name="input"/> does not match.</param>
    /// <param name="dirSubset">Only files of this list in this directory, or below it, are considered; their names are compared without it.</param>
    /// <param name="remoteSubdir">Removed from the start of the names in <paramref name="input"/> before comparing.</param>
    /// <remarks>Names compare without regard to ASCII case, as _stricmp does.</remarks>
    public void GetDeltaToCurrent(FileList input, FileList output, string? dirSubset, string? remoteSubdir)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var subset = Encoding.UTF8.GetBytes(dirSubset ?? string.Empty);
        var remote = Encoding.UTF8.GetBytes(remoteSubdir ?? string.Empty);
        var remoteLength = remote.Length;
        if (remoteLength > 0 && FileOperations.IsSlash((char)remote[remoteLength - 1]))
            remoteLength--;

        foreach (var local in _files)
        {
            var name = Encoding.UTF8.GetBytes(local.Filename);
            var localPathLength = name.Length;
            while (localPathLength > 0)
            {
                if (FileOperations.IsSlash((char)name[localPathLength - 1]))
                {
                    localPathLength--;
                    break;
                }
                localPathLength--;
            }

            // The file's directory must be dirSubset or below it.
            if (subset.Length > 0 && (localPathLength < subset.Length || !AsciiEqualsIgnoreCase(name.AsSpan(0, subset.Length), subset)
                || (localPathLength > subset.Length && !FileOperations.IsSlash((char)name[subset.Length]))))
                continue;

            var match = false;
            foreach (var other in input._files)
            {
                var otherName = Encoding.UTF8.GetBytes(other.Filename);
                if (otherName.Length < remoteLength || name.Length < subset.Length
                    || !AsciiEqualsIgnoreCase(otherName.AsSpan(remoteLength), name.AsSpan(subset.Length)))
                    continue;
                match = true;
                if (!(other.FileLengthBytes == local.FileLengthBytes && other.DataLengthBytes == local.DataLengthBytes
                      && SentPrefixEquals(other, local, local.DataLengthBytes)))
                    output.AddFile(local.Filename, local.FullPathToFile, [], local.FileLengthBytes, default);
                break;
            }
            if (!match)
                output.AddFile(local.Filename, local.FullPathToFile, [], local.FileLengthBytes, default);
        }
    }

    /// <summary>
    /// ListMissingOrChangedFiles (FileList.cpp:544-602): adds to <paramref name="missingOrChangedFiles"/> each file of
    /// this list that is missing under <paramref name="applicationDirectory"/> or differs from it in length or hash.
    /// </summary>
    /// <param name="applicationDirectory">Prefixed to each name.</param>
    /// <param name="missingOrChangedFiles">Receives the files, by name and length, with the hash on disk where it was computed and <paramref name="neverWriteHash"/> is false.</param>
    /// <param name="alwaysWriteHash">Hash a file on disk even when its length already differs.</param>
    /// <param name="neverWriteHash">Never store the hash.</param>
    public void ListMissingOrChangedFiles(string applicationDirectory, FileList missingOrChangedFiles, bool alwaysWriteHash, bool neverWriteHash)
    {
        ArgumentNullException.ThrowIfNull(applicationDirectory);
        ArgumentNullException.ThrowIfNull(missingOrChangedFiles);
        Span<byte> hash = stackalloc byte[HashLength];
        foreach (var node in _files)
        {
            var path = FileOperations.Normalize(FixEndingSlash(applicationDirectory) + node.Filename);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                missingOrChangedFiles.AddFile(node.Filename, node.FullPathToFile, [], 0, default);
                continue;
            }
            using (stream)
            {
                var fileLength = unchecked((uint)stream.Length);
                if (fileLength != node.FileLengthBytes && !alwaysWriteHash)
                {
                    missingOrChangedFiles.AddFile(node.Filename, node.FullPathToFile, [], fileLength, default);
                    continue;
                }
                BinaryPrimitives.WriteUInt32BigEndian(hash, SuperFastHash.HashStream(stream));
                var stored = node.Data;
                if (fileLength != node.FileLengthBytes || stored is null || stored.Length < HashLength || !hash.SequenceEqual(stored.AsSpan(0, HashLength)))
                    missingOrChangedFiles.AddFile(node.Filename, node.FullPathToFile, neverWriteHash ? [] : hash, fileLength, default);
            }
        }
    }

    /// <summary>
    /// PopulateDataFromDisk (FileList.cpp:603-690): reads each file under <paramref name="applicationDirectory"/> into
    /// its data: the contents, the hash, or the hash then the contents.
    /// </summary>
    /// <param name="applicationDirectory">Prefixed to each name.</param>
    /// <param name="writeFileData">Read the contents.</param>
    /// <param name="writeFileHash">Store the hash first, or alone.</param>
    /// <param name="removeUnknownFiles">Remove files that are not on disk.</param>
    /// <remarks>
    /// As in RakNet, the hash then the contents leaves <see cref="FileListNode.DataLengthBytes"/> as it was, the hash
    /// alone sets it to <see cref="HashLength"/>, and a file read for neither keeps no data; RakNet never moves past such
    /// a file and loops forever.
    /// </remarks>
    public void PopulateDataFromDisk(string applicationDirectory, bool writeFileData, bool writeFileHash, bool removeUnknownFiles)
    {
        ArgumentNullException.ThrowIfNull(applicationDirectory);
        var i = 0;
        while (i < _files.Count)
        {
            var node = _files[i];
            node.Data = null;
            var path = FileOperations.Normalize(FixEndingSlash(applicationDirectory) + node.Filename);
            byte[] contents;
            try
            {
                contents = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                if (removeUnknownFiles)
                    _files.RemoveAt(i);
                else
                    i++;
                continue;
            }

            if (writeFileHash || writeFileData)
            {
                node.FileLengthBytes = (uint)contents.Length;
                if (writeFileHash && writeFileData)
                {
                    var data = new byte[HashLength + contents.Length];
                    contents.CopyTo(data, HashLength);
                    BinaryPrimitives.WriteUInt32BigEndian(data, SuperFastHash.Hash(contents));
                    node.Data = data;
                }
                else if (writeFileHash)
                {
                    node.DataLengthBytes = HashLength;
                    var hash = new byte[HashLength];
                    BinaryPrimitives.WriteUInt32BigEndian(hash, SuperFastHash.Hash(contents));
                    node.Data = hash;
                }
                else
                {
                    node.DataLengthBytes = node.FileLengthBytes;
                    node.Data = contents;
                }
            }
            else
            {
                node.DataLengthBytes = 0;
            }
            i++;
        }
    }

    /// <summary>
    /// FlagFilesAsReferences (FileList.cpp:691-698): marks every file a reference whose data length is its file length,
    /// so <see cref="FileListTransfer"/> reads it incrementally.
    /// </summary>
    public void FlagFilesAsReferences()
    {
        foreach (var node in _files)
        {
            node.IsAReference = true;
            node.DataLengthBytes = node.FileLengthBytes;
        }
    }

    /// <summary>
    /// WriteDataToDisk (FileList.cpp:699-725): writes each file's data under <paramref name="applicationDirectory"/>,
    /// creating directories. As in RakNet, a name containing ".." cancels the rest of the write; so does one that would
    /// land outside the directory.
    /// </summary>
    public void WriteDataToDisk(string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(applicationDirectory);
        var directory = FixEndingSlash(applicationDirectory);
        foreach (var node in _files)
        {
            if (node.Filename.Contains("..", StringComparison.Ordinal) || !FileOperations.TryGetContainedPath(directory, node.Filename, out var path))
                return;
            FileOperations.WriteFileWithDirectories(path, node.SentData());
        }
    }

    /// <summary>
    /// DeleteFiles (FileList.cpp:730-769): deletes each file under <paramref name="applicationDirectory"/>. As in RakNet,
    /// a name containing ".." cancels the rest; so does one outside the directory.
    /// </summary>
    public void DeleteFiles(string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(applicationDirectory);
        var directory = FixEndingSlash(applicationDirectory);
        foreach (var node in _files)
        {
            if (node.Filename.Contains("..", StringComparison.Ordinal) || !FileOperations.TryGetContainedPath(directory, node.Filename, out var path))
                return;
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // RakNet prints "FileList::DeleteFiles: unlink (%s) failed." and goes on.
            }
        }
    }

    /// <summary>AddCallback: adds a progress callback once.</summary>
    public void AddCallback(IFileListProgress? callback)
    {
        if (callback is not null && !_callbacks.Contains(callback))
            _callbacks.Add(callback);
    }

    /// <summary>RemoveCallback</summary>
    public void RemoveCallback(IFileListProgress callback) => _callbacks.Remove(callback);

    /// <summary>ClearCallbacks</summary>
    public void ClearCallbacks() => _callbacks.Clear();

    /// <summary>GetCallbacks</summary>
    public IReadOnlyList<IFileListProgress> Callbacks => _callbacks;

    /// <summary>
    /// FixEndingSlash (FileList.cpp:795-812): appends '\' on Windows and '/' elsewhere unless the path already ends in
    /// either. An empty path stays empty.
    /// </summary>
    public static string FixEndingSlash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0 || FileOperations.IsSlash(path[^1]))
            return path;
        return path + (OperatingSystem.IsWindows() ? "\\" : "/");
    }

    // RakString(const char*): up to the first NUL.
    private static string CString(string value)
    {
        var end = value.IndexOf('\0', StringComparison.Ordinal);
        return end < 0 ? value : value[..end];
    }

    // An upper bound on EncodeString's output: a Huffman code over 256 symbols is at most 255 bits, plus the
    // compressed length.
    private static int MaxEncodedNameBytes(string name) => 8 + Math.Min(Encoding.UTF8.GetByteCount(name), MaxFilenameLength) * 32;

    // fread of fileInfo.size bytes after `offset` bytes for the hash; false when the file cannot be opened.
    private static bool TryReadPrefix(string path, uint size, int offset, out byte[] data)
    {
        data = new byte[offset + (int)Math.Min(size, (uint)(Array.MaxLength - offset))];
        try
        {
            using var stream = new FileStream(FileOperations.Normalize(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.ReadAtLeast(data.AsSpan(offset), data.Length - offset, throwOnEndOfStream: false);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    // memcmp of the first `length` bytes of each node's data; data a node lacks compares as different unless nothing is compared.
    private static bool SentPrefixEquals(FileListNode a, FileListNode b, uint length)
    {
        if (length == 0)
            return true;
        if (a.Data is null || b.Data is null || a.Data.Length < length || b.Data.Length < length)
            return false;
        return a.Data.AsSpan(0, (int)length).SequenceEqual(b.Data.AsSpan(0, (int)length));
    }

    // memcmp of up to `length` bytes of both.
    private static bool SameBytes(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, uint length)
    {
        var count = (int)Math.Min(length, (uint)Math.Min(a.Length, b.Length));
        return a.Length >= count && b.Length >= count && a[..count].SequenceEqual(b[..count]) && (a.Length >= length) == (b.Length >= length);
    }

    // _stricmp and _strnicmp in the C locale: bytes compare equal when they are equal after ASCII letters are lowered.
    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i] && Lower(a[i]) != Lower(b[i]))
                return false;
        }
        return true;

        static int Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c;
    }
}
