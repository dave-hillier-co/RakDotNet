using System.Buffers;
using System.Net;
using System.Text;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire;

// The BitStream class forwards to BitStreamWriter, so expected bytes come from the writer the other tests check.
public class PooledBitStreamTests
{
    [Fact]
    public void Writes_the_same_bits_as_the_writer()
    {
        using var stream = new BitStream();
        stream.WriteBit(true);
        stream.WriteUInt16(0x1234);
        stream.WriteBits(0b101, 3);
        stream.WriteUInt24(new UInt24(0x010203));
        stream.WriteSingle(1f);

        Span<byte> buffer = stackalloc byte[32];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        writer.WriteUInt16(0x1234);
        writer.WriteBits(0b101, 3);
        writer.WriteUInt24(new UInt24(0x010203));
        writer.WriteSingle(1f);

        Assert.Equal(writer.BitsWritten, stream.BitsWritten);
        Assert.Equal(writer.BytesWritten, stream.BytesWritten);
        Assert.Equal(writer.Written.ToArray(), stream.Written.ToArray());
    }

    [Fact]
    public void Every_scalar_writer_member_forwards()
    {
        using var stream = new BitStream(1);
        stream.WriteBool(true);
        stream.WriteByte(0xAB);
        stream.WriteInt16(-2);
        stream.WriteUInt32(0x01020304);
        stream.WriteInt32(-1);
        stream.WriteUInt64(0x0102030405060708);
        stream.WriteInt64(-3);
        stream.WriteDouble(0.5);
        stream.WriteBytes([1, 2]);
        stream.WriteBits([0xFF, 0x03], 10);
        stream.WriteAlignedBytes([9]);
        stream.AlignToByte();

        var reader = stream.CreateReader();
        Assert.True(reader.TryReadBool(out var flag) && flag);
        Assert.True(reader.TryReadByte(out var b) && b == 0xAB);
        Assert.True(reader.TryReadInt16(out var s) && s == -2);
        Assert.True(reader.TryReadUInt32(out var u) && u == 0x01020304);
        Assert.True(reader.TryReadInt32(out var i) && i == -1);
        Assert.True(reader.TryReadUInt64(out var ul) && ul == 0x0102030405060708);
        Assert.True(reader.TryReadInt64(out var l) && l == -3);
        Assert.True(reader.TryReadDouble(out var d) && d == 0.5);
        Span<byte> two = stackalloc byte[2];
        Assert.True(reader.TryReadBytes(two));
        Assert.Equal([1, 2], two.ToArray());
        Assert.True(reader.TryReadBits(two, 10));
        Assert.Equal([0xFF, 0x03], two.ToArray());
        Assert.True(reader.TryReadAlignedBytes(1, out var aligned) && aligned[0] == 9);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void Grows_past_its_initial_capacity_keeping_what_was_written()
    {
        using var stream = new BitStream(1);
        stream.WriteBit(true);
        var payload = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
        stream.WriteBytes(payload);

        var reader = stream.CreateReader();
        Assert.True(reader.TryReadBit(out var lead) && lead);
        var read = new byte[1000];
        Assert.True(reader.TryReadBytes(read));
        Assert.Equal(payload, read);
        Assert.Equal(1001 * 8 - 7, stream.BitsWritten);
    }

    [Fact]
    public void Reset_empties_the_stream()
    {
        using var stream = new BitStream();
        stream.WriteUInt32(0xFFFFFFFF);
        stream.Reset();
        stream.WriteBits(0b1, 1);

        Assert.Equal(1, stream.BitsWritten);
        Assert.Equal([0x80], stream.Written.ToArray());
    }

    [Fact]
    public void BeginWrite_resumes_at_the_current_bit_and_EndWrite_commits()
    {
        using var stream = new BitStream(1);
        stream.WriteBit(true);
        var writer = stream.BeginWrite(4);
        writer.WriteUInt32(0x80000001);
        stream.EndWrite(writer);

        Assert.Equal(33, stream.BitsWritten);
        Assert.Equal(Convert.FromHexString("c000000080"), stream.Written.ToArray());
    }

    [Fact]
    public void EndWrite_rejects_a_writer_over_another_buffer()
    {
        using var stream = new BitStream();
        var other = new byte[8];

        Assert.Throws<ArgumentException>(() =>
        {
            var writer = new BitStreamWriter(other);
            writer.WriteByte(1);
            stream.EndWrite(writer);
        });
    }

    [Fact]
    public void Write_of_a_wire_message_grows_as_needed_off_a_byte_boundary()
    {
        using var stream = new BitStream(1);
        stream.WriteBit(true);
        stream.Write(new OutOfBandHeader(new RakNetGuid(0x0102030405060708)));

        Span<byte> buffer = stackalloc byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        new OutOfBandHeader(new RakNetGuid(0x0102030405060708)).Write(ref writer);

        Assert.Equal(writer.BitsWritten, stream.BitsWritten);
        Assert.Equal(writer.Written.ToArray(), stream.Written.ToArray());
    }

    [Fact]
    public void WriteAddress_writes_a_SystemAddress_or_an_endpoint()
    {
        using var stream = new BitStream();
        stream.WriteAddress(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 19132));
        stream.WriteAddress(new SystemAddress(IPAddress.Parse("10.0.0.2"), 80));

        var reader = stream.CreateReader();
        Assert.True(reader.TryReadAddress(out var endPoint));
        Assert.Equal(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 19132), endPoint);
        Assert.True(reader.TryReadSystemAddress(out var address));
        Assert.Equal(new SystemAddress(IPAddress.Parse("10.0.0.2"), 80), address);
    }

    [Fact]
    public void CreateReader_is_limited_to_the_bits_written()
    {
        using var stream = new BitStream();
        stream.WriteBits(0b11, 2);

        var reader = stream.CreateReader();
        Assert.Equal(2, reader.BitsRemaining);
    }

    [Fact]
    public void As_a_buffer_writer_it_aligns_then_appends_bytes()
    {
        using var stream = new BitStream(2);
        stream.WriteBit(true);
        IBufferWriter<byte> bufferWriter = stream;
        Encoding.ASCII.GetBytes("hello world", bufferWriter);

        Assert.Equal(8 + 11 * 8, stream.BitsWritten);
        Assert.Equal(0x80, stream.Written[0]);
        Assert.Equal("hello world", Encoding.ASCII.GetString(stream.Written[1..]));
    }

    [Fact]
    public void Advance_past_the_span_throws()
    {
        using var stream = new BitStream(4);
        var length = stream.GetSpan(4).Length;

        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Advance(length + 1));
    }

    [Fact]
    public void A_rented_stream_comes_back_empty()
    {
        var first = BitStream.Rent();
        first.WriteUInt64(ulong.MaxValue);
        first.Dispose();

        using var second = BitStream.Rent();
        Assert.Equal(0, second.BitsWritten);
        second.WriteByte(7);
        Assert.Equal([7], second.Written.ToArray());
    }

    [Fact]
    public void Disposing_a_rented_stream_twice_never_touches_a_later_renter()
    {
        var stale = BitStream.Rent();
        stale.Dispose();
        using var owner = BitStream.Rent();
        owner.WriteByte(7);

        stale.Dispose();
        using var other = BitStream.Rent();
        other.WriteByte(9);

        Assert.NotSame(owner, other);
        Assert.Equal([7], owner.Written.ToArray());
        Assert.Throws<ObjectDisposedException>(() => stale.WriteByte(1));
    }

    [Fact]
    public void A_disposed_stream_throws_and_disposing_twice_is_harmless()
    {
        var stream = new BitStream();
        stream.Dispose();
        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
        Assert.Throws<ObjectDisposedException>(() => _ = stream.BitsWritten);
    }
}
