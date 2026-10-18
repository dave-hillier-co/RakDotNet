using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class MessageIdTests
{
    [Theory]
    [InlineData(MessageId.ConnectedPing, 0x00)]
    [InlineData(MessageId.UnconnectedPing, 0x01)]
    [InlineData(MessageId.OpenConnectionRequest1, 0x05)]
    [InlineData(MessageId.OpenConnectionReply2, 0x08)]
    [InlineData(MessageId.ConnectionRequest, 0x09)]
    [InlineData(MessageId.ConnectionRequestAccepted, 0x10)]
    [InlineData(MessageId.NewIncomingConnection, 0x13)]
    [InlineData(MessageId.DisconnectionNotification, 0x15)]
    [InlineData(MessageId.IncompatibleProtocolVersion, 0x19)]
    [InlineData(MessageId.Timestamp, 0x1B)]
    [InlineData(MessageId.UnconnectedPong, 0x1C)]
    [InlineData(MessageId.AdvertiseSystem, 0x1D)]
    [InlineData(MessageId.UserPacketEnum, 0x86)]
    public void Values_match_raknet(MessageId id, byte expected)
    {
        Assert.Equal(expected, (byte)id);
    }
}
