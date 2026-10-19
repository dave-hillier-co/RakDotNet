using RakDotNet.Protocol.Wire;
using static RakDotNet.Protocol.Tests.Wire.NativeBits;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are known answers printed by native RakNet 1a169895 with tools/bitstream-vectors.
public class BitStreamPositioningTests
{
    [Fact]
    public void WriteBitsFromIntegerRange_with_an_explicit_bit_count_writes_that_many_bits()
    {
        // BitStream.h:1758-1784: the caller's requiredBits replace the width maximum - minimum needs.
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange((ushort)5, (ushort)0, (ushort)1000, 12), 13, "8280");
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange(300u, 0u, 1000u, 9), 10, "9640");
        AssertWritten((ref BitStreamWriter w) => w.WriteBitsFromIntegerRange((ushort)5000, (ushort)0, (ushort)1000, 12, allowOutsideRange: true), 18, "c4e200");

        var reader = Reader("8280", 13);
        Assert.True(reader.TryReadBitsFromIntegerRange((ushort)0, (ushort)1000, 12, out ushort value));
        Assert.Equal(5, value);
        reader = Reader("9640", 10);
        Assert.True(reader.TryReadBitsFromIntegerRange(0u, 1000u, 9, out uint wide));
        Assert.Equal(300u, wide);
        reader = Reader("c4e200", 18);
        Assert.True(reader.TryReadBitsFromIntegerRange((ushort)0, (ushort)1000, 12, out ushort outside, allowOutsideRange: true));
        Assert.Equal(5000, outside);
    }

    [Fact]
    public void An_explicit_bit_count_wider_than_the_type_is_rejected()
    {
        var buffer = new byte[8];
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var writer = new BitStreamWriter(buffer);
            writer.WriteBitsFromIntegerRange((byte)1, (byte)0, (byte)3, 9);
        });
    }

    [Fact]
    public void WriteBits_left_aligned_takes_the_high_bits_of_the_final_byte()
    {
        // BitStream.cpp:424-480 with rightAlignedBits false: the partial last byte is not shifted.
        AssertWritten((ref BitStreamWriter w) => w.WriteBits([0xab, 0xe0], 11, rightAlignedBits: false), 12, "d5f0");
        AssertWritten((ref BitStreamWriter w) => w.WriteBits([0x05], 3, rightAlignedBits: true), 4, "d0");
    }

    [Fact]
    public void WriteBits_left_aligned_keeps_the_unused_low_bits_of_the_final_byte_as_native_does()
    {
        // BitStream.cpp:448-460: the whole input byte is copied, so the bits past the count stay set and later writes OR
        // into them.
        AssertWritten((ref BitStreamWriter w) =>
        {
            w.WriteBits([0xff], 3, rightAlignedBits: false);
            w.WriteBit(false);
            w.WriteBit(false);
        }, 6, "ff");
    }

    [Fact]
    public void TryReadBits_left_aligned_leaves_the_final_byte_unshifted()
    {
        // BitStream.cpp:551-617: with alignBitsToRight false the last partial byte keeps the following bits of its
        // source byte in its low bits.
        var source = new BitStreamReader([0x5a, 0xc3, 0xa0], 17);
        Assert.True(source.IgnoreBits(1));
        var left = new byte[2];
        Assert.True(source.TryReadBits(left, 10, alignBitsToRight: false));
        Assert.Equal("b586", Convert.ToHexStringLower(left));
        Assert.Equal(11, source.BitPosition);

        source.BitPosition = 1;
        var right = new byte[2];
        Assert.True(source.TryReadBits(right, 10, alignBitsToRight: true));
        Assert.Equal("b502", Convert.ToHexStringLower(right));
    }

    [Fact]
    public void Write_from_a_reader_copies_bits_from_its_position()
    {
        // BitStream.cpp:193-237: copies from the source's read offset.
        Span<byte> buffer = stackalloc byte[8];
        var source = new BitStreamReader([0x5a, 0xc3, 0x80], 17);
        Assert.True(source.IgnoreBits(3));
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        Assert.Equal(10, writer.Write(source, 10));
        Assert.Equal(11, writer.BitsWritten);
        Assert.Equal("eb00", Convert.ToHexStringLower(writer.Written));

        writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        Assert.Equal(14, writer.Write(source));
        Assert.Equal(15, writer.BitsWritten);
        Assert.Equal("eb0e", Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void BitStream_Write_from_a_reader_advances_the_reader()
    {
        var source = new BitStreamReader([0x5a, 0xc3, 0x80], 17);
        Assert.True(source.IgnoreBits(3));
        using var stream = new BitStream();
        stream.WriteBit(true);
        stream.Write(ref source, 10);
        Assert.Equal("eb00", Convert.ToHexStringLower(stream.Written));
        Assert.Equal(13, source.BitPosition);
    }

    [Fact]
    public void Write_from_a_reader_stops_at_the_end_of_the_reader()
    {
        // BitStream.cpp:207: the bit loop stops when the source runs out.
        Span<byte> buffer = stackalloc byte[8];
        var source = new BitStreamReader([0xff], 4);
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        Assert.Equal(4, writer.Write(source, 10));
        Assert.Equal(5, writer.BitsWritten);
        Assert.Equal("f8", Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void A_reader_can_read_into_a_BitStream_and_fails_when_short()
    {
        // BitStream.cpp:246-270: Read(BitStream&, bits) fails when fewer bits are unread.
        var source = new BitStreamReader([0x5a, 0xc3, 0x80], 17);
        Assert.True(source.IgnoreBits(3));
        using var stream = new BitStream();
        stream.WriteBit(true);
        Assert.True(source.TryReadBits(stream, 10));
        Assert.Equal("eb00", Convert.ToHexStringLower(stream.Written));
        Assert.Equal(11, stream.BitsWritten);
        Assert.False(source.TryReadBits(stream, 5));
        Assert.Equal(13, source.BitPosition);
    }

    [Fact]
    public void BitStream_Write_from_a_reader_matches_the_writer()
    {
        var source = new BitStreamReader([0x5a, 0xc3, 0x80], 17);
        Assert.True(source.IgnoreBits(3));
        using var stream = new BitStream();
        stream.WriteBit(true);
        stream.Write(ref source);
        Assert.Equal(15, stream.BitsWritten);
        Assert.Equal("eb0e", Convert.ToHexStringLower(stream.Written));
    }

    [Fact]
    public void IgnoreBits_and_IgnoreBytes_skip_and_fail_past_the_end()
    {
        var reader = new BitStreamReader([1, 2, 3], 20);
        Assert.True(reader.IgnoreBytes(1));
        Assert.True(reader.TryReadByte(out var b));
        Assert.Equal(2, b);
        Assert.False(reader.IgnoreBits(5));
        Assert.Equal(16, reader.BitPosition);
        Assert.True(reader.IgnoreBits(4));
        Assert.Equal(0, reader.BitsRemaining);
        Assert.False(reader.IgnoreBytes(1));
    }

    [Fact]
    public void The_read_position_can_be_set_back_and_forward()
    {
        // SetReadOffset and ResetReadPointer (BitStream.h:508-511, BitStream.cpp:293).
        var reader = new BitStreamReader([0x12, 0x34]);
        Assert.True(reader.TryReadUInt16(out var first));
        reader.BitPosition = 8;
        Assert.True(reader.TryReadByte(out var second));
        Assert.Equal(0x34, second);
        reader.BitPosition = 0;
        Assert.True(reader.TryReadUInt16(out var again));
        Assert.Equal(first, again);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            var r = new BitStreamReader([1]);
            r.BitPosition = 9;
        });
    }

    [Fact]
    public void SetWriteOffset_rewinds_to_patch_a_field_and_restores_the_length()
    {
        // BitStream.cpp:924-927 only moves numberOfBitsUsed; a byte-aligned write replaces the byte.
        using var stream = new BitStream();
        stream.WriteUInt32(0x11223344);
        stream.SetWriteOffset(8);
        stream.WriteByte(0xaa);
        stream.SetWriteOffset(32);
        Assert.Equal(32, stream.BitsWritten);
        Assert.Equal("11aa3344", Convert.ToHexStringLower(stream.Written));
    }

    [Fact]
    public void SetWriteOffset_past_the_end_zero_fills_the_gap()
    {
        using var stream = new BitStream(1);
        stream.WriteByte(0xff);
        stream.SetWriteOffset(40);
        Assert.Equal("ff00000000", Convert.ToHexStringLower(stream.Written));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.SetWriteOffset(-1));
    }

    [Fact]
    public void PadWithZeroToByteLength_aligns_then_pads_and_never_shrinks()
    {
        // BitStream.cpp:744-754.
        using var stream = new BitStream();
        stream.WriteBit(true);
        stream.PadWithZeroToByteLength(4);
        Assert.Equal(32, stream.BitsWritten);
        Assert.Equal("80000000", Convert.ToHexStringLower(stream.Written));

        stream.Reset();
        stream.WriteUInt32(0x11223344);
        stream.WriteByte(0x55);
        stream.PadWithZeroToByteLength(4);
        Assert.Equal("1122334455", Convert.ToHexStringLower(stream.Written));

        Span<byte> buffer = stackalloc byte[4];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        writer.PadWithZeroToByteLength(3);
        Assert.Equal(24, writer.BitsWritten);
    }

    [Fact]
    public void WriteAlignedBytesSafe_writes_the_whole_length_and_at_most_the_maximum_bytes()
    {
        // BitStream.cpp:355-365: the length is the input's, the bytes are capped at maxBytesToWrite.
        AssertWritten((ref BitStreamWriter w) => w.WriteAlignedBytesSafe("abcdef"u8, 4), 72, "800000018061626364");
        AssertWritten((ref BitStreamWriter w) => w.WriteAlignedBytesSafe("abcdef"u8, 10), 88, "8000000180616263646566");
        AssertWritten((ref BitStreamWriter w) => w.WriteAlignedBytesSafe([], 10), 9, "f800");
    }

    [Fact]
    public void TryReadAlignedBytesSafe_caps_the_length_at_the_maximum()
    {
        // BitStream.cpp:395-404.
        var reader = Reader("8000000180616263646566", 88);
        Assert.True(reader.TryReadAlignedBytesSafe(4, out var capped));
        Assert.Equal("abcd"u8.ToArray(), capped.ToArray());
        Assert.Equal(16, reader.BitsRemaining);

        reader = Reader("8000000180616263646566", 88);
        Assert.True(reader.TryReadAlignedBytesSafe(10, out var whole));
        Assert.Equal("abcdef"u8.ToArray(), whole.ToArray());

        reader = Reader("f800", 9);
        Assert.True(reader.TryReadAlignedBytesSafe(10, out var empty));
        Assert.True(empty.IsEmpty);
        Assert.Equal(0, reader.BitsRemaining);

        reader = Reader("8000000180616263", 64);
        Assert.False(reader.TryReadAlignedBytesSafe(10, out _));
        Assert.Equal(1, reader.BitPosition);
    }
}
