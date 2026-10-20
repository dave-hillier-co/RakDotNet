using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Peer;

// The core reuses its message events and the session's deliveries, and queues datagrams in one reused buffer, so that
// the hot path allocates nothing. These tests pin down what reuse must not change.
public class PeerCoreReuseTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public PeerCoreReuseTests()
    {
        _client = _link.Attach(
            ClientAddress,
            new PeerCore(new PeerOptions { Guid = ClientGuid, LocalAddresses = LocalAddressList.Build([ClientAddress.Address], 50_001) })
        );
        _server = _link.Attach(
            ServerAddress,
            new PeerCore(
                new PeerOptions
                {
                    Guid = ServerGuid,
                    MaxIncomingConnections = 8,
                    LocalAddresses = LocalAddressList.Build([ServerAddress.Address], 19_132),
                }
            )
        );
    }

    [Fact]
    public void A_message_event_that_is_not_recycled_keeps_its_data_while_later_messages_arrive()
    {
        Connect();
        var first = SendToServer([(byte)MessageId.UserPacketEnum, 1, 2, 3]);
        for (var i = 0; i < 50; i++)
            SendToServer([(byte)MessageId.UserPacketEnum, 9, 9, 9, 9, 9, 9]);

        Assert.Equal([(byte)MessageId.UserPacketEnum, 1, 2, 3], first.Packet.ToArray());
    }

    [Fact]
    public void A_recycled_message_event_is_reused_for_a_later_message_with_its_sender_data_and_length()
    {
        Connect();
        var first = SendToServer([(byte)MessageId.UserPacketEnum, 1, 2, 3, 4, 5, 6, 7]);
        _server.Recycle(first);

        var second = SendToServer([(byte)MessageId.UserPacketEnum, 8]);

        Assert.Same(first, second);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 8], second.Packet.ToArray());
        Assert.Equal(16, second.BitLength);
        Assert.Equal(ClientAddress, second.Remote);
        Assert.Equal(ClientGuid, second.Guid);
    }

    [Fact]
    public void A_split_message_kept_after_its_event_is_recycled_is_not_written_over_by_the_next_message()
    {
        Connect();
        var large = Enumerable.Range(0, 3_000).Select(i => (byte)i).ToArray();
        large[0] = (byte)MessageId.UserPacketEnum;
        var split = SendToServer(large);
        var kept = split.Data;
        _server.Recycle(split);

        var next = SendToServer([(byte)MessageId.UserPacketEnum, 7, 7, 7]);

        Assert.Same(split, next);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 7, 7, 7], next.Packet.ToArray());
        Assert.Equal(large, kept.ToArray());
    }

    [Fact]
    public void Connection_events_keep_their_packets_while_the_messages_after_them_reuse_the_deliveries()
    {
        Connect();
        var incoming = _link.EventsOf(_server).OfType<IncomingConnectionCompleted>().Single();
        var outgoing = _link.EventsOf(_client).OfType<OutgoingConnectionCompleted>().Single();
        var incomingPacket = incoming.Packet.ToArray();
        var outgoingPacket = outgoing.Packet.ToArray();
        Assert.Equal((byte)MessageId.NewIncomingConnection, incomingPacket[0]);
        Assert.Equal((byte)MessageId.ConnectionRequestAccepted, outgoingPacket[0]);

        for (var i = 0; i < 20; i++)
        {
            var filler = Enumerable.Repeat((byte)0xEE, 120).ToArray();
            filler[0] = (byte)MessageId.UserPacketEnum;
            _server.Recycle(SendToServer(filler));
            _client.Send(ServerGuid, filler, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
            _server.Send(ClientGuid, filler, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
            _link.Run(TimeSpan.FromMilliseconds(5));
        }

        Assert.Equal(incomingPacket, incoming.Packet.ToArray());
        Assert.Equal(outgoingPacket, outgoing.Packet.ToArray());
    }

    [Fact]
    public void A_datagram_taken_without_a_copy_carries_its_destination_and_the_dont_fragment_flag_of_a_probe()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _client.Ping(ServerAddress, onlyIfAccepting: false, _link.NowUs);
        _client.Update(_link.NowUs + 1_000);

        var taken = new List<(IPEndPoint Remote, byte[] Data, bool DoNotFragment)>();
        while (_client.TryDequeueDatagram(out var remote, out var datagram, out var doNotFragment))
            taken.Add((remote, datagram.ToArray(), doNotFragment));

        Assert.Equal(2, taken.Count);
        Assert.All(taken, t => Assert.Equal(ServerAddress, t.Remote));
        Assert.Equal((byte)MessageId.UnconnectedPing, taken[0].Data[0]);
        Assert.False(taken[0].DoNotFragment);
        Assert.Equal((byte)MessageId.OpenConnectionRequest1, taken[1].Data[0]);
        Assert.True(taken[1].DoNotFragment);
        // OPEN_CONNECTION_REQUEST_1 is padded to the probed MTU less the IP and UDP headers.
        Assert.Equal(RakNetConstants.MtuSizes[0] - RakNetConstants.UdpHeaderSize, taken[1].Data.Length);
    }

    private MessageReceived SendToServer(byte[] message)
    {
        var count = _link.EventsOf(_server).Count;
        _client.Send(ServerGuid, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(_server).Skip(count).OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));
        return _link.EventsOf(_server).Skip(count).OfType<MessageReceived>().Single();
    }

    private void Connect()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(
            () => _link.EventsOf(_client).OfType<OutgoingConnectionCompleted>().Any()
                && _link.EventsOf(_server).OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(1)
        );
    }
}
