using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class UnconnectedPingTests
{
    // RakPeer.cpp:2101-2111: ID, RakNet::Time (uint64, RakNetTime.h:22 with __GET_TIME_64BIT=1 from
    // RakNetDefines.h:23), aligned magic, sender GUID.
    private const string PingHex = "01" + Codec.TimeHex + Codec.Magic + Codec.GuidHex;

    [Fact]
    public void Ping_encodes_as_id_time_magic_guid()
    {
        Assert.Equal(PingHex, Codec.EncodeHex(new UnconnectedPing(false, Codec.Time, Codec.Guid)));
    }

    [Fact]
    public void Open_connections_ping_uses_its_own_id()
    {
        // RakPeer.cpp:2102-2103
        Assert.Equal("02" + PingHex[2..], Codec.EncodeHex(new UnconnectedPing(true, Codec.Time, Codec.Guid)));
    }

    [Fact]
    public void Ping_decodes_every_field()
    {
        var ping = Codec.Decode<UnconnectedPing>(PingHex);

        Assert.False(ping.OpenConnectionsOnly);
        Assert.Equal(Codec.Time, ping.SendPingTime);
        Assert.Equal(Codec.Guid, ping.ClientGuid);
        Assert.True(Codec.Decode<UnconnectedPing>("02" + PingHex[2..]).OpenConnectionsOnly);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ping_round_trips(bool openConnectionsOnly)
    {
        var ping = new UnconnectedPing(openConnectionsOnly, ulong.MaxValue, 42);

        Assert.True(Codec.TryDecode<UnconnectedPing>(Codec.Encode(ping), out var decoded));
        Assert.Equal(ping, decoded);
    }

    [Fact]
    public void Ping_without_a_guid_is_accepted_as_the_cpp_server_does()
    {
        // RakPeer.cpp:4636 only requires ID + time + magic (25 bytes); the GUID read at 4647 may fail and
        // leaves UNASSIGNED_RAKNET_GUID.
        var ping = Codec.Decode<UnconnectedPing>(PingHex[..50]);

        Assert.Null(ping.ClientGuid);
        Assert.Equal(PingHex[..50], Codec.EncodeHex(ping));
    }

    [Fact]
    public void Ping_with_a_partial_guid_decodes_as_having_no_guid()
    {
        Assert.Null(Codec.Decode<UnconnectedPing>(PingHex[..60]).ClientGuid);
    }

    [Fact]
    public void Ping_shorter_than_id_time_and_magic_fails()
    {
        Codec.AssertPrefixesShorterThanFail<UnconnectedPing>(Convert.FromHexString(PingHex), 25);
    }

    [Fact]
    public void Ping_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<UnconnectedPing>(
            Convert.FromHexString(PingHex),
            MessageId.UnconnectedPing,
            MessageId.UnconnectedPingOpenConnections
        );
    }

    [Fact]
    public void Ping_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<UnconnectedPing>(Convert.FromHexString(PingHex), 9);
    }
}
