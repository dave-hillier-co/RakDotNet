namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet's StringCompressor with its default language: Huffman codes built from englishCharacterFrequencies
/// (StringCompressor.cpp:62-320) the way HuffmanEncodingTree::GenerateFromFrequencyTable builds them
/// (DS_HuffmanEncodingTree.cpp:66-160).
/// </summary>
internal sealed class StringCompressor
{
    private readonly int[] _left;
    private readonly int[] _right;
    private readonly byte[] _value;
    private readonly int _root;
    private readonly byte[][] _codes;

    private StringCompressor(ReadOnlySpan<uint> frequencies)
    {
        // Leaves are nodes 0 to 255, inner nodes follow; -1 marks no child.
        const int nodeCount = 511;
        _left = new int[nodeCount];
        _right = new int[nodeCount];
        _value = new byte[nodeCount];
        var weight = new long[nodeCount];
        var parent = new int[nodeCount];
        Array.Fill(_left, -1);
        Array.Fill(_right, -1);

        // A list kept in ascending weight; a node goes before the first node not lighter than it
        // (InsertNodeIntoSortedList, DS_HuffmanEncodingTree.cpp:262-300).
        var sorted = new List<int>(256);
        for (var i = 0; i < 256; i++)
        {
            _value[i] = (byte)i;
            weight[i] = Math.Max(1u, frequencies[i]); // 0 weights are illegal (DS_HuffmanEncodingTree.cpp:80-81)
            Insert(sorted, weight, i);
        }

        var next = 256;
        while (true)
        {
            var lesser = sorted[0];
            var greater = sorted[1];
            sorted.RemoveRange(0, 2);
            var node = next++;
            _left[node] = lesser;
            _right[node] = greater;
            weight[node] = weight[lesser] + weight[greater];
            parent[lesser] = node;
            parent[greater] = node;
            if (sorted.Count == 0)
            {
                _root = node;
                break;
            }
            Insert(sorted, weight, node);
        }

        // Each leaf's path, root to leaf, packed most significant bit first: a left branch is 0, a right branch 1.
        _codes = new byte[256][];
        Span<bool> path = stackalloc bool[256];
        for (var leaf = 0; leaf < 256; leaf++)
        {
            var length = 0;
            for (var node = leaf; node != _root; node = parent[node])
                path[length++] = _right[parent[node]] == node;
            var code = new byte[1 + length];
            code[0] = (byte)length;
            for (var i = 0; i < length; i++)
            {
                if (path[length - 1 - i])
                    code[1 + (i >> 3)] |= (byte)(0x80 >> (i & 7));
            }
            _codes[leaf] = code;
        }
    }

    /// <summary>Language 0, the tree every RakNet StringCompressor starts with (StringCompressor.cpp:322-330).</summary>
    public static StringCompressor English { get; } = new(EnglishCharacterFrequencies);

    /// <summary>
    /// StringCompressor::EncodeString (StringCompressor.cpp:366-397): at most <paramref name="maxCharCount"/> - 1 bytes,
    /// or all of them when the limit is 0 or more than their count; the bit length as a compressed u32, then the codes
    /// padded to a byte with a prefix of a longer code (DS_HuffmanEncodingTree.cpp:168-193).
    /// </summary>
    public void Write(ref BitStreamWriter writer, scoped ReadOnlySpan<byte> text, int maxCharCount)
    {
        var count = maxCharCount <= 0 || text.Length < maxCharCount ? text.Length : maxCharCount - 1;
        text = text[..count];

        var bitLength = 0;
        foreach (var b in text)
            bitLength += _codes[b][0];
        var padding = (8 - (bitLength & 7)) & 7;
        writer.WriteCompressed((uint)(bitLength + padding));

        foreach (var b in text)
            WriteCode(ref writer, _codes[b], _codes[b][0]);
        if (padding == 0)
            return;
        foreach (var code in _codes)
        {
            if (code[0] > padding)
            {
                WriteCode(ref writer, code, padding);
                return;
            }
        }
    }

    /// <summary>
    /// StringCompressor::DecodeString (StringCompressor.cpp:399-427): false when the limit is not positive or the stream
    /// is shorter than the encoded length; keeps every decoded byte when fewer than <paramref name="maxCharCount"/>,
    /// otherwise the first <paramref name="maxCharCount"/> - 1.
    /// </summary>
    public bool TryRead(ref BitStreamReader reader, int maxCharCount, out byte[] text)
    {
        text = [];
        if (maxCharCount <= 0)
            return false;
        var r = reader;
        if (!r.TryReadCompressed(out uint bitLength) || (uint)r.BitsRemaining < bitLength)
            return false;

        var decoded = new List<byte>();
        var node = _root;
        for (var i = 0u; i < bitLength; i++)
        {
            r.TryReadBit(out var right);
            node = right ? _right[node] : _left[node];
            if (_left[node] < 0 && _right[node] < 0)
            {
                decoded.Add(_value[node]);
                node = _root;
            }
        }

        var kept = decoded.Count < maxCharCount ? decoded.Count : maxCharCount - 1;
        text = [.. decoded.GetRange(0, kept)];
        reader = r;
        return true;
    }

    private static void WriteCode(ref BitStreamWriter writer, byte[] code, int bitCount)
    {
        for (var i = 0; i < bitCount; i++)
            writer.WriteBit((code[1 + (i >> 3)] & (0x80 >> (i & 7))) != 0);
    }

    private static void Insert(List<int> sorted, long[] weight, int node)
    {
        var index = sorted.FindIndex(n => weight[n] >= weight[node]);
        if (index < 0)
            sorted.Add(node);
        else
            sorted.Insert(index, node);
    }

    // englishCharacterFrequencies, StringCompressor.cpp:62-320.
    private static ReadOnlySpan<uint> EnglishCharacterFrequencies =>
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
