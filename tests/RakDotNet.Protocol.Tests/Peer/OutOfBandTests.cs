using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// AdvertiseSystem and SendOutOfBand (RakPeer.cpp:2683-2689, 2979-3040) and their receipt (RakPeer.cpp:4706-4730).
public class OutOfBandTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public OutOfBandTests()
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        // Accepting nobody: out-of-band messages need neither a connection nor a free slot.
        _server = _link.Attach(ServerAddress, new PeerCore(new PeerOptions { Guid = ServerGuid }));
    }

    private IReadOnlyList<PeerEvent> ServerEvents => _link.EventsOf(_server);

    [Fact]
    public void Advertising_delivers_id_advertise_system_and_the_data()
    {
        _client.AdvertiseSystem(ServerAddress, "lan game"u8);
        _link.Run(TimeSpan.FromMilliseconds(5));

        var sent = Assert.Single(_link.Delivered);
        Assert.Equal(Encode(new AdvertiseSystem(ClientGuid, "lan game"u8.ToArray())), sent.Data);
        var advertised = Assert.IsType<AdvertiseSystemReceived>(Assert.Single(ServerEvents));
        Assert.Equal(ClientAddress, advertised.Remote);
        Assert.Equal(ClientGuid, advertised.Guid);
        Assert.Equal("lan game"u8.ToArray(), advertised.Data.ToArray());
        // RakPeer.cpp:4719-4724: the header is replaced by the ID_ADVERTISE_SYSTEM byte.
        Assert.Equal([(byte)MessageId.AdvertiseSystem, .. "lan game"u8.ToArray()], advertised.Packet.ToArray());
    }

    [Fact]
    public void Advertising_with_no_data_delivers_the_id_alone()
    {
        _client.AdvertiseSystem(ServerAddress, default);
        _link.Run(TimeSpan.FromMilliseconds(5));

        Assert.Equal([(byte)MessageId.AdvertiseSystem], Assert.Single(ServerEvents).Packet.ToArray());
    }

    [Fact]
    public void Other_out_of_band_data_is_delivered_after_id_out_of_band_internal()
    {
        _client.SendOutOfBand(ServerAddress, [(byte)MessageId.UserPacketEnum, 1, 2]);
        _link.Run(TimeSpan.FromMilliseconds(5));

        var received = Assert.IsType<OutOfBandReceived>(Assert.Single(ServerEvents));
        Assert.Equal(ClientGuid, received.Guid);
        Assert.Equal(ClientAddress, received.Remote);
        // RakPeer.cpp:4726-4729.
        Assert.Equal([(byte)MessageId.OutOfBandInternal, (byte)MessageId.UserPacketEnum, 1, 2], received.Packet.ToArray());
    }

    [Fact]
    public void A_connected_system_can_still_advertise()
    {
        _server.MaxIncomingConnections = 1;
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<IncomingConnectionCompleted>().Any(), TimeSpan.FromSeconds(1));

        _client.AdvertiseSystem(ServerAddress, [7]);
        _link.RunUntil(() => ServerEvents.OfType<AdvertiseSystemReceived>().Any(), TimeSpan.FromSeconds(1));

        Assert.Equal([(byte)MessageId.AdvertiseSystem, 7], ServerEvents.OfType<AdvertiseSystemReceived>().Single().Packet.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(400)]
    public void Out_of_band_datagrams_the_cpp_receiver_ignores_are_ignored(int payloadLength)
    {
        // RakPeer.cpp:4706-4708: the payload must be 1 to 399 bytes. The datagram is still an offline message, so it
        // goes no further.
        var datagram = Encode(new OutOfBandMessage(ClientGuid, new byte[payloadLength]));

        _server.Receive(ClientAddress, datagram, _link.NowUs);
        _server.Update(_link.NowUs);

        Assert.Empty(Events(_server));
        Assert.Empty(Datagrams(_server));
    }

    [Fact]
    public void The_longest_out_of_band_payload_the_cpp_receiver_accepts_is_delivered()
    {
        _server.Receive(ClientAddress, Encode(new OutOfBandMessage(ClientGuid, new byte[OutOfBandMessage.MaxPayloadLength])), _link.NowUs);

        Assert.Equal(1 + OutOfBandMessage.MaxPayloadLength, Assert.Single(Events(_server)).Packet.Length);
    }

    [Fact]
    public void A_banned_address_cannot_advertise()
    {
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);

        _client.AdvertiseSystem(ServerAddress, [7]);
        _link.Run(TimeSpan.FromMilliseconds(5));

        Assert.Empty(ServerEvents);
        Assert.Contains(_link.Delivered, d => d.From.Equals(ServerAddress) && d.Data[0] == (byte)MessageId.ConnectionBanned);
    }

    [Fact]
    public void Data_longer_than_the_receiver_accepts_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _client.AdvertiseSystem(ServerAddress, new byte[AdvertiseSystem.MaxDataLength + 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _client.SendOutOfBand(ServerAddress, new byte[OutOfBandMessage.MaxPayloadLength + 1]));
        Assert.Empty(Datagrams(_client));
    }
}
