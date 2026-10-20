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
    public void A_background_peer_answers_a_connected_ping_without_waiting_for_its_next_update_tick()
    {
        // RakPeer.cpp:6086-6091: after queuing ID_CONNECTED_PONG RakNet sets quitAndDataEvents, "so the ping goes out right
        // away" rather than on the next update tick. Here the clock runs but the update ticks come only from the test:
        // every 50 ms while connecting, so the handshake's round trip spans ticks, and none once connected, so only that
        // wake can send the pong.
        var time = new HandTickedTimeProvider();
        using var server = RakPeer.Start(Options(time, maxIncoming: 4) with { Threading = PeerThreading.Background });
        using var client = RakPeer.Start(Options(time, maxIncoming: 0) with { Threading = PeerThreading.Background });
        RakConnection? connection = null;
        client.Connect(server.Sockets[0].LocalAddress);
        for (var i = 0; i < 100 && connection is null; i++)
        {
            Thread.Sleep(50);
            time.Tick();
            while (client.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Connected)
                    connection = e.Connection;
            }
        }
        Assert.NotNull(connection);
        Thread.Sleep(100);
        var handshake = connection.LowestPing!.Value;
        Assert.True(handshake >= TimeSpan.FromMilliseconds(40), $"The handshake took {handshake.TotalMilliseconds} ms.");

        connection.Ping();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (connection.LowestPing == handshake && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.True(connection.LowestPing < handshake, "No pong arrived.");
    }

    // The system clock, with periodic timers that fire only when Tick is called; one-shot timers never fire.
    private sealed class HandTickedTimeProvider : TimeProvider
    {
        private readonly List<HandTickedTimer> _timers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new HandTickedTimer(callback, state, period != Timeout.InfiniteTimeSpan);
            lock (_timers)
                _timers.Add(timer);
            return timer;
        }

        public void Tick()
        {
            HandTickedTimer[] timers;
            lock (_timers)
                timers = [.. _timers];
            foreach (var timer in timers)
                timer.Fire();
        }
    }

    private sealed class HandTickedTimer(TimerCallback callback, object? state, bool periodic) : ITimer
    {
        private volatile bool _stopped = !periodic;

        public void Fire()
        {
            if (!_stopped)
                callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _stopped = period == Timeout.InfiniteTimeSpan;
            return true;
        }

        public void Dispose() => _stopped = true;

        public ValueTask DisposeAsync()
        {
            _stopped = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void An_ID_TIMESTAMP_message_arrives_in_the_receivers_clock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        using var server = RakPeer.Start(Options(time, maxIncoming: 4));
        time.Advance(TimeSpan.FromSeconds(5));
        using var client = RakPeer.Start(Options(time, maxIncoming: 0));
        var serverEvents = new List<SeenEvent>();
        var clientEvents = new List<SeenEvent>();
        var timestamps = new List<ulong>();
        void Step()
        {
            time.Advance(TimeSpan.FromMilliseconds(1));
            server.Update();
            ManualPeers.Drain(server, serverEvents);
            client.Update();
            while (client.Events.TryRead(out var e))
            {
                if (e.HasTimestamp)
                    timestamps.Add(e.Timestamp);
                clientEvents.Add(SeenEvent.From(e));
            }
        }

        client.Connect(server.Sockets[0].LocalAddress);
        for (var i = 0; i < 200 && !serverEvents.Any(e => e.Kind == PeerEventKind.Connected); i++)
            Step();
        var connection = serverEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;

        // The server started 5 s before the client, so its clock reads 5 s more.
        Assert.InRange(connection.ClockDifferential, -5_010, -4_990);
        var message = new byte[1 + 8 + 1];
        message[0] = (byte)MessageId.Timestamp;
        BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1), server.GetTime());
        message[9] = (byte)MessageId.UserPacketEnum;
        connection.Send(message, Reliability.ReliableOrdered);
        for (var i = 0; i < 50 && timestamps.Count == 0; i++)
            Step();

        // Shifted once, by the sender's clock differential.
        var received = Assert.Single(timestamps);
        Assert.InRange((long)client.GetTime() - (long)received, 0, 20);
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
