using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class ApplicationHeadersTests
{
    [Fact]
    public void A_timestamp_header_is_the_id_then_the_time_big_endian()
    {
        // ID_TIMESTAMP (27), then RakNet::Time as BitStream writes a u64.
        Span<byte> buffer = stackalloc byte[9];
        var writer = new BitStreamWriter(buffer);

        new TimestampHeader(0x0102030405060708).Write(ref writer);

        Assert.Equal("1b0102030405060708", Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void A_timestamp_header_reads_its_time_and_leaves_the_reader_at_the_message_id()
    {
        var reader = new BitStreamReader(Convert.FromHexString("1b000000000000303986"));

        Assert.True(TimestampHeader.TryRead(ref reader, out var header));
        Assert.Equal(12345UL, header.Time);
        Assert.True(reader.TryReadByte(out var id));
        Assert.Equal(0x86, id);
    }

    [Fact]
    public void A_timestamp_header_with_another_id_or_too_few_bytes_does_not_read()
    {
        var wrongId = new BitStreamReader(Convert.FromHexString("860000000000003039"));
        Assert.False(TimestampHeader.TryRead(ref wrongId, out _));

        var truncated = new BitStreamReader(Convert.FromHexString("1b00000000"));
        Assert.False(TimestampHeader.TryRead(ref truncated, out _));
        Assert.Equal(0, truncated.BitPosition);
    }

    [Fact]
    public void An_out_of_band_header_is_the_id_the_guid_then_the_magic()
    {
        // RakPeer.cpp:2979-2985: ID_OUT_OF_BAND_INTERNAL (13), myGuid, then OFFLINE_MESSAGE_DATA_ID aligned.
        Span<byte> buffer = stackalloc byte[1 + 8 + OfflineMagic.Length];
        var writer = new BitStreamWriter(buffer);

        new OutOfBandHeader(new RakNetGuid(0x1122334455667788)).Write(ref writer);

        Assert.Equal("0d1122334455667788" + Convert.ToHexStringLower(OfflineMagic.Bytes), Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void An_out_of_band_header_reads_back_and_rejects_a_bad_magic()
    {
        var bytes = Convert.FromHexString("0d0000000000000063" + Convert.ToHexStringLower(OfflineMagic.Bytes));
        var reader = new BitStreamReader(bytes);
        Assert.True(OutOfBandHeader.TryRead(ref reader, out var header));
        Assert.Equal(new RakNetGuid(99), header.Sender);

        bytes[^1] ^= 0xff;
        var bad = new BitStreamReader(bytes);
        Assert.False(OutOfBandHeader.TryRead(ref bad, out _));
        Assert.Equal(0, bad.BitPosition);
    }
}
