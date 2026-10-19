using System.Buffers.Binary;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// Connected pings and their history (RakPeer.cpp:2073-2076, 2149-2258, 4393-4415), and ID_TIMESTAMP shifted onto the
// receiver's clock (RakPeer.cpp:1548-1555, 3773-3799).
public class PingAndTimestampTests
{
    [Fact]
    public void A_connection_reports_RakNets_ping_statistics_after_the_handshake()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        // The handshake's round trip is the first sample (RakPeer.cpp:6164).
        Assert.NotNull(server.AveragePing);
        Assert.NotNull(server.LastPing);
        Assert.NotNull(server.LowestPing);
        Assert.True(server.LowestPing <= server.LastPing);
    }

    [Fact]
    public void Ping_measures_the_round_trip_again()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();

        server.Ping();
        for (var i = 0; i < 10; i++)
        {
            peers.Step(TimeSpan.FromMilliseconds(50));
            // Loopback datagrams reach the other socket on the wall clock, not the fake one.
            Thread.Sleep(2);
        }

        Assert.InRange(server.LastPing!.Value.TotalMilliseconds, 50, 200);
        Assert.True(server.AveragePing > server.LowestPing);
    }

    [Fact]
    public void Ping_takes_a_send_receipt_number()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        var before = peers.Client.PeekNextReceipt();

        server.Ping();

        // Ping goes through Send, which calls IncrementNextSendReceipt (RakPeer.cpp:2073-2076, 4103-4115, 1326-1330).
        Assert.Equal(before.Value + 1, peers.Client.PeekNextReceipt().Value);
    }

    [Fact]
    public void An_ID_TIMESTAMP_message_arrives_in_the_receivers_clock()
    {
        using var clocks = new SkewedPeers();
        var connection = clocks.Connect();

        // The server's clock reads 5 s more.
        Assert.InRange(connection.ClockDifferential, -5_010, -4_990);
        var message = new byte[1 + 8 + 1];
        message[0] = (byte)MessageId.Timestamp;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1), clocks.Server.GetTime());
        message[9] = (byte)MessageId.UserPacketEnum;
        connection.Send(message, Reliability.ReliableOrdered);
        for (var i = 0; i < 50 && clocks.Timestamps.Count == 0; i++)
            clocks.Step();

        // Shifted once, by the sender's clock differential.
        var received = Assert.Single(clocks.Timestamps);
        Assert.InRange((long)clocks.Client.GetTime() - (long)received, 0, 20);
    }

    [Fact]
    public void An_injected_ID_TIMESTAMP_packet_is_shifted_when_it_is_read()
    {
        using var clocks = new SkewedPeers();
        var server = clocks.ClientSideConnection();
        var differential = clocks.Client.TryGetConnection(server, out var connection) ? connection.ClockDifferential : 0;
        Assert.NotEqual(0, differential);
        var message = new byte[1 + 8 + 1];
        message[0] = (byte)MessageId.Timestamp;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1), 1_000_000);
        message[9] = (byte)MessageId.UserPacketEnum;

        clocks.Client.Inject(message, server, RakNetGuid.Unassigned);

        // RakPeer::Receive shifts every ID_TIMESTAMP packet it pops, PushBackPacket ones included, by the clock
        // differential of the active system at the packet's address (RakPeer.cpp:1548-1555, 3773-3799).
        Assert.True(clocks.Client.Events.TryRead(out var e));
        Assert.Equal((ulong)(1_000_000 - differential), e.Timestamp);
    }

    [Fact]
    public void A_timestamp_read_after_its_senders_connection_ended_is_not_shifted()
    {
        using var clocks = new SkewedPeers();
        var server = clocks.ClientSideConnection();
        var message = new byte[1 + 8 + 1];
        message[0] = (byte)MessageId.Timestamp;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1), 1_000_000);
        message[9] = (byte)MessageId.UserPacketEnum;
        clocks.Client.Inject(message, server, RakNetGuid.Unassigned);

        clocks.Client.Close(server, notify: false);
        clocks.Client.Update();

        // GetBestClockDifferential looks up only active systems (RakPeer.cpp:3793), and the shift happens at the read.
        var timestamps = new List<ulong>();
        while (clocks.Client.Events.TryRead(out var e))
        {
            if (e.HasTimestamp)
                timestamps.Add(e.Timestamp);
        }
        Assert.Equal([1_000_000UL], timestamps);
    }

    /// <summary>Two Manual-mode peers on separate fake clocks, the server's 5 s ahead, as two processes would be.</summary>
    private sealed class SkewedPeers : IDisposable
    {
        private readonly FakeTimeProvider _serverTime = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        private readonly FakeTimeProvider _clientTime = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        private readonly List<SeenEvent> _serverEvents = [];
        private readonly List<SeenEvent> _clientEvents = [];

        public SkewedPeers()
        {
            Server = RakPeer.Start(Options(_serverTime, maxIncoming: 4));
            _serverTime.Advance(TimeSpan.FromSeconds(5));
            Client = RakPeer.Start(Options(_clientTime, maxIncoming: 0));
        }

        public RakPeer Server { get; }

        public RakPeer Client { get; }

        public List<ulong> Timestamps { get; } = [];

        public void Step()
        {
            _serverTime.Advance(TimeSpan.FromMilliseconds(1));
            _clientTime.Advance(TimeSpan.FromMilliseconds(1));
            Server.Update();
            ManualPeers.Drain(Server, _serverEvents);
            Client.Update();
            while (Client.Events.TryRead(out var e))
            {
                if (e.HasTimestamp)
                    Timestamps.Add(e.Timestamp);
                _clientEvents.Add(SeenEvent.From(e));
            }
        }

        /// <summary>Connects and returns the server's connection to the client.</summary>
        public RakConnection Connect()
        {
            Client.Connect(Server.Sockets[0].LocalAddress);
            for (var i = 0; i < 200 && !_serverEvents.Any(e => e.Kind == PeerEventKind.Connected); i++)
                Step();
            return _serverEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;
        }

        /// <summary>Connects and returns the server's address, with the client's events drained.</summary>
        public SystemAddress ClientSideConnection()
        {
            Connect();
            for (var i = 0; i < 200 && !_clientEvents.Any(e => e.Kind == PeerEventKind.Connected); i++)
                Step();
            return Server.Sockets[0].LocalAddress;
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    private static RakPeerOptions Options(TimeProvider time, int maxIncoming) => new()
    {
        MaxConnections = 4,
        MaxIncomingConnections = maxIncoming,
        Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
        Threading = PeerThreading.Manual,
        TimeProvider = time,
    };
}
