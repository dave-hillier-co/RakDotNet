using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class NewIncomingConnectionTests
{
    // RakPeer.cpp:6194-6199 (client) and 6018-6025 (server): ID, the server's address, 10 internal addresses,
    // the server time echoed from ID_CONNECTION_REQUEST_ACCEPTED, the client's time.
    private const string Unassigned = "04ffffffff0000";
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 19132);
    private static readonly string ExpectedHex =
        "13" + "0480fffffe4abc" + string.Concat(Enumerable.Repeat(Unassigned, 10)) + Codec.TimeHex + Codec.OtherTimeHex;

    [Fact]
    public void New_incoming_connection_encodes_with_ten_internal_addresses()
    {
        var message = new NewIncomingConnection(Server, InternalAddressList.CreateUnassigned(), Codec.Time, Codec.OtherTime);

        var bytes = Codec.Encode(message);

        Assert.Equal(ExpectedHex, Convert.ToHexStringLower(bytes));
        Assert.Equal(94, bytes.Length);
    }

    [Fact]
    public void New_incoming_connection_decodes_every_field()
    {
        var message = Codec.Decode<NewIncomingConnection>(ExpectedHex);

        Assert.Equal(Server, message.ServerAddress);
        Assert.Equal(InternalAddressList.CreateUnassigned(), message.InternalAddresses);
        Assert.Equal(Codec.Time, message.SendPingTime);
        Assert.Equal(Codec.OtherTime, message.SendPongTime);
    }

    [Fact]
    public void New_incoming_connection_round_trips()
    {
        var message = new NewIncomingConnection(
            new IPEndPoint(IPAddress.Parse("10.1.2.3"), 1),
            InternalAddressList.Pad([new IPEndPoint(IPAddress.Parse("10.9.9.9"), 2), new IPEndPoint(IPAddress.Parse("10.9.9.8"), 3)]),
            ulong.MaxValue,
            0
        );

        Assert.True(Codec.TryDecode<NewIncomingConnection>(Codec.Encode(message), out var decoded));
        Assert.Equal(message.ServerAddress, decoded.ServerAddress);
        Assert.Equal(message.InternalAddresses, decoded.InternalAddresses);
        Assert.Equal(message.SendPingTime, decoded.SendPingTime);
        Assert.Equal(message.SendPongTime, decoded.SendPongTime);
    }

    [Fact]
    public void Internal_address_count_is_configurable()
    {
        var bytes = Codec.Encode(new NewIncomingConnection(Server, InternalAddressList.CreateUnassigned(1), 1, 2));
        var reader = new BitStreamReader(bytes);

        Assert.True(NewIncomingConnection.TryRead(ref reader, out var decoded, internalAddressCount: 1));
        Assert.Single(decoded.InternalAddresses);
    }

    [Fact]
    public void Truncated_new_incoming_connection_fails()
    {
        Codec.AssertAllTruncationsFail<NewIncomingConnection>(Convert.FromHexString(ExpectedHex));
    }

    [Fact]
    public void New_incoming_connection_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<NewIncomingConnection>(Convert.FromHexString(ExpectedHex), MessageId.NewIncomingConnection);
    }
}
