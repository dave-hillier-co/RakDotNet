namespace RakDotNet.Plugins;

/// <summary>One directory entry, as _finddata_t describes it.</summary>
internal readonly record struct FindData(string Name, bool IsDirectory, bool IsHiddenOrSystem, long Size);

/// <summary>
/// _findfirst, _findnext and _findclose (_FindFirst.cpp), the directory listing <see cref="FileList"/> walks, on
/// <see cref="DirectoryInfo.EnumerateFileSystemInfos(string, EnumerationOptions)"/>.
/// </summary>
/// <remarks>
/// Entries come in the order the file system lists them, readdir's order on Unix as in RakNet's shim, and every entry is
/// listed, hidden ones included. The shim reports only regular files and directories and marks nothing hidden or system
/// (_FindFirst.cpp:109-123), so on Unix a dot file is an ordinary file; on Windows the attributes are the file
/// system's, as _findfirst reports them there.
/// </remarks>
internal static class FindFirst
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Simple,
    };

    /// <summary>
    /// The entries of <paramref name="directory"/> matching <paramref name="pattern"/>, or null when it cannot be opened,
    /// where _findfirst returns -1.
    /// </summary>
    public static List<FindData>? Find(string directory, string pattern = "*")
    {
        var info = new DirectoryInfo(directory.Length == 0 ? "." : FileOperations.Normalize(directory));
        var entries = new List<FindData>();
        try
        {
            foreach (var entry in info.EnumerateFileSystemInfos(pattern, Options))
            {
                var attributes = entry.Attributes;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var hiddenOrSystem = OperatingSystem.IsWindows() && (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                long size = 0;
                if (!isDirectory)
                {
                    if (entry is not FileInfo file || !TryLength(file, out size))
                        continue;
                }
                entries.Add(new FindData(entry.Name, isDirectory, hiddenOrSystem, size));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
        return entries;
    }

    // The shim stats each entry and skips one it cannot stat (_FindFirst.cpp:101-107).
    private static bool TryLength(FileInfo file, out long length)
    {
        try
        {
            length = file.Length;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            length = 0;
            return false;
        }
    }
}
