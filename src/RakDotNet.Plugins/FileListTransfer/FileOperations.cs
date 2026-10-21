namespace RakDotNet.Plugins;

/// <summary>
/// FileOperations (FileOperations.cpp): the file and directory helpers <see cref="FileList"/> and
/// <see cref="DirectoryDeltaTransfer"/> use, on <see cref="File"/>, <see cref="Directory"/> and <see cref="Path"/>.
/// </summary>
/// <remarks>
/// RakNet treats both '/' and '\' as separators. On Windows both are; elsewhere a '\' in a path is written as '/', where
/// RakNet would create the directories at each '\' and then a file whose name contains the '\'.
/// </remarks>
public static class FileOperations
{
    /// <summary>
    /// WriteFileWithDirectories with data (FileOperations.cpp:40-117): creates every directory along
    /// <paramref name="path"/>, then writes <paramref name="data"/> to it. False for an empty path or when a directory or
    /// the file cannot be created.
    /// </summary>
    public static bool WriteFileWithDirectories(string path, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return false;
        var normalized = Normalize(path);
        try
        {
            var directory = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            using var stream = new FileStream(normalized, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.Write(data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// WriteFileWithDirectories with null data (FileOperations.cpp:92-112): creates every directory along
    /// <paramref name="path"/>, the last one included. False for an empty path or when one cannot be created.
    /// </summary>
    public static bool CreateDirectories(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            return false;
        try
        {
            Directory.CreateDirectory(Normalize(path));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>IsSlash</summary>
    public static bool IsSlash(char c) => c is '/' or '\\';

    /// <summary>
    /// AddSlash (FileOperations.cpp:122-135): a trailing '\' becomes '/', and '/' is appended when the path ends in
    /// neither. An empty path stays empty.
    /// </summary>
    public static string AddSlash(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Length == 0)
            return input;
        return input[^1] switch
        {
            '\\' => string.Concat(input.AsSpan(0, input.Length - 1), "/"),
            '/' => input,
            _ => input + "/",
        };
    }

    /// <summary>DirectoryExists (FileOperations.cpp:136-149).</summary>
    public static bool DirectoryExists(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        return Directory.Exists(Normalize(directory));
    }

    /// <summary>QuoteIfSpaces (FileOperations.cpp:150-169): wraps the text in double quotes when it holds a space.</summary>
    public static string QuoteIfSpaces(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Contains(' ', StringComparison.Ordinal) ? $"\"{text}\"" : text;
    }

    /// <summary>GetFileLength (FileOperations.cpp:170-178): the file's length, or 0 when it cannot be opened.</summary>
    public static uint GetFileLength(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            return unchecked((uint)new FileInfo(Normalize(path)).Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Joins <paramref name="directory"/> and <paramref name="relativePath"/> as RakNet does, by concatenation, and returns
    /// true only when the result names something strictly inside <paramref name="directory"/>. Every path that comes from
    /// another system is checked with this before it is written to or deleted.
    /// </summary>
    /// <remarks>
    /// The check is on the full paths after '.' and '..' are resolved, so neither '..' nor a rooted or drive-qualified
    /// name can leave the directory. A NUL ends nothing here: a name holding one is refused.
    /// </remarks>
    public static bool TryGetContainedPath(string directory, string relativePath, out string fullPath)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(relativePath);
        fullPath = string.Empty;
        if (relativePath.Length == 0 || relativePath.Contains('\0', StringComparison.Ordinal) || directory.Contains('\0', StringComparison.Ordinal))
            return false;
        try
        {
            var root = Path.GetFullPath(Normalize(directory.Length == 0 ? "." : directory));
            if (!Path.EndsInDirectorySeparator(root))
                root += Path.DirectorySeparatorChar;
            var joined = Path.GetFullPath(Normalize(root + relativePath));
            if (joined.Length <= root.Length || !joined.StartsWith(root, StringComparison.Ordinal))
                return false;
            fullPath = joined;
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>A path with RakNet's '\' separators written as this platform's.</summary>
    internal static string Normalize(string path) =>
        Path.DirectorySeparatorChar == '\\' ? path : path.Replace('\\', '/');
}
