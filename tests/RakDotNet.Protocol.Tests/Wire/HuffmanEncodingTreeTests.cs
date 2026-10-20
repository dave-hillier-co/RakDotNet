using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// The frequencies of "aaaaaaaabbbbccd". Native StringCompressor with that tree as language 7 wrote "abcd" as
// 800000083ff3dd6fc0 (66 bits, tools/bitstream-vectors): a leading 1 bit, the bit length compressed, then
// HuffmanEncodingTree::EncodeArray's output.
public class HuffmanEncodingTreeTests
{
    private const string NativeAbcd = "800000083ff3dd6fc0";

    private static HuffmanEncodingTree SampleTree()
    {
        var frequencies = new uint[256];
        frequencies['a'] = 8;
        frequencies['b'] = 4;
        frequencies['c'] = 2;
        frequencies['d'] = 1;
        return new HuffmanEncodingTree(frequencies);
    }

    private static (byte[] Bytes, int Bits) NativeEncodedAbcd()
    {
        var reader = new BitStreamReader(Convert.FromHexString(NativeAbcd), 66);
        Assert.True(reader.IgnoreBits(1));
        Assert.True(reader.TryReadCompressed(out uint bitLength));
        var bytes = new byte[(bitLength + 7) / 8];
        Assert.True(reader.TryReadBits(bytes, (int)bitLength));
        return (bytes, (int)bitLength);
    }

    [Fact]
    public void EncodeArray_writes_the_codes_padded_to_a_byte_as_native_does()
    {
        // DS_HuffmanEncodingTree.cpp:171-199.
        var (expected, bits) = NativeEncodedAbcd();
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitStreamWriter(buffer);
        SampleTree().EncodeArray("abcd"u8, ref writer);
        Assert.Equal(bits, writer.BitsWritten);
        Assert.Equal(expected, writer.Written.ToArray());
    }

    [Fact]
    public void EncodeArray_pads_to_the_writer_s_byte_boundary()
    {
        // DS_HuffmanEncodingTree.cpp:182-191: the padding uses the output's whole bit count, not the encoded bits alone.
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBits(0, 3);
        SampleTree().EncodeArray("abcd"u8, ref writer);
        Assert.Equal(0, writer.BitsWritten % 8);
    }

    [Fact]
    public void TryDecodeArray_counts_every_decoded_byte_and_keeps_those_that_fit()
    {
        // DS_HuffmanEncodingTree.cpp:202-232: the return counts every leaf; only maxCharsToWrite are stored.
        var (encoded, bits) = NativeEncodedAbcd();
        var reader = new BitStreamReader(encoded, bits);
        var output = new byte[3];
        Assert.True(SampleTree().TryDecodeArray(ref reader, bits, output, out var count));
        Assert.Equal(4, count);
        Assert.Equal("abc"u8.ToArray(), output);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void TryDecodeArray_fails_without_moving_when_the_bits_are_missing()
    {
        var (encoded, bits) = NativeEncodedAbcd();
        var reader = new BitStreamReader(encoded, bits - 1);
        Assert.False(SampleTree().TryDecodeArray(ref reader, bits, new byte[8], out _));
        Assert.Equal(0, reader.BitPosition);
    }

    [Fact]
    public void DecodeArray_from_bytes_writes_each_decoded_byte()
    {
        // DS_HuffmanEncodingTree.cpp:235-260.
        var (encoded, bits) = NativeEncodedAbcd();
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        SampleTree().DecodeArray(encoded, bits, ref writer);
        var reader = new BitStreamReader(writer.Written, writer.BitsWritten);
        Assert.True(reader.IgnoreBits(1));
        var decoded = new byte[4];
        Assert.True(reader.TryReadBytes(decoded));
        Assert.Equal("abcd"u8.ToArray(), decoded);
    }

    [Fact]
    public void Merged_weights_wrap_at_32_bits_as_native_does()
    {
        // DS_HuffmanEncodingTreeNode.h:21 keeps the weight as unsigned, so the sum at DS_HuffmanEncodingTree.cpp:109 wraps
        // and the wrapped node sorts lighter. Native (tools/bitstream-vectors, huff_overflow_encode_2013) wrote a 1 bit
        // then the codes for 2, 0, 1, 3 as c68120 (24 bits).
        var frequencies = new uint[256];
        Array.Fill(frequencies, 1u);
        frequencies[0] = 0x80000000;
        frequencies[1] = 0x80000000;
        frequencies[2] = 0xC0000000;
        Span<byte> buffer = stackalloc byte[16];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        new HuffmanEncodingTree(frequencies).EncodeArray([2, 0, 1, 3], ref writer);
        Assert.Equal(24, writer.BitsWritten);
        Assert.Equal("c68120", Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void A_tree_needs_256_frequencies()
    {
        Assert.Throws<ArgumentException>(() => new HuffmanEncodingTree(new uint[255]));
    }
}
