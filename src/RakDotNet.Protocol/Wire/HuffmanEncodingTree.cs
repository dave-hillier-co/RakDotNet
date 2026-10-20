namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet's HuffmanEncodingTree (DS_HuffmanEncodingTree.h): a Huffman code for byte values built from a table of 256
/// frequencies, used by <see cref="StringCompressor"/> and usable on its own. Immutable once built, so it can be shared
/// between threads.
/// </summary>
public sealed class HuffmanEncodingTree
{
    private const int LeafCount = 256;

    private readonly int[] _left;
    private readonly int[] _right;
    private readonly byte[] _value;
    private readonly int _root;

    // Each byte's path, root to leaf, packed most significant bit first: a left branch is 0, a right branch 1. The first
    // byte is the length in bits; the bits after it are zero, as the BitStream that builds RakNet's encodingTable leaves
    // them (DS_HuffmanEncodingTree.cpp:131-166).
    private readonly byte[][] _codes;

    /// <summary>
    /// HuffmanEncodingTree::GenerateFromFrequencyTable (DS_HuffmanEncodingTree.cpp:68-168). A frequency of 0 counts as 1,
    /// as RakNet treats 0 weights as illegal (DS_HuffmanEncodingTree.cpp:86-87).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="frequencyTable"/> does not hold 256 entries.</exception>
    public HuffmanEncodingTree(ReadOnlySpan<uint> frequencyTable)
    {
        if (frequencyTable.Length != LeafCount)
            throw new ArgumentException("A frequency table has one entry for each of the 256 byte values.", nameof(frequencyTable));

        // Leaves are nodes 0 to 255, inner nodes follow; -1 marks no child.
        const int nodeCount = (LeafCount * 2) - 1;
        _left = new int[nodeCount];
        _right = new int[nodeCount];
        _value = new byte[nodeCount];
        // HuffmanEncodingTreeNode::weight is unsigned (DS_HuffmanEncodingTreeNode.h:21), so sums wrap modulo 2^32.
        var weight = new uint[nodeCount];
        var parent = new int[nodeCount];
        Array.Fill(_left, -1);
        Array.Fill(_right, -1);

        // A list kept in ascending weight; a node goes before the first node not lighter than it
        // (InsertNodeIntoSortedList, DS_HuffmanEncodingTree.cpp:263-300).
        var sorted = new List<int>(LeafCount);
        for (var i = 0; i < LeafCount; i++)
        {
            _value[i] = (byte)i;
            weight[i] = Math.Max(1u, frequencyTable[i]);
            Insert(sorted, weight, i);
        }

        var next = LeafCount;
        while (true)
        {
            var lesser = sorted[0];
            var greater = sorted[1];
            sorted.RemoveRange(0, 2);
            var node = next++;
            _left[node] = lesser;
            _right[node] = greater;
            weight[node] = unchecked(weight[lesser] + weight[greater]);
            parent[lesser] = node;
            parent[greater] = node;
            if (sorted.Count == 0)
            {
                _root = node;
                break;
            }
            Insert(sorted, weight, node);
        }

        _codes = new byte[LeafCount][];
        Span<bool> path = stackalloc bool[LeafCount];
        for (var leaf = 0; leaf < LeafCount; leaf++)
        {
            var length = 0;
            for (var node = leaf; node != _root; node = parent[node])
                path[length++] = _right[parent[node]] == node;
            var code = new byte[1 + ((length + 7) >> 3)];
            code[0] = (byte)length;
            for (var i = 0; i < length; i++)
            {
                if (path[length - 1 - i])
                    code[1 + (i >> 3)] |= (byte)(0x80 >> (i & 7));
            }
            _codes[leaf] = code;
        }
    }

    /// <summary>
    /// HuffmanEncodingTree::EncodeArray (DS_HuffmanEncodingTree.cpp:171-199): each byte's code, then, if the writer is
    /// not on a byte boundary, the leading bits of the first code longer than the bits left in the byte, so the padding
    /// decodes to no byte.
    /// </summary>
    public void EncodeArray(scoped ReadOnlySpan<byte> input, ref BitStreamWriter output)
    {
        foreach (var b in input)
            WriteCode(ref output, b, _codes[b][0]);

        if ((output.BitsWritten & 7) == 0)
            return;
        var remainingBits = 8 - (output.BitsWritten & 7);
        for (var b = 0; b < LeafCount; b++)
        {
            if (_codes[b][0] > remainingBits)
            {
                WriteCode(ref output, (byte)b, remainingBits);
                return;
            }
        }
    }

    /// <summary>
    /// HuffmanEncodingTree::DecodeArray (DS_HuffmanEncodingTree.cpp:202-232): walks <paramref name="sizeInBits"/> bits,
    /// stores the first <paramref name="output"/>.Length decoded bytes and counts all of them in
    /// <paramref name="decodedCount"/>. False, without moving, when fewer bits are unread, where RakNet reads past the end.
    /// </summary>
    public bool TryDecodeArray(ref BitStreamReader input, int sizeInBits, scoped Span<byte> output, out int decodedCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeInBits);
        decodedCount = 0;
        if (input.BitsRemaining < sizeInBits)
            return false;
        var node = _root;
        for (var i = 0; i < sizeInBits; i++)
        {
            input.TryReadBit(out var right);
            node = right ? _right[node] : _left[node];
            if (_left[node] >= 0 || _right[node] >= 0)
                continue;
            if (decodedCount < output.Length)
                output[decodedCount] = _value[node];
            decodedCount++;
            node = _root;
        }
        return true;
    }

    /// <summary>
    /// HuffmanEncodingTree::DecodeArray from raw bytes (DS_HuffmanEncodingTree.cpp:235-260): decodes the first
    /// <paramref name="sizeInBits"/> bits of <paramref name="input"/> and writes each byte to <paramref name="output"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sizeInBits"/> is more than <paramref name="input"/> holds.</exception>
    public void DecodeArray(scoped ReadOnlySpan<byte> input, int sizeInBits, ref BitStreamWriter output)
    {
        if (sizeInBits <= 0)
            return;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeInBits, input.Length * 8);
        var reader = new BitStreamReader(input, sizeInBits);
        var node = _root;
        for (var i = 0; i < sizeInBits; i++)
        {
            reader.TryReadBit(out var right);
            node = right ? _right[node] : _left[node];
            if (_left[node] >= 0 || _right[node] >= 0)
                continue;
            output.WriteByte(_value[node]);
            node = _root;
        }
    }

    /// <summary>The bits EncodeArray writes for <paramref name="input"/> from a byte boundary, padding included.</summary>
    internal int EncodedBitLength(scoped ReadOnlySpan<byte> input)
    {
        var bits = 0;
        foreach (var b in input)
            bits += _codes[b][0];
        return (bits + 7) & ~7;
    }

    // Left-aligned, as EncodeArray writes the encodingTable entries (DS_HuffmanEncodingTree.cpp:178).
    private void WriteCode(ref BitStreamWriter output, byte value, int bitCount)
    {
        if (bitCount > 0)
            output.WriteBits(_codes[value].AsSpan(1), bitCount, rightAlignedBits: false);
    }

    private static void Insert(List<int> sorted, uint[] weight, int node)
    {
        var index = sorted.FindIndex(n => weight[n] >= weight[node]);
        if (index < 0)
            sorted.Add(node);
        else
            sorted.Insert(index, node);
    }
}
