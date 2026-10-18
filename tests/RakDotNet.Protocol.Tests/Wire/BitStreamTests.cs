using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are derived by hand from BitStream.cpp/BitStream.h (RakNet 1a169895) on a little-endian host.
public class BitStreamTests
{
    [Fact]
    public void Bits_are_packed_most_significant_first()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteBit(true);
            w.WriteBits(0b101, 3);
        }, out var bits);

        Assert.Equal(4, bits);
        Assert.Equal([0xD0], bytes);
    }

    [Fact]
    public void Multi_byte_values_are_big_endian_and_not_aligned()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteBit(true);
            w.WriteUInt16(0x1234);
        }, out var bits);

        Assert.Equal(17, bits);
        Assert.Equal([0x89, 0x1A, 0x00], bytes);
    }

    [Fact]
    public void UInt32_UInt64_and_floats_are_big_endian()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteUInt32(0x01020304);
            w.WriteUInt64(0x0A0B0C0D0E0F1011);
            w.WriteSingle(1.0f);
        }, out _);

        Assert.Equal(
            [0x01, 0x02, 0x03, 0x04, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x3F, 0x80, 0x00, 0x00],
            bytes
        );
    }

    [Fact]
    public void UInt24_aligns_then_writes_little_endian()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteBit(true);
            w.WriteUInt24(new UInt24(0x123456));
        }, out var bits);

        Assert.Equal(32, bits);
        Assert.Equal([0x80, 0x56, 0x34, 0x12], bytes);
    }

    [Fact]
    public void Ipv4_address_is_inverted_with_big_endian_port()
    {
        var bytes = Write((ref BitStreamWriter w) => w.WriteAddress(new IPEndPoint(IPAddress.Loopback, 19132)), out _);

        Assert.Equal([0x04, 0x80, 0xFF, 0xFF, 0xFE, 0x4A, 0xBC], bytes);
    }

    [Fact]
    public void Partial_trailing_byte_uses_its_low_bits()
    {
        var bytes = Write((ref BitStreamWriter w) => w.WriteBits([0xAB, 0x0C], 12), out var bits);

        Assert.Equal(12, bits);
        Assert.Equal([0xAB, 0xC0], bytes);
    }

    [Fact]
    public void Align_pads_with_zero_bits_and_is_a_no_op_when_aligned()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.AlignToByte();
            w.WriteBit(true);
            w.AlignToByte();
            w.WriteAlignedBytes([0xFF]);
        }, out var bits);

        Assert.Equal(16, bits);
        Assert.Equal([0x80, 0xFF], bytes);
    }

    [Fact]
    public void Writing_past_the_buffer_throws()
    {
        var threw = false;
        try
        {
            var w = new BitStreamWriter(new byte[1]);
            w.WriteUInt16(1);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void Reads_round_trip_every_primitive_at_an_unaligned_offset()
    {
        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteBits(0b011, 3);
            w.WriteByte(0xA5);
            w.WriteInt16(-2);
            w.WriteInt32(-3);
            w.WriteInt64(-4);
            w.WriteDouble(Math.PI);
            w.WriteBool(true);
            w.WriteUInt24(UInt24.MaxValue);
            w.WriteAddress(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 65535));
        }, out var bits);

        var r = new BitStreamReader(bytes, bits);
        Assert.True(r.TryReadBits(3, out var three));
        Assert.True(r.TryReadByte(out var b));
        Assert.True(r.TryReadInt16(out var s));
        Assert.True(r.TryReadInt32(out var i));
        Assert.True(r.TryReadInt64(out var l));
        Assert.True(r.TryReadDouble(out var d));
        Assert.True(r.TryReadBool(out var flag));
        Assert.True(r.TryReadUInt24(out var u24));
        Assert.True(r.TryReadAddress(out var address));

        Assert.Equal(0b011, three);
        Assert.Equal(0xA5, b);
        Assert.Equal(-2, s);
        Assert.Equal(-3, i);
        Assert.Equal(-4, l);
        Assert.Equal(Math.PI, d);
        Assert.True(flag);
        Assert.Equal(UInt24.MaxValue, u24);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 65535), address);
        Assert.Equal(0, r.BitsRemaining);
    }

    [Fact]
    public void Reading_past_the_end_fails_without_moving()
    {
        var r = new BitStreamReader([0xFF], 4);

        Assert.False(r.TryReadByte(out _));
        Assert.Equal(0, r.BitPosition);
        Assert.True(r.TryReadBits(4, out var nibble));
        Assert.Equal(0xF, nibble);
        Assert.False(r.TryReadBit(out _));
    }

    [Fact]
    public void Aligned_bytes_are_sliced_without_copying()
    {
        byte[] data = [0x80, 0x01, 0x02, 0x03];
        var r = new BitStreamReader(data);
        r.TryReadBit(out _);

        Assert.True(r.TryReadAlignedBytes(3, out var slice));
        Assert.Equal([0x01, 0x02, 0x03], slice.ToArray());
        Assert.False(r.TryReadAlignedBytes(1, out _));
    }

    [Fact]
    public void Address_with_unsupported_version_fails_without_moving()
    {
        var r = new BitStreamReader([0x06, 0x00, 0x00]);

        Assert.False(r.TryReadAddress(out var address));
        Assert.Null(address);
        Assert.Equal(0, r.BitPosition);
    }

    [Fact]
    public void Truncated_uint24_fails()
    {
        var r = new BitStreamReader([0x01, 0x02]);

        Assert.False(r.TryReadUInt24(out _));
    }

    private delegate void WriteAction(ref BitStreamWriter writer);

    private static byte[] Write(WriteAction write, out int bits)
    {
        var writer = new BitStreamWriter(new byte[64]);
        write(ref writer);
        bits = writer.BitsWritten;
        return writer.Written.ToArray();
    }
}
