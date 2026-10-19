using System.Buffers;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet's StringCompressor (StringCompressor.h): Huffman-coded strings, with a tree per language id. Language 0 is
/// built from <see cref="EnglishCharacterFrequencies"/>; <see cref="GenerateTreeFromStrings"/> adds or replaces others.
/// </summary>
/// <remarks>
/// Strings are bytes, as RakNet's are C strings: input ends at its first NUL byte, as <c>strlen</c> ends it. The
/// string encodings in <see cref="BitStreamEncodings"/> use <see cref="Instance"/>, as RakString does. Safe to use from
/// several threads; a language replaced while another thread encodes is seen either whole or not at all.
/// </remarks>
public sealed class StringCompressor
{
    private static readonly HuffmanEncodingTree English = new(EnglishCharacterFrequencies);

    private readonly HuffmanEncodingTree?[] _trees = new HuffmanEncodingTree?[256];

    /// <summary>A compressor with language 0 only (StringCompressor.cpp:322-330).</summary>
    public StringCompressor() => _trees[0] = English;

    /// <summary>StringCompressor::Instance, the compressor RakString and the string encodings use.</summary>
    public static StringCompressor Instance { get; } = new();

    /// <summary>
    /// StringCompressor::GenerateTreeFromStrings (StringCompressor.cpp:332-360): builds language
    /// <paramref name="languageId"/> from the byte frequencies of <paramref name="input"/>, replacing any tree it had.
    /// Empty input removes the language, as RakNet deletes the old tree before it returns.
    /// </summary>
    public void GenerateTreeFromStrings(scoped ReadOnlySpan<byte> input, byte languageId)
    {
        if (input.IsEmpty)
        {
            Volatile.Write(ref _trees[languageId], null);
            return;
        }
        Span<uint> frequencies = stackalloc uint[256];
        frequencies.Clear();
        foreach (var b in input)
            frequencies[b]++;
        Volatile.Write(ref _trees[languageId], new HuffmanEncodingTree(frequencies));
    }

    /// <summary>
    /// StringCompressor::EncodeString (StringCompressor.cpp:366-397): at most <paramref name="maxCharCount"/> - 1 bytes,
    /// or all of them when the limit is 0 or more than their count; the bit length as a compressed u32, then the codes
    /// padded to a byte (DS_HuffmanEncodingTree.cpp:171-199). Nothing is written for a language without a tree.
    /// </summary>
    public void EncodeString(ref BitStreamWriter writer, scoped ReadOnlySpan<byte> input, int maxCharCount, byte languageId = 0)
    {
        var tree = Volatile.Read(ref _trees[languageId]);
        if (tree is null)
            return;
        var text = ToCString(input);
        var count = maxCharCount <= 0 || text.Length < maxCharCount ? text.Length : maxCharCount - 1;
        text = text[..count];

        // RakNet encodes into a fresh BitStream, so the padding is relative to the start of the codes.
        var bitLength = tree.EncodedBitLength(text);
        var byteLength = bitLength >> 3;
        var rented = byteLength > 256 ? ArrayPool<byte>.Shared.Rent(byteLength) : null;
        var encoded = rented is null ? stackalloc byte[byteLength] : rented.AsSpan(0, byteLength);
        var encoder = new BitStreamWriter(encoded);
        tree.EncodeArray(text, ref encoder);
        writer.WriteCompressed((uint)bitLength);
        writer.WriteBits(encoded, bitLength);
        if (rented is not null)
            ArrayPool<byte>.Shared.Return(rented);
    }

    /// <summary>
    /// StringCompressor::DecodeString into a buffer (StringCompressor.cpp:399-427): false for a language without a tree,
    /// a limit that is not positive, or a stream shorter than the encoded length. Keeps every decoded byte when fewer than
    /// <paramref name="maxCharCount"/>, otherwise the first <paramref name="maxCharCount"/> - 1; a decoded NUL is kept,
    /// as it is in RakNet's buffer.
    /// </summary>
    public bool TryDecodeString(ref BitStreamReader reader, int maxCharCount, out byte[] output, byte languageId = 0)
    {
        output = [];
        var tree = Volatile.Read(ref _trees[languageId]);
        if (tree is null || maxCharCount <= 0)
            return false;
        var r = reader;
        if (!r.TryReadCompressed(out uint bitLength) || (uint)r.BitsRemaining < bitLength)
            return false;

        // Every code is at least one bit, so no more bytes than bits are decoded.
        var decoded = new byte[(int)Math.Min((uint)maxCharCount, bitLength)];
        tree.TryDecodeArray(ref r, (int)bitLength, decoded, out var count);
        var kept = count < maxCharCount ? count : maxCharCount - 1;
        output = kept == decoded.Length ? decoded : decoded[..kept];
        reader = r;
        return true;
    }

    /// <summary>The bytes before the first NUL, as strlen measures a C string.</summary>
    internal static ReadOnlySpan<byte> ToCString(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        return end < 0 ? value : value[..end];
    }

    /// <summary>englishCharacterFrequencies (StringCompressor.cpp:62-320), the table language 0 is built from.</summary>
    public static ReadOnlySpan<uint> EnglishCharacterFrequencies =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 722, 0, 0, 2, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        11084, 58, 63, 1, 0, 31, 0, 317, 64, 64, 44, 0, 695, 62, 980, 266,
        69, 67, 56, 7, 73, 3, 14, 2, 69, 1, 167, 9, 1, 2, 25, 94,
        0, 195, 139, 34, 96, 48, 103, 56, 125, 653, 21, 5, 23, 64, 85, 44,
        34, 7, 92, 76, 147, 12, 14, 57, 15, 39, 15, 1, 1, 1, 2, 3,
        0, 3611, 845, 1077, 1884, 5870, 841, 1057, 2501, 3212, 164, 531, 2019, 1330, 3056, 4037,
        848, 47, 2586, 2919, 4771, 1707, 535, 1106, 152, 1243, 100, 0, 2, 0, 10, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];
}
