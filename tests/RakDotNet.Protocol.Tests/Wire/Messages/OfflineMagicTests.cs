using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OfflineMagicTests
{
    [Fact]
    public void Magic_is_the_sixteen_bytes_of_OFFLINE_MESSAGE_DATA_ID()
    {
        // RakPeer.cpp:135
        Assert.Equal(Codec.Magic, Convert.ToHexStringLower(OfflineMagic.Bytes));
        Assert.Equal(16, OfflineMagic.Length);
    }

    [Fact]
    public void Write_aligns_to_a_byte_boundary_first()
    {
        // RakPeer.cpp:2109 and every other send site use WriteAlignedBytes.
        var buffer = new byte[32];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        OfflineMagic.Write(ref writer);

        Assert.Equal("80" + Codec.Magic, Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void TryRead_consumes_the_magic_when_it_matches()
    {
        var bytes = Convert.FromHexString(Codec.Magic + "ab");
        var reader = new BitStreamReader(bytes);

        Assert.True(OfflineMagic.TryRead(ref reader));
        Assert.True(reader.TryReadByte(out var next));
        Assert.Equal(0xAB, next);
    }

    [Fact]
    public void TryRead_fails_without_moving_when_the_magic_differs()
    {
        var bytes = Convert.FromHexString(Codec.Magic);
        bytes[15] = 0x79;
        var reader = new BitStreamReader(bytes);

        Assert.False(OfflineMagic.TryRead(ref reader));
        Assert.Equal(0, reader.BitPosition);
    }

    [Fact]
    public void TryRead_fails_without_moving_when_truncated()
    {
        var bytes = Convert.FromHexString(Codec.Magic)[..15];
        var reader = new BitStreamReader(bytes);

        Assert.False(OfflineMagic.TryRead(ref reader));
        Assert.Equal(0, reader.BitPosition);
    }

    [Fact]
    public void Matches_checks_the_first_sixteen_bytes()
    {
        Assert.True(OfflineMagic.Matches(Convert.FromHexString(Codec.Magic + "ff")));
        Assert.False(OfflineMagic.Matches(Convert.FromHexString(Codec.Magic).AsSpan(0, 15)));
        Assert.False(OfflineMagic.Matches(new byte[16]));
    }
}
