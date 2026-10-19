using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Peer;

// SetMaximumIncomingConnections and the slot limit (RakPeer.cpp:900-914, 3573-3592, 3595-3770, 4038-4041, 5040-5048,
// 5340-5356), cross connections (RakPeer.cpp:4941-4954, 5976-6000, 6127-6220) and CancelConnectionAttempt
// (RakPeer.cpp:1686-1710).
public class ConnectionLimitTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);
    private static readonly IPEndPoint FirstClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint SecondClientAddress = new(IPAddress.Parse("10.0.0.3"), 50_003);
    private static readonly IPEndPoint ThirdPeerAddress = new(IPAddress.Parse("10.0.0.4"), 50_004);

    private readonly InMemoryLink _link = new();

    // Incoming limit.

    [Fact]
    public void A_server_refuses_clients_beyond_its_incoming_limit_until_the_limit_is_raised()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 1);
        var first = Peer(FirstClientAddress, 1);
        var second = Peer(SecondClientAddress, 2);
        Assert.Equal(1, server.MaxIncomingConnections);

        ConnectAndWait(first, ServerAddress);
        Assert.Equal(MessageId.NoFreeIncomingConnections, ConnectAndFail(second, ServerAddress).Reason);

        server.MaxIncomingConnections = 2;
        ConnectAndWait(second, ServerAddress);
        Assert.Equal(2, server.ActiveSystemCount);
    }

    [Fact]
    public void Only_connections_the_remote_system_started_count_against_the_incoming_limit()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 1);
        Peer(ThirdPeerAddress, 3, maxIncoming: 1);
        var client = Peer(FirstClientAddress, 1);

        // GetNumberOfRemoteInitiatedConnections, RakPeer.cpp:3573-3592.
        ConnectAndWait(server, ThirdPeerAddress);
        ConnectAndWait(client, ServerAddress);

        Assert.Equal(2, server.ActiveSystemCount);
    }

    [Fact]
    public void Clients_still_completing_their_handshake_do_not_count_against_the_incoming_limit()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 1);
        var first = Peer(FirstClientAddress, 1);
        var second = Peer(SecondClientAddress, 2);

        // Only CONNECTED systems count (RakPeer.cpp:3584-3587), so two clients that ask together both get in.
        first.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        second.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(
            () => _link.EventsOf(server).OfType<IncomingConnectionCompleted>().Count() == 2,
            TimeSpan.FromSeconds(1)
        );

        Assert.Equal(2, server.ActiveSystemCount);
    }

    [Fact]
    public void Lowering_the_limit_does_not_drop_connected_clients()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 2);
        var client = Peer(FirstClientAddress, 1);
        ConnectAndWait(client, ServerAddress);

        server.MaxIncomingConnections = 0;
        _link.Run(TimeSpan.FromSeconds(1), stepUs: 10_000);

        Assert.Equal(ConnectionState.IsConnected, server.GetConnectionState(FirstClientAddress));
    }

    // Slot limit.

    [Fact]
    public void A_server_with_no_free_slot_answers_but_the_client_times_out_in_the_connected_handshake()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 1, maxConnections: 1);
        Peer(ThirdPeerAddress, 3, maxIncoming: 1);
        var client = Peer(FirstClientAddress, 1);
        ConnectAndWait(server, ThirdPeerAddress);

        // The incoming limit allows it, so ID_OPEN_CONNECTION_REPLY_2 goes out although no slot is free
        // (RakPeer.cpp:5366-5417); the client's ID_CONNECTION_REQUEST then comes from an unknown system and is ignored
        // (RakPeer.cpp:5454-5468), until the client gives up after 10 s (RakPeer.cpp:5877-5881).
        var startMs = _link.NowMs;
        var failed = ConnectAndFail(client, ServerAddress, TimeSpan.FromSeconds(12));

        Assert.Equal(MessageId.ConnectionAttemptFailed, failed.Reason);
        Assert.InRange(_link.NowMs - startMs, 10_000, 10_500);
        Assert.Equal(1, server.ActiveSystemCount);
    }

    [Fact]
    public void A_client_with_no_free_slot_fails_the_attempt_when_the_server_answers()
    {
        Peer(ServerAddress, 2, maxIncoming: 1);
        Peer(ThirdPeerAddress, 3, maxIncoming: 1);
        var client = Peer(FirstClientAddress, 1, maxConnections: 1);
        ConnectAndWait(client, ThirdPeerAddress);

        var failed = ConnectAndFail(client, ServerAddress);

        // RakPeer.cpp:5040-5048: the packet is the ID alone, with the server's GUID.
        Assert.Equal([(byte)MessageId.ConnectionAttemptFailed], failed.Packet.ToArray());
        Assert.Equal(2ul, failed.Guid);
        Assert.Equal(ServerAddress, failed.Remote);
    }

    // Cross connections.

    [Fact]
    public void Two_peers_that_accept_connections_and_connect_to_each_other_at_once_end_up_with_one_connection()
    {
        var a = Peer(FirstClientAddress, 1, maxIncoming: 1);
        var b = Peer(ServerAddress, 2, maxIncoming: 1);

        a.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        b.Connect(FirstClientAddress, default, 12, 500, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(200));

        // Each takes the other's ID_OPEN_CONNECTION_REQUEST_2, then its own ID_OPEN_CONNECTION_REPLY_2 finds the slot
        // that made and turns it into REQUESTED_CONNECTION (RakPeer.cpp:4941-4954). Each then accepts the other's
        // ID_CONNECTION_REQUEST (RakPeer.cpp:5983-5986) and completes on the other's acceptance.
        foreach (var (peer, remote, guid) in new[] { (a, ServerAddress, 2ul), (b, FirstClientAddress, 1ul) })
        {
            var completed = Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(peer)));
            Assert.Equal(remote, completed.Remote);
            Assert.Equal(guid, completed.Guid);
            Assert.Equal(ConnectionState.IsConnected, peer.GetConnectionState(remote));
            Assert.Equal(1, peer.ActiveSystemCount);
        }

        a.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, 1], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        b.Send(FirstClientAddress, [(byte)MessageId.UserPacketEnum, 2], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(20));
        Assert.Equal([(byte)MessageId.UserPacketEnum, 2], _link.EventsOf(a).OfType<MessageReceived>().Single().Packet.ToArray());
        Assert.Equal([(byte)MessageId.UserPacketEnum, 1], _link.EventsOf(b).OfType<MessageReceived>().Single().Packet.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Two_peers_that_connect_to_each_other_a_little_apart_end_up_connected_once(int lagMs)
    {
        var a = Peer(FirstClientAddress, 1, maxIncoming: 1);
        var b = Peer(ServerAddress, 2, maxIncoming: 1);

        a.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(lagMs));
        b.Connect(FirstClientAddress, default, 12, 500, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(200));

        // However the handshakes interleave, each peer holds one connection to the other and nothing failed.
        Assert.Equal(ConnectionState.IsConnected, a.GetConnectionState(ServerAddress));
        Assert.Equal(ConnectionState.IsConnected, b.GetConnectionState(FirstClientAddress));
        Assert.Equal(1, a.ActiveSystemCount);
        Assert.Equal(1, b.ActiveSystemCount);
        Assert.Contains(_link.EventsOf(a), e => e is OutgoingConnectionCompleted);
        Assert.DoesNotContain(_link.EventsOf(a).Concat(_link.EventsOf(b)), e => e is ConnectionLost or Disconnected);
    }

    [Fact]
    public void When_only_one_peer_accepts_connections_a_cross_connection_completes_in_the_other_direction()
    {
        // CrossConnectionConvertTest: the server accepts one connection and the client none.
        var server = Peer(ServerAddress, 2, maxIncoming: 1);
        var client = Peer(FirstClientAddress, 1);

        server.Connect(FirstClientAddress, default, 12, 500, 0, _link.NowUs);
        client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(200));

        Assert.Equal(
            [MessageId.NoFreeIncomingConnections, MessageId.NewIncomingConnection],
            _link.EventsOf(server).Select(e => e.Id)
        );
        Assert.Equal([MessageId.ConnectionRequestAccepted], _link.EventsOf(client).Select(e => e.Id));
        Assert.Equal(ConnectionState.IsConnected, server.GetConnectionState(FirstClientAddress));
        Assert.Equal(ConnectionState.IsConnected, client.GetConnectionState(ServerAddress));
    }

    // Cancelling.

    [Fact]
    public void Cancelling_an_attempt_stops_its_requests_without_an_event()
    {
        var client = Peer(FirstClientAddress, 1);
        var nobody = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 9);
        client.Connect(nobody, default, 12, 500, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(10));

        client.CancelConnectionAttempt(nobody);
        var sent = _link.Sent.Count;
        _link.Run(TimeSpan.FromSeconds(10), stepUs: 10_000);

        Assert.Equal(sent, _link.Sent.Count);
        Assert.Empty(_link.EventsOf(client));
        Assert.Equal(ConnectionState.IsNotConnected, client.GetConnectionState(nobody));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect(nobody, default, 12, 500, 0, _link.NowUs));
    }

    [Fact]
    public void Cancelling_an_address_with_no_attempt_does_nothing()
    {
        var client = Peer(FirstClientAddress, 1);
        Peer(ServerAddress, 2, maxIncoming: 1);
        ConnectAndWait(client, ServerAddress);

        client.CancelConnectionAttempt(ServerAddress);

        Assert.Equal(ConnectionState.IsConnected, client.GetConnectionState(ServerAddress));
    }

    private PeerCore Peer(IPEndPoint address, ulong guid, int maxIncoming = 0, int maxConnections = 8) =>
        _link.Attach(address, new PeerCore(new PeerOptions { Guid = guid, MaxIncomingConnections = maxIncoming, MaxConnections = maxConnections }));

    private void ConnectAndWait(PeerCore peer, IPEndPoint remote)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, peer.Connect(remote, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => _link.EventsOf(peer).OfType<OutgoingConnectionCompleted>().Any(e => e.Remote.Equals(remote)), TimeSpan.FromSeconds(1));
        // Let the remote side process ID_NEW_INCOMING_CONNECTION.
        _link.Run(TimeSpan.FromMilliseconds(20));
    }

    private ConnectionAttemptFailed ConnectAndFail(PeerCore peer, IPEndPoint remote, TimeSpan? limit = null)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, peer.Connect(remote, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(
            () => _link.EventsOf(peer).OfType<ConnectionAttemptFailed>().Any(e => e.Remote.Equals(remote)),
            limit ?? TimeSpan.FromSeconds(1),
            stepUs: 10_000
        );
        return _link.EventsOf(peer).OfType<ConnectionAttemptFailed>().Single(e => e.Remote.Equals(remote));
    }
}
