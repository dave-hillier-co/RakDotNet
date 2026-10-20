using System.Numerics;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are known answers printed by native RakNet 1a169895 (BitStream.h, BitStream.cpp, RakString.cpp,
// StringCompressor.cpp) on a little-endian host, with tools/bitstream-vectors. Every case starts with one 1 bit, as the
// tool writes it, so each encoding is checked off a byte boundary.
public class BitStreamEncodingsTests
{
    [Theory]
    [InlineData((byte)0, 6, "c0")]
    [InlineData((byte)5, 6, "d4")]
    [InlineData((byte)0xab, 10, "aac0")]
    public void WriteCompressed_byte_matches_native(byte value, int bits, string hex) => AssertCompressed(value, bits, hex);

    [Fact]
    public void WriteCompressed_sbyte_matches_native() => AssertCompressed((sbyte)-1, 10, "bfc0");

    [Theory]
    [InlineData((ushort)0, 7, "e0")]
    [InlineData((ushort)1, 18, "800040")]
    [InlineData((ushort)0x0100, 7, "e2")]
    [InlineData((ushort)0x0500, 7, "ea")]
    [InlineData((ushort)0x1234, 18, "848d00")]
    public void WriteCompressed_ushort_drops_zero_low_order_bytes_as_native_does(ushort value, int bits, string hex) =>
        AssertCompressed(value, bits, hex);

    [Fact]
    public void WriteCompressed_short_treats_the_value_as_unsigned_as_native_does() => AssertCompressed((short)-1, 18, "bfffc0");

    [Theory]
    [InlineData(0u, 9, "f800")]
    [InlineData(1u, 34, "8000000040")]
    [InlineData(0x01000000u, 9, "f880")]
    [InlineData(0x12000000u, 13, "f090")]
    [InlineData(0x12345678u, 34, "848d159e00")]
    public void WriteCompressed_uint_matches_native(uint value, int bits, string hex) => AssertCompressed(value, bits, hex);

    [Theory]
    [InlineData(-1, 34, "bfffffffc0")]
    [InlineData(100, 34, "8000001900")]
    public void WriteCompressed_int_matches_native(int value, int bits, string hex) => AssertCompressed(value, bits, hex);

    [Theory]
    [InlineData(0ul, 13, "ff80")]
    [InlineData(0x0300000000000000ul, 13, "ff98")]
    [InlineData(0x123456789abcdef0ul, 66, "848d159e26af37bc00")]
    public void WriteCompressed_ulong_matches_native(ulong value, int bits, string hex) => AssertCompressed(value, bits, hex);

    [Fact]
    public void TryReadCompressed_fails_on_truncated_input_without_moving()
    {
        var reader = new BitStreamReader([0x84, 0x8d], 12);

        Assert.False(reader.TryReadCompressed(out uint _));
        Assert.Equal(0, reader.BitPosition);
    }

    [Theory]
    [InlineData((ushort)500, (ushort)0, (ushort)1000, 11, "fa20")]
    [InlineData((ushort)7, (ushort)5, (ushort)20, 5, "90")]
    public void WriteBitsFromIntegerRange_writes_the_offset_little_endian_in_the_needed_bits(ushort value, ushort minimum, ushort maximum, int bits, string hex)
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange(value, minimum, maximum), bits, hex);
        var reader = Reader(hex, bits);
        Assert.True(reader.TryReadBitsFromIntegerRange(minimum, maximum, out ushort read));
        Assert.Equal(value, read);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void WriteBitsFromIntegerRange_handles_negative_ranges()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange(-3, -10, 10), 6, "9c");
        var reader = Reader("9c", 6);
        Assert.True(reader.TryReadBitsFromIntegerRange(-10, 10, out int read));
        Assert.Equal(-3, read);
    }

    [Fact]
    public void WriteBitsFromIntegerRange_covers_byte_and_wide_ranges()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange((byte)3, (byte)0, (byte)3), 3, "e0");
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange(0x12345u, 0u, 0xfffffu), 21, "a29188");
        var reader = Reader("a29188", 21);
        Assert.True(reader.TryReadBitsFromIntegerRange(0u, 0xfffffu, out uint read));
        Assert.Equal(0x12345u, read);
    }

    [Fact]
    public void WriteBitsFromIntegerRange_outside_the_range_writes_a_flag_then_the_whole_value()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange((ushort)2000, (ushort)0, (ushort)1000, allowOutsideRange: true), 18, "c1f400");
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange((ushort)500, (ushort)0, (ushort)1000, allowOutsideRange: true), 12, "bd10");

        var outside = Reader("c1f400", 18);
        Assert.True(outside.TryReadBitsFromIntegerRange((ushort)0, (ushort)1000, out ushort big, allowOutsideRange: true));
        Assert.Equal(2000, big);
        var inside = Reader("bd10", 12);
        Assert.True(inside.TryReadBitsFromIntegerRange((ushort)0, (ushort)1000, out ushort small, allowOutsideRange: true));
        Assert.Equal(500, small);
    }

    [Fact]
    public void WriteBitsFromIntegerRange_rejects_a_value_outside_the_range_unless_allowed()
    {
        var buffer = new byte[8];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BitStreamWriter(buffer);
            writer.WriteBitsFromIntegerRange((ushort)2000, (ushort)0, (ushort)1000);
        });
    }

    [Fact]
    public void A_single_value_range_takes_no_bits_and_reads_back_as_the_minimum()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange(7, 7, 7), 1, "80");
        var reader = Reader("80", 1);
        Assert.True(reader.TryReadBitsFromIntegerRange(7, 7, out int read));
        Assert.Equal(7, read);
    }

    [Theory]
    [InlineData(0.5f, -1f, 1f, "dfff80")]
    [InlineData(123.4f, 0f, 1000f, "8fcb80")]
    [InlineData(-1f, -1f, 1f, "800000")]
    [InlineData(1f, -1f, 1f, "ffff80")]
    public void WriteFloat16_matches_native(float value, float minimum, float maximum, string hex)
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteFloat16(value, minimum, maximum), 17, hex);
        var reader = Reader(hex, 17);
        Assert.True(reader.TryReadFloat16(minimum, maximum, out var read));
        Assert.Equal(value, read, (maximum - minimum) / 65535f);
    }

    [Fact]
    public void WriteNormVector_writes_three_float16s()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteNormVector(new Vector3(0.6f, -0.8f, 0f)), 49, "e6660cccbfff80");
        var reader = Reader("e6660cccbfff80", 49);
        Assert.True(reader.TryReadNormVector(out var read));
        AssertClose(new Vector3(0.6f, -0.8f, 0f), read, 0.0001f);
    }

    [Fact]
    public void WriteVector_writes_the_magnitude_then_the_compressed_direction()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteVector(new Vector3(3f, -4f, 12f)), 81, "a0a800004ec4ac4efb1300");
        var reader = Reader("a0a800004ec4ac4efb1300", 81);
        Assert.True(reader.TryReadVector(out var read));
        AssertClose(new Vector3(3f, -4f, 12f), read, 0.001f);
    }

    [Fact]
    public void WriteVector_of_zero_writes_only_the_magnitude()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteVector(Vector3.Zero), 33, "8000000000");
        var reader = Reader("8000000000", 33);
        Assert.True(reader.TryReadVector(out var read));
        Assert.Equal(Vector3.Zero, read);
    }

    [Fact]
    public void WriteNormQuat_writes_four_signs_and_three_components()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteNormQuat(new Quaternion(0f, 0.70710678f, 0f, 0.70710678f)), 53, "800005a8200000");
        AssertWritten((ref BitStreamWriter w) => w.WriteNormQuat(new Quaternion(0.5f, -0.5f, 0.5f, -0.5f)), 53, "d3fffbfffbfff8");

        var reader = Reader("d3fffbfffbfff8", 53);
        Assert.True(reader.TryReadNormQuat(out var read));
        Assert.Equal(-0.5f, read.W, 0.0001f);
        Assert.Equal(0.5f, read.X, 0.0001f);
        Assert.Equal(-0.5f, read.Y, 0.0001f);
        Assert.Equal(0.5f, read.Z, 0.0001f);
    }

    [Fact]
    public void WriteOrthMatrix_writes_the_rotation_as_a_quaternion()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteOrthMatrix(Matrix4x4.Identity), 53, "80000000000000");
        // RakNet's m00..m22 rows: (0,-1,0), (1,0,0), (0,0,1), a quarter turn about z.
        var quarterTurn = new Matrix4x4(0f, -1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
        AssertWritten((ref BitStreamWriter w) => w.WriteOrthMatrix(quarterTurn), 53, "8000000005a820");

        var reader = Reader("8000000005a820", 53);
        Assert.True(reader.TryReadOrthMatrix(out var read));
        Assert.Equal(0f, read.M11, 0.0001f);
        Assert.Equal(-1f, read.M12, 0.0001f);
        Assert.Equal(1f, read.M21, 0.0001f);
        Assert.Equal(0f, read.M22, 0.0001f);
        Assert.Equal(1f, read.M33, 0.0001f);
        Assert.Equal(1f, read.M44);
    }

    [Fact]
    public void WriteDelta_writes_one_bit_when_unchanged()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(5u, 5u), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(6u, 5u), 34, "c000000180");

        var unchanged = Reader("80", 2);
        var value = 5u;
        Assert.True(unchanged.TryReadDelta(ref value));
        Assert.Equal(5u, value);
        var changed = Reader("c000000180", 34);
        Assert.True(changed.TryReadDelta(ref value));
        Assert.Equal(6u, value);
    }

    [Fact]
    public void WriteCasted_writes_the_smaller_type()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteCasted<byte, int>(53), 9, "9a80");
        AssertWritten((ref BitStreamWriter w) => w.WriteCasted<short, int>(-2), 17, "ffff00");

        var reader = Reader("ffff00", 17);
        Assert.True(reader.TryReadCasted<short, int>(out var read));
        Assert.Equal(-2, read);
    }

    [Fact]
    public void WriteRakString_writes_a_length_then_aligned_bytes()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString("hello"), 64, "80028068656c6c6f");
        AssertWritten((ref BitStreamWriter w) => w.WriteRakString(""), 24, "800000");

        var reader = Reader("80028068656c6c6f", 64);
        Assert.True(reader.TryReadRakString(out var text));
        Assert.Equal("hello", text);
        var empty = Reader("800000", 24);
        Assert.True(empty.TryReadRakString(out var none));
        Assert.Equal("", none);
        Assert.Equal(0, empty.BitsRemaining);
    }

    [Fact]
    public void TryReadRakString_fails_when_the_bytes_are_missing()
    {
        var reader = Reader("800a8068", 32);
        Assert.False(reader.TryReadRakString(out _));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void WriteCompressedString_matches_native()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("Hello, World!", 256), 114, "800000141f02528d7d5c8cc9caea80");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("", 256), 9, "f800");
        // RakString::SerializeCompressed passes 0xFFFF as the limit (RakString.cpp:1228-1232).
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("hello", 0xFFFF), 58, "8000000630252840");

        var reader = Reader("800000141f02528d7d5c8cc9caea80", 114);
        Assert.True(reader.TryReadCompressedString(256, out var text));
        Assert.Equal("Hello, World!", text);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void WriteCompressedString_writes_at_most_one_less_than_the_limit()
    {
        // StringCompressor.cpp:381-384: a string as long as the limit loses its last character.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedString("abcdef", 4), 50, "8000000418fa00");
        var reader = Reader("8000000418fa00", 50);
        Assert.True(reader.TryReadCompressedString(256, out var text));
        Assert.Equal("abc", text);
    }

    [Fact]
    public void TryReadCompressedString_keeps_one_less_than_the_limit()
    {
        var reader = Reader("800000141f02528d7d5c8cc9caea80", 114);
        Assert.True(reader.TryReadCompressedString(5, out var text));
        Assert.Equal("Hell", text);
    }

    [Fact]
    public void The_default_Huffman_code_of_every_byte_matches_native()
    {
        var expected = NativeHuffmanCodes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(256, expected.Length);
        var tree = new HuffmanEncodingTree(StringCompressor.EnglishCharacterFrequencies);
        Span<byte> buffer = stackalloc byte[64];
        for (var value = 0; value < 256; value++)
        {
            var writer = new BitStreamWriter(buffer);
            tree.EncodeArray([(byte)value], ref writer);
            // The code padded to a byte; the code is the prefix of the padding.
            var reader = new BitStreamReader(writer.Written, writer.BitsWritten);
            var code = expected[value];
            Assert.Equal((code.Length + 7) / 8 * 8, writer.BitsWritten);
            for (var i = 0; i < code.Length; i++)
            {
                Assert.True(reader.TryReadBit(out var bit));
                Assert.True(bit == (code[i] == '1'), $"Byte {value}: bit {i} of {code}.");
            }
        }
    }

    [Fact]
    public void BitSerializer_writes_and_reads_with_one_body()
    {
        Span<byte> buffer = stackalloc byte[32];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var (a, b, c, d) = (0x1234, true, 2.5f, 77u);
        Assert.True(Serialize(ref writing, ref a, ref b, ref c, ref d));
        Assert.True(writing.IsWriting);
        var written = writing.Writer;

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var (ra, rb, rc, rd) = (0, false, 0f, 0u);
        Assert.True(Serialize(ref reading, ref ra, ref rb, ref rc, ref rd));
        Assert.Equal((a, b, c, d), (ra, rb, rc, rd));
        Assert.Equal(0, reading.Reader.BitsRemaining);
        Assert.False(reading.IsWriting);
    }

    [Fact]
    public void A_reading_BitSerializer_has_no_writer()
    {
        Assert.Throws<InvalidOperationException>(() => { _ = new BitSerializer(new BitStreamReader([1])).Writer.BitsWritten; });
    }

    [Fact]
    public void BitSerializer_writes_the_same_bits_as_the_writer()
    {
        Span<byte> buffer = stackalloc byte[16];
        var serializer = new BitSerializer(new BitStreamWriter(buffer));
        var flag = true;
        var value = (ushort)0x1234;
        serializer.Serialize(ref flag);
        serializer.Serialize(ref value);

        Assert.Equal(17, serializer.Writer.BitsWritten);
        Assert.Equal(Convert.FromHexString("891a00"), serializer.Writer.Written.ToArray());
    }

    [Fact]
    public void BitSerializer_reports_a_failed_read()
    {
        var serializer = new BitSerializer(new BitStreamReader([0x12]));
        var value = 0u;
        Assert.False(serializer.Serialize(ref value));
    }

    private static bool Serialize(ref BitSerializer s, ref int a, ref bool b, ref float c, ref uint d) =>
        s.Serialize(ref a) && s.Serialize(ref b) && s.Serialize(ref c) && s.SerializeCompressed(ref d);

    private static void AssertCompressed<T>(T value, int bits, string hex)
        where T : unmanaged, IBinaryInteger<T>
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(value), bits, hex);
        var reader = Reader(hex, bits);
        Assert.True(reader.TryReadCompressed(out T read));
        Assert.Equal(value, read);
        Assert.Equal(0, reader.BitsRemaining);
    }

    private delegate void WriteAction(ref BitStreamWriter writer);

    private static void AssertWritten(WriteAction write, int bits, string hex)
    {
        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        write(ref writer);
        Assert.Equal(bits, writer.BitsWritten);
        Assert.Equal(hex, Convert.ToHexStringLower(writer.Written));
    }

    /// <summary>A reader over native output, past the leading 1 bit.</summary>
    private static BitStreamReader Reader(string hex, int bits)
    {
        var reader = new BitStreamReader(Convert.FromHexString(hex), bits);
        Assert.True(reader.TryReadBit(out var lead) && lead);
        return reader;
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.Equal(expected.X, actual.X, tolerance);
        Assert.Equal(expected.Y, actual.Y, tolerance);
        Assert.Equal(expected.Z, actual.Z, tolerance);
    }

    // HuffmanEncodingTree::GenerateFromFrequencyTable(englishCharacterFrequencies): the code of bytes 0 to 255, root to leaf.
    private const string NativeHuffmanCodes = """
        0101011110001101 0101011110001100 0101011110001111 0101011110001110 0101011110001001 0101011110001000
        0101011110001011 0101011110001010 1001110100100101 1001110100100100 001101 1001110100100111 1001110100100110
        101010110110000 1001110100100001 1001110100100000 1001110100100011 1001110100100010 1001110100101101
        1001110100101100 1001110100101111 1001110100101110 1001110100101001 1001110100101000 1001110100101011
        1001110100101010 0111110010110101 0111110010110100 0111110010110111 0111110010110110 0111110010110001
        0111110010110000 111 0101011101 0111110011 0111110010110011 0111110010110010 01111100100 0111110010111101
        10101010 0111110101 0111110100 0011000111 0111110010111100 1101011 0101011111 011110 10011100 1010100010
        1001110101 0101011011 0101011110000 1010100011 01010101110100 010101011111 010101011101011 1001110111
        0111110010111111 00110000 1001110100111 0111110010111110 010101011101010 00110011011 001100101 0111110010111001
        01010100 101010000 10011101101 001100111 0101010110 010101100 0101011010 011111000 1101010 101010110111
        10101011011001 00110011010 0111110111 001100010 0011000110 10011101100 0101010111011 001100100 1010101100
        101010010 010101011100 010101011110 0101011100 010101111010 10101011010 010101111001 0111110010111000
        0111110010111011 0111110010111010 100111010011001 101010110110001 0111110010100101 0110 001111 101000 01110 000
        001110 100110 11000 0100 101010111 0111111 10010 110100 0010 1000 010100 0011001100 11001 11011 1011 01011
        1001111 101001 101010011 101011 010101010 0111110010100100 100111010011000 0111110010100111 1010101101101
        0111110010100110 0111110010100001 0111110010100000 0111110010100011 0111110010100010 0111110010101101
        0111110010101100 0111110010101111 0111110010101110 0111110010101001 0111110010101000 0111110010101011
        0111110010101010 0111110110010101 0111110110010100 0111110110010111 0111110110010110 0111110110010001
        0111110110010000 0111110110010011 0111110110010010 0111110110011101 0111110110011100 0111110110011111
        0111110110011110 0111110110011001 0111110110011000 0111110110011011 0111110110011010 0111110110000101
        0111110110000100 0111110110000111 0111110110000110 0111110110000001 0111110110000000 0111110110000011
        0111110110000010 0111110110001101 0111110110001100 0111110110001111 0111110110001110 0111110110001001
        0111110110001000 0111110110001011 0111110110001010 0111110110110101 0111110110110100 0111110110110111
        0111110110110110 0111110110110001 0111110110110000 0111110110110011 0111110110110010 0111110110111101
        0111110110111100 0111110110111111 0111110110111110 0111110110111001 0111110110111000 0111110110111011
        0111110110111010 0111110110100101 0111110110100100 0111110110100111 0111110110100110 0111110110100001
        0111110110100000 0111110110100011 0111110110100010 0111110110101101 0111110110101100 0111110110101111
        0111110110101110 0111110110101001 0111110110101000 0111110110101011 0111110110101010 1001110100010101
        1001110100010100 1001110100010111 1001110100010110 1001110100010001 1001110100010000 1001110100010011
        1001110100010010 1001110100011101 1001110100011100 1001110100011111 1001110100011110 1001110100011001
        1001110100011000 1001110100011011 1001110100011010 1001110100000101 1001110100000100 1001110100000111
        1001110100000110 1001110100000001 1001110100000000 1001110100000011 1001110100000010 1001110100001101
        1001110100001100 1001110100001111 1001110100001110 1001110100001001 1001110100001000 1001110100001011
        1001110100001010 0101011110110101 0101011110110100 0101011110110111 0101011110110110 0101011110110001
        0101011110110000 0101011110110011 0101011110110010 0101011110111101 0101011110111100 0101011110111111
        0101011110111110 0101011110111001 0101011110111000 0101011110111011 0101011110111010 1001110100110101
        1001110100110100 1001110100110111 1001110100110110
        """;
}
