using System.Buffers.Binary;
using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// Connected pings, the ping and clock differential history, ID_TIMESTAMP and timeouts (RakPeer.cpp:2073-2076,
// 2149-2258, 2524-2564, 3740-3749, 3773-3799, 4393-4415, 5857-5931, 1548-1555). The server's clock runs 5 s ahead of
// the client's, so a clock differential has something to measure.
public class ConnectedPingTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;
    private const long ServerClockAheadMs = 5_000;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public ConnectedPingTests()
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        _server = _link.Attach(
            ServerAddress,
            new PeerCore(new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8 }),
            clockOffsetUs: ServerClockAheadMs * 1000
        );
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    private long ClientNowUs => _link.NowUsOf(_client);

    // Ping history.

    [Fact]
    public void An_unknown_system_has_no_ping_and_no_clock_differential()
    {
        var nobody = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 9);

        // RakPeer.cpp:2149-2216, 2228-2234.
        Assert.Equal(-1, _client.GetAveragePing(nobody));
        Assert.Equal(-1, _client.GetLastPing(nobody));
        Assert.Equal(-1, _client.GetLowestPing(nobody));
        Assert.Equal(0, _client.GetClockDifferential(nobody));
        Assert.Equal(-1, _client.GetAveragePing(ServerGuid));
    }

    [Fact]
    public void The_handshake_measures_the_round_trip_on_both_sides()
    {
        Connect();

        // The client times ID_CONNECTION_REQUEST against ID_CONNECTION_REQUEST_ACCEPTED, the server
        // ID_CONNECTION_REQUEST_ACCEPTED against ID_NEW_INCOMING_CONNECTION (RakPeer.cpp:6031, 6164).
        Assert.InRange(_client.GetLastPing(ServerAddress), 1, 10);
        Assert.InRange(_server.GetLastPing(ClientAddress), 1, 10);
        Assert.Equal(_client.GetLastPing(ServerAddress), _client.GetLastPing(ServerGuid));
        Assert.True(_client.GetLowestPing(ServerAddress) <= _client.GetLastPing(ServerAddress));
        Assert.InRange(_client.GetAveragePing(ServerAddress), 1, 10);
    }

    [Fact]
    public void Pinging_a_connected_system_measures_the_round_trip_again()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));
        var handshakePing = _client.GetLowestPing(ServerAddress);

        // RakPeer::Ping(SystemAddress), RakPeer.cpp:2073-2076: one unreliable ID_CONNECTED_PING.
        var sentBefore = _link.Sent.Count;
        _client.PingConnected(ServerAddress, ClientNowUs);
        _link.Run(TimeSpan.FromMilliseconds(200), stepUs: 10_000);

        var ping = Assert.Single(ConnectedMessages(SentFrom(ClientAddress, sentBefore)), m => m.Payload.Span[0] == (byte)MessageId.ConnectedPing);
        Assert.Equal(Reliability.Unreliable, ping.Reliability);
        Assert.InRange(_client.GetLastPing(ServerAddress), 10, 60);
        Assert.Equal(handshakePing, _client.GetLowestPing(ServerAddress));
    }

    [Fact]
    public void Pinging_an_address_that_is_not_connected_sends_nothing()
    {
        _client.PingConnected(ServerAddress, ClientNowUs);
        _link.Run(TimeSpan.FromMilliseconds(20));

        Assert.Empty(_link.Sent);
    }

    [Fact]
    public void Pinging_uses_up_a_send_receipt_number_whether_or_not_the_address_is_connected()
    {
        Connect();
        var nobody = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 9);

        // RakPeer::Ping goes through Send, which always takes a new receipt (RakPeer.cpp:2073-2076, 4111-4114, 1385-1389).
        var first = _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum], Reliability.Reliable, Priority.High, 0, ClientNowUs);
        _client.PingConnected(ServerAddress, ClientNowUs);
        var second = _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum], Reliability.Reliable, Priority.High, 0, ClientNowUs);
        _client.PingConnected(nobody, ClientNowUs);
        var third = _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum], Reliability.Reliable, Priority.High, 0, ClientNowUs);

        Assert.Equal(first + 2, second);
        Assert.Equal(second + 2, third);
    }

    [Fact]
    public void The_average_covers_the_last_five_pings()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));
        var lowest = _client.GetLowestPing(ServerAddress);

        // PING_TIMES_ARRAY_SIZE is 5 (RakNetTypes.h:462); five slow pings replace every fast sample.
        for (var i = 0; i < 5; i++)
        {
            _client.PingConnected(ServerAddress, ClientNowUs);
            _link.Run(TimeSpan.FromMilliseconds(200), stepUs: 10_000);
        }

        Assert.InRange(_client.GetAveragePing(ServerAddress), 10, 60);
        Assert.Equal(lowest, _client.GetLowestPing(ServerAddress));
    }

    [Fact]
    public void Without_occasional_pings_a_connection_stops_pinging_once_it_has_a_round_trip()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(100));
        var sentBefore = _link.Sent.Count;

        _link.Run(TimeSpan.FromSeconds(20), stepUs: 10_000);

        // RakPeer.cpp:5924: only the reliable keepalive pings remain (RakPeer.cpp:5857-5869).
        var pings = ConnectedMessages(SentFrom(ClientAddress, sentBefore)).Where(m => m.Payload.Span[0] == (byte)MessageId.ConnectedPing).ToList();
        Assert.NotEmpty(pings);
        Assert.All(pings, p => Assert.Equal(Reliability.Reliable, p.Reliability));
    }

    [Fact]
    public void With_occasional_pings_a_connection_pings_every_five_seconds()
    {
        _client.OccasionalPing = true;
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(100));
        var sentBefore = _link.Sent.Count;

        _link.Run(TimeSpan.FromSeconds(20), stepUs: 10_000);

        // SetOccasionalPing, RakPeer.cpp:2218-2221, 5924-5931.
        var unreliablePings = ConnectedMessages(SentFrom(ClientAddress, sentBefore))
            .Count(m => m.Payload.Span[0] == (byte)MessageId.ConnectedPing && m.Reliability == Reliability.Unreliable);
        Assert.InRange(unreliablePings, 3, 4);
    }

    [Fact]
    public void A_later_pong_with_a_lower_round_trip_lowers_the_lowest_ping()
    {
        Connect(stepUs: 20_000);
        _link.Run(TimeSpan.FromMilliseconds(400), stepUs: 20_000);
        var slowest = _client.GetLowestPing(ServerAddress);

        var fast = PingOnce(stepUs: 1_000);

        // OnConnectedPong, RakPeer.cpp:4410-4411.
        Assert.True(fast < slowest, $"{fast} should be below {slowest}");
        Assert.Equal(fast, _client.GetLowestPing(ServerAddress));
    }

    [Fact]
    public void The_history_holds_five_pings()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));

        // PING_TIMES_ARRAY_SIZE is 5 (RakNetTypes.h:462): each ping writes the next slot, wrapping after the fifth.
        var pings = new List<int>();
        for (var i = 1; i <= 6; i++)
            pings.Add(PingOnce(stepUs: i * 5_000));

        Assert.Equal(pings.Order().Distinct(), pings);
        Assert.Equal(pings.Skip(1).Sum() / 5, _client.GetAveragePing(ServerAddress));
    }

    [Fact]
    public void Of_samples_with_equal_round_trips_the_clock_differential_comes_from_the_first_slot()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));

        // The handshake fills slots 0 and 1 (the request's round trip and the ping that follows it), so these five
        // pings land in slots 2, 3, 4, 0 and 1. The server's clock jumps a second before each, so every slot holds a
        // different differential with the same round trip, and RakPeer.cpp:2245-2253 keeps slot 0's: the fourth ping's.
        var pings = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            _link.ShiftClock(_server, 1_000_000);
            pings.Add(PingOnce(stepUs: 10_000));
        }

        Assert.Single(pings.Distinct());
        Assert.InRange(_client.GetClockDifferential(ServerAddress), ServerClockAheadMs + 4_000 - 15, ServerClockAheadMs + 4_000 + 15);
    }

    // Clock differential and ID_TIMESTAMP.

    [Fact]
    public void The_clock_differential_is_how_far_the_remote_clock_is_ahead()
    {
        Connect();

        // OnConnectedPong, RakPeer.cpp:4408: the remote time minus the midpoint of the round trip.
        Assert.InRange(_client.GetClockDifferential(ServerAddress), ServerClockAheadMs - 5, ServerClockAheadMs + 5);
        Assert.InRange(_server.GetClockDifferential(ClientAddress), -ServerClockAheadMs - 5, -ServerClockAheadMs + 5);
    }

    [Fact]
    public void The_clock_differential_comes_from_the_sample_with_the_lowest_ping()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));
        _link.ShiftClock(_server, 1_000_000);

        // One slow sample after the clock jump does not displace the fast samples (RakPeer.cpp:2238-2258).
        _client.PingConnected(ServerAddress, ClientNowUs);
        _link.Run(TimeSpan.FromMilliseconds(200), stepUs: 10_000);
        Assert.InRange(_client.GetClockDifferential(ServerAddress), ServerClockAheadMs - 5, ServerClockAheadMs + 5);

        // Once the history holds only samples taken after the jump, it shows.
        for (var i = 0; i < 4; i++)
        {
            _client.PingConnected(ServerAddress, ClientNowUs);
            _link.Run(TimeSpan.FromMilliseconds(200), stepUs: 10_000);
        }
        Assert.InRange(_client.GetClockDifferential(ServerAddress), ServerClockAheadMs + 1_000 - 15, ServerClockAheadMs + 1_000 + 15);
    }

    [Fact]
    public void An_incoming_timestamp_is_shifted_onto_the_local_clock()
    {
        Connect();
        var serverTimeMs = (ulong)(_link.NowUsOf(_server) / 1000);
        byte[] message = [(byte)MessageId.Timestamp, .. BigEndian(serverTimeMs), (byte)MessageId.UserPacketEnum, 1, 2];

        _server.Send(ClientAddress, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUsOf(_server));
        _link.RunUntil(() => ClientEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));

        // RakPeer::Receive, RakPeer.cpp:1548-1555, 3773-3787: the time is rewritten on the way to the application.
        var received = ClientEvents.OfType<MessageReceived>().Single().Packet.ToArray();
        var shifted = BinaryPrimitives.ReadUInt64BigEndian(received.AsSpan(1));
        Assert.Equal(serverTimeMs - (ulong)_client.GetClockDifferential(ServerAddress), shifted);
        Assert.InRange((long)shifted, _link.NowMs - 10, _link.NowMs);
        Assert.Equal(message[..1], received[..1]);
        Assert.Equal(message[9..], received[9..]);
    }

    [Fact]
    public void A_timestamp_dequeued_after_its_sender_disconnected_is_not_shifted()
    {
        Connect();
        Assert.NotEqual(0, _client.GetClockDifferential(ServerAddress));
        byte[] message = [(byte)MessageId.Timestamp, .. BigEndian(1_234_567), (byte)MessageId.UserPacketEnum];
        _server.Send(ClientAddress, message, Reliability.ReliableOrdered, Priority.Immediate, 0, _link.NowUsOf(_server));
        // Deliver by hand for 10 ms, leaving the client's events queued.
        for (var us = 1_000; us <= 10_000; us += 1_000)
        {
            _server.Update(_link.NowUsOf(_server) + us);
            foreach (var datagram in Datagrams(_server))
                _client.Receive(ServerAddress, datagram.Data, ClientNowUs + us);
            _client.Update(ClientNowUs + us);
        }

        _client.CloseConnection(ServerAddress, false, 0, Priority.Low, ClientNowUs);

        // GetBestClockDifferential looks up only active systems (RakPeer.cpp:3793), so the time is shifted by 0.
        var received = Events(_client).OfType<MessageReceived>().Single().Packet.ToArray();
        Assert.Equal(message, received);
    }

    [Fact]
    public void A_timestamp_message_too_short_to_hold_a_time_is_delivered_unchanged()
    {
        Connect();
        byte[] message = [(byte)MessageId.Timestamp, 1, 2, 3, 4, 5, 6, 7];

        _server.Send(ClientAddress, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUsOf(_server));
        _link.RunUntil(() => ClientEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));

        Assert.Equal(message, ClientEvents.OfType<MessageReceived>().Single().Packet.ToArray());
    }

    // Timeouts.

    [Fact]
    public void A_connected_system_that_falls_silent_is_lost_after_the_timeout()
    {
        Connect();
        _client.SetTimeoutTime(2_000, null);
        var silentFromMs = _link.NowMs;
        _link.Drop = (from, _, _) => from.Equals(ServerAddress);

        _link.RunUntil(() => ClientEvents.OfType<ConnectionLost>().Any(), TimeSpan.FromSeconds(5), stepUs: 10_000);

        // The keepalive at half the timeout goes unacknowledged, and the reliability layer gives up once nothing has
        // arrived for the timeout (RakPeer.cpp:5857-5869, 5874-5896).
        Assert.InRange(_link.NowMs - silentFromMs, 2_000, 3_100);
        var lost = ClientEvents.OfType<ConnectionLost>().Single();
        Assert.Equal(ServerGuid, lost.Guid);
        Assert.Equal([(byte)MessageId.ConnectionLost], lost.Packet.ToArray());
        Assert.Equal(ConnectionState.IsDisconnected, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void Setting_the_timeout_of_one_system_leaves_the_default_alone()
    {
        Connect();

        _client.SetTimeoutTime(2_000, ServerAddress);

        // GetTimeoutTime returns the default even for a system with its own timeout (RakPeer.cpp:2551-2564).
        Assert.Equal(10_000, _client.GetTimeoutTime(null));
        Assert.Equal(10_000, _client.GetTimeoutTime(ServerAddress));
        var silentFromMs = _link.NowMs;
        _link.Drop = (from, _, _) => from.Equals(ServerAddress);
        _link.RunUntil(() => ClientEvents.OfType<ConnectionLost>().Any(), TimeSpan.FromSeconds(5), stepUs: 10_000);
        Assert.InRange(_link.NowMs - silentFromMs, 2_000, 3_100);
    }

    [Fact]
    public void A_new_default_timeout_applies_to_later_connections()
    {
        _client.SetTimeoutTime(3_000, null);
        Assert.Equal(3_000, _client.GetTimeoutTime(null));
        Connect();
        var silentFromMs = _link.NowMs;
        _link.Drop = (from, _, _) => from.Equals(ServerAddress);

        _link.RunUntil(() => ClientEvents.OfType<ConnectionLost>().Any(), TimeSpan.FromSeconds(6), stepUs: 10_000);

        Assert.InRange(_link.NowMs - silentFromMs, 3_000, 4_600);
    }

    private void Connect(long stepUs = 1_000)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, ClientNowUs));
        _link.RunUntil(
            () => ClientEvents.OfType<OutgoingConnectionCompleted>().Any() && _link.EventsOf(_server).OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(1),
            stepUs
        );
    }

    // One ping whose round trip grows with stepUs, as each hop takes a step; returns the round trip measured.
    private int PingOnce(long stepUs)
    {
        _client.PingConnected(ServerAddress, ClientNowUs);
        _link.Run(TimeSpan.FromMicroseconds(stepUs * 20), stepUs);
        return _client.GetLastPing(ServerAddress);
    }

    private IEnumerable<byte[]> SentFrom(IPEndPoint from, int skip) => _link.Sent.Skip(skip).Where(d => d.From.Equals(from)).Select(d => d.Data);

    private static byte[] BigEndian(ulong value)
    {
        var bytes = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
