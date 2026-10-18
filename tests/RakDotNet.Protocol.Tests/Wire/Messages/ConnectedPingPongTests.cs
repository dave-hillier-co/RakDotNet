using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class ConnectedPingTests
{
    // RakPeer.cpp:4108-4110: ID, RakNet::Time.
    private const string PingHex = "00" + Codec.TimeHex;

    [Fact]
    public void Ping_encodes_as_id_and_time()
    {
        Assert.Equal(PingHex, Codec.EncodeHex(new ConnectedPing(Codec.Time)));
    }

    [Fact]
    public void Ping_decodes_the_time()
    {
        Assert.Equal(Codec.Time, Codec.Decode<ConnectedPing>(PingHex).SendPingTime);
    }

    [Fact]
    public void Ping_round_trips()
    {
        Assert.True(Codec.TryDecode<ConnectedPing>(Codec.Encode(new ConnectedPing(ulong.MaxValue)), out var decoded));
        Assert.Equal(new ConnectedPing(ulong.MaxValue), decoded);
    }

    [Fact]
    public void Ping_with_trailing_bytes_fails_as_the_cpp_peer_requires_exactly_9_bytes()
    {
        // RakPeer.cpp:6077: byteSize == sizeof(unsigned char) + sizeof(RakNet::Time).
        Assert.False(Codec.TryDecode<ConnectedPing>(Convert.FromHexString(PingHex + "00"), out _));
    }

    [Fact]
    public void Truncated_ping_fails()
    {
        Codec.AssertAllTruncationsFail<ConnectedPing>(Convert.FromHexString(PingHex));
    }

    [Fact]
    public void Ping_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<ConnectedPing>(Convert.FromHexString(PingHex), MessageId.ConnectedPing);
    }
}

public class ConnectedPongTests
{
    // RakPeer.cpp:6085-6087: ID, the echoed ping time, the responder's time.
    private const string PongHex = "03" + Codec.TimeHex + Codec.OtherTimeHex;

    [Fact]
    public void Pong_encodes_as_id_ping_time_pong_time()
    {
        Assert.Equal(PongHex, Codec.EncodeHex(new ConnectedPong(Codec.Time, Codec.OtherTime)));
    }

    [Fact]
    public void Pong_decodes_both_times()
    {
        var pong = Codec.Decode<ConnectedPong>(PongHex);

        Assert.Equal(Codec.Time, pong.SendPingTime);
        Assert.Equal(Codec.OtherTime, pong.SendPongTime);
    }

    [Fact]
    public void Pong_round_trips()
    {
        var pong = new ConnectedPong(1, ulong.MaxValue);

        Assert.True(Codec.TryDecode<ConnectedPong>(Codec.Encode(pong), out var decoded));
        Assert.Equal(pong, decoded);
    }

    [Fact]
    public void Pong_with_trailing_bytes_fails_as_the_cpp_peer_requires_exactly_17_bytes()
    {
        // RakPeer.cpp:6060: byteSize == sizeof(unsigned char) + sizeof(RakNet::Time) * 2.
        Assert.False(Codec.TryDecode<ConnectedPong>(Convert.FromHexString(PongHex + "00"), out _));
    }

    [Fact]
    public void Truncated_pong_fails()
    {
        Codec.AssertAllTruncationsFail<ConnectedPong>(Convert.FromHexString(PongHex));
    }

    [Fact]
    public void Pong_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<ConnectedPong>(Convert.FromHexString(PongHex), MessageId.ConnectedPong);
    }
}

public class DisconnectionNotificationTests
{
    [Fact]
    public void Notification_is_the_id_byte_alone()
    {
        // RakPeer.cpp:3559-3560
        Assert.Equal("15", Codec.EncodeHex(new DisconnectionNotification()));
    }

    [Fact]
    public void Notification_round_trips()
    {
        Assert.True(Codec.TryDecode<DisconnectionNotification>(Codec.Encode(new DisconnectionNotification()), out var decoded));
        Assert.Equal(new DisconnectionNotification(), decoded);
    }

    [Fact]
    public void Notification_with_trailing_bytes_is_accepted_as_the_cpp_peer_only_checks_the_id()
    {
        // RakPeer.cpp:6095
        Assert.True(Codec.TryDecode<DisconnectionNotification>(Convert.FromHexString("15ff"), out _));
    }

    [Fact]
    public void Empty_input_fails()
    {
        Assert.False(Codec.TryDecode<DisconnectionNotification>([], out _));
    }

    [Fact]
    public void Notification_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<DisconnectionNotification>([0x15], MessageId.DisconnectionNotification);
    }
}
