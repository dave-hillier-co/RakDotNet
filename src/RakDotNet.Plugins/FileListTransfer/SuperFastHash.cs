using System.Buffers;
using System.Buffers.Binary;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's SuperFastHash (SuperFastHash.cpp, Paul Hsieh's hash): the file hash <see cref="FileList"/> stores and
/// <see cref="DirectoryDeltaTransfer"/> compares across systems, so it is bit-exact with native.
/// </summary>
/// <remarks>
/// <para>
/// The data is hashed in blocks of 64 KiB, each block a SuperFastHashIncremental seeded with the previous block's
/// hash and the first with the length, so a whole buffer and a file read in blocks hash the same
/// (SuperFastHash.cpp:42-60).
/// </para>
/// <para>
/// get16bits reads little-endian pairs, and the trailing single byte and the third byte of a three-byte tail are read
/// through <c>const char*</c> and so sign-extended, as on every platform RakNet targets where <c>char</c> is signed
/// (SuperFastHash.cpp:94-108).
/// </para>
/// </remarks>
public static class SuperFastHash
{
    /// <summary>INCREMENTAL_READ_BLOCK (SuperFastHash.cpp:40).</summary>
    public const int IncrementalReadBlock = 65536;

    /// <summary>SuperFastHash: the hash of <paramref name="data"/>; 0 for empty data.</summary>
    public static uint Hash(ReadOnlySpan<byte> data)
    {
        var lastHash = (uint)data.Length;
        while (data.Length >= IncrementalReadBlock)
        {
            lastHash = HashIncremental(data[..IncrementalReadBlock], lastHash);
            data = data[IncrementalReadBlock..];
        }
        if (!data.IsEmpty)
            lastHash = HashIncremental(data, lastHash);
        return lastHash;
    }

    /// <summary>SuperFastHashIncremental: continues from <paramref name="lastHash"/>; 0 for empty data.</summary>
    public static uint HashIncremental(ReadOnlySpan<byte> data, uint lastHash)
    {
        if (data.IsEmpty)
            return 0;
        var hash = lastHash;
        var rem = data.Length & 3;
        var blocks = data.Length >> 2;
        var i = 0;
        for (; blocks > 0; blocks--, i += 4)
        {
            hash += BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            var tmp = ((uint)BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 2)..]) << 11) ^ hash;
            hash = (hash << 16) ^ tmp;
            hash += hash >> 11;
        }

        switch (rem)
        {
            case 3:
                hash += BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                hash ^= hash << 16;
                hash ^= unchecked((uint)((sbyte)data[i + 2] << 18));
                hash += hash >> 11;
                break;
            case 2:
                hash += BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
                hash ^= hash << 11;
                hash += hash >> 17;
                break;
            case 1:
                hash += unchecked((uint)(sbyte)data[i]);
                hash ^= hash << 10;
                hash += hash >> 1;
                break;
        }

        // Force "avalanching" of final 127 bits.
        hash ^= hash << 3;
        hash += hash >> 5;
        hash ^= hash << 4;
        hash += hash >> 17;
        hash ^= hash << 25;
        hash += hash >> 6;
        return hash;
    }

    /// <summary>SuperFastHashFile: the hash of the file's contents, or 0 when it cannot be opened.</summary>
    public static uint HashFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        using (stream)
            return HashStream(stream);
    }

    /// <summary>
    /// SuperFastHashFilePtr: the hash of <paramref name="stream"/> from its start, read in blocks of
    /// <see cref="IncrementalReadBlock"/> bytes and seeded with its length.
    /// </summary>
    public static uint HashStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = stream.Length;
        stream.Seek(0, SeekOrigin.Begin);
        var block = ArrayPool<byte>.Shared.Rent(IncrementalReadBlock);
        try
        {
            var lastHash = unchecked((uint)length);
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, IncrementalReadBlock);
                stream.ReadExactly(block, 0, count);
                lastHash = HashIncremental(block.AsSpan(0, count), lastHash);
                remaining -= count;
            }
            return lastHash;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }
    }
}
