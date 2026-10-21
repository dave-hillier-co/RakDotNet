namespace RakDotNet.Plugins;

/// <summary>
/// IncrementalReadInterface (IncrementalReadInterface.h): reads a file in parts, so <see cref="FileListTransfer"/> can
/// send a file referenced by name without holding it in memory.
/// </summary>
public interface IIncrementalReader
{
    /// <summary>
    /// GetFilePart: reads from <paramref name="startReadBytes"/> of <paramref name="filename"/> into
    /// <paramref name="destination"/> and returns the bytes read, 0 when none are.
    /// </summary>
    uint GetFilePart(string filename, uint startReadBytes, Span<byte> destination, FileListNodeContext context);
}

/// <summary>
/// RakNet's own IncrementalReadInterface::GetFilePart (IncrementalReadInterface.cpp:16-25): reads the file from disk,
/// 0 bytes when it cannot be opened.
/// </summary>
public sealed class FileIncrementalReader : IIncrementalReader
{
    public uint GetFilePart(string filename, uint startReadBytes, Span<byte> destination, FileListNodeContext context)
    {
        ArgumentNullException.ThrowIfNull(filename);
        try
        {
            using var handle = File.OpenHandle(FileOperations.Normalize(filename), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var total = 0;
            while (total < destination.Length)
            {
                var read = RandomAccess.Read(handle, destination[total..], startReadBytes + (long)total);
                if (read == 0)
                    break;
                total += read;
            }
            return (uint)total;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return 0;
        }
    }
}
