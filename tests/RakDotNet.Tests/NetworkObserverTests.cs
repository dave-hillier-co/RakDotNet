using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;
using RakDotNet.Testing;

namespace RakDotNet.Tests;

// The reliability-layer hooks of PluginInterface2, raised from the I/O cycle where ReliabilityLayer.cpp raises them:
// OnInternalPacket (OnFrame), OnAck and OnReliabilityLayerNotification.
public class NetworkObserverTests
{
    [Fact]
    public void A_sent_message_is_seen_as_a_frame_on_both_sides_and_its_acknowledgement_by_the_sender()
    {
        using var peers = new ManualPeers();
        var clientObserver = new RecordingObserver();
        var serverObserver = new RecordingObserverPlugin();
        peers.Client.LowLevel.Observers.Attach(clientObserver);
        peers.Server.Plugins.Attach(serverObserver);
        var connection = peers.Connect();

        connection.Send(new byte[] { 0x86, 0x01, 0x02 }, Reliability.ReliableOrdered, Priority.High, orderingChannel: 2);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Id == MessageId.UserPacketEnum), "the message arrives");
        peers.Settle();

        var sent = Assert.Single(clientObserver.Frames, f => f.IsSend && f.Data == "860102");
        Assert.Equal(peers.ServerAddress, sent.Remote);
        Assert.Same(connection, sent.Connection);
        Assert.Equal(Reliability.ReliableOrdered, sent.Reliability);
        Assert.Equal(Priority.High, sent.Priority);
        Assert.Equal(2, sent.OrderingChannel);
        Assert.Equal(24, sent.BitLength);
        Assert.False(sent.IsSplit);
        Assert.Contains(new SeenAck(sent.MessageNumber, peers.ServerAddress), clientObserver.Acks);

        var received = Assert.Single(serverObserver.Frames, f => !f.IsSend && f.Data == "860102");
        Assert.Equal(sent.MessageNumber, received.MessageNumber);
        Assert.Equal(2, received.OrderingChannel);
        Assert.Equal(peers.Server.Connections.Single(), received.Connection);
        Assert.Equal(peers.Server.Connections.Single().Address, received.Remote);
    }

    [Fact]
    public void Frame_and_ack_times_are_the_peer_clock_in_milliseconds()
    {
        using var peers = new ManualPeers();
        var observer = new RecordingObserver();
        peers.Client.LowLevel.Observers.Attach(observer);
        var connection = peers.Connect();

        connection.Send(new byte[] { 0x86 }, Reliability.Reliable, Priority.High);
        peers.Step();

        var sent = Assert.Single(observer.Frames, f => f.IsSend && f.Data == "86");
        Assert.Equal(peers.Client.GetTime(), sent.TimeMs);
    }

    [Fact]
    public void A_malformed_datagram_from_a_connected_system_is_reported_as_an_error_notification()
    {
        using var peers = new ManualPeers();
        var observer = new RecordingObserver();
        peers.Server.LowLevel.Observers.Attach(observer);
        peers.Connect();

        // No valid flag (ReliabilityLayer.cpp:689-695), and no offline message ID.
        Assert.True(peers.Client.Sockets[0].SendTo(new byte[] { 0x7F, 0x00, 0x00, 0x00 }, peers.ServerAddress));
        peers.RunUntil(() => observer.Notifications.Count > 0, "the notification");

        Assert.Equal([new SeenNotification("dhf.isValid==false", 32, peers.Server.Connections.Single().Address, true)], observer.Notifications);
    }

    [Fact]
    public void A_detached_observer_sees_nothing_more()
    {
        using var peers = new ManualPeers();
        var observer = new RecordingObserver();
        peers.Client.LowLevel.Observers.Attach(observer);
        var connection = peers.Connect();
        Assert.True(peers.Client.LowLevel.Observers.Detach(observer));
        peers.Step();
        var seen = observer.Frames.Count;

        connection.Send(new byte[] { 0x86 }, Reliability.Reliable, Priority.High);
        peers.Settle();

        Assert.Equal(seen, observer.Frames.Count);
    }

    [Fact]
    public void An_attached_observer_adds_no_allocation_to_exchanging_messages()
    {
        var without = BytesForExchange(observer: null);
        var with = BytesForExchange(new NullObserver());

        Assert.True(with <= without, $"Exchanging messages allocated {without} B without an observer and {with} B with one.");
    }

    private static long BytesForExchange(INetworkObserver? observer)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var network = new InMemoryNetwork(time);
        using var server = Start(network, time, maxIncoming: 1);
        using var client = Start(network, time, maxIncoming: 0);
        if (observer is not null)
        {
            server.LowLevel.Observers.Attach(observer);
            client.LowLevel.Observers.Attach(observer);
        }
        Assert.Equal(ConnectAttemptResult.Started, client.Connect(server.Sockets[0].LocalAddress));
        for (var i = 0; i < 1_000 && server.ConnectionCount == 0; i++)
            Cycle(time, server, client);
        var target = client.Connections.Single();
        var payload = new byte[] { 0x86, 1, 2, 3 };
        // Warm up pools and queues, then measure the same exchange again.
        Exchange(time, server, client, target, payload);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Exchange(time, server, client, target, payload);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Exchange(FakeTimeProvider time, RakPeer server, RakPeer client, RakConnection target, byte[] payload)
    {
        for (var round = 0; round < 20; round++)
        {
            target.Send(payload, Reliability.ReliableOrdered, Priority.High);
            Cycle(time, server, client);
        }
        for (var i = 0; i < 50; i++)
            Cycle(time, server, client);
    }

    private static void Cycle(FakeTimeProvider time, RakPeer server, RakPeer client)
    {
        time.Advance(TimeSpan.FromMilliseconds(1));
        foreach (var peer in new[] { client, server })
        {
            peer.Update();
            while (peer.Events.TryRead(out _))
            {
            }
        }
    }

    private static RakPeer Start(InMemoryNetwork network, TimeProvider time, int maxIncoming) => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 1,
        MaxIncomingConnections = maxIncoming,
        Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
        Transport = network,
        Threading = PeerThreading.Manual,
        TimeProvider = time,
    });

    private sealed record SeenFrame(
        bool IsSend,
        uint FrameNumber,
        ulong TimeMs,
        SystemAddress Remote,
        RakConnection? Connection,
        Reliability Reliability,
        Priority Priority,
        byte OrderingChannel,
        UInt24 MessageNumber,
        bool IsSplit,
        string Data,
        int BitLength)
    {
        public static SeenFrame From(in FrameInfo f) => new(
            f.IsSend, f.FrameNumber, f.TimeMs, f.Remote, f.Connection, f.Reliability, f.Priority, f.OrderingChannel,
            f.MessageNumber, f.IsSplit, Convert.ToHexStringLower(f.Data), f.BitLength);
    }

    private sealed record SeenAck(UInt24 MessageNumber, SystemAddress Remote);

    private sealed record SeenNotification(string Message, int BitLength, SystemAddress Remote, bool IsError);

    private class RecordingObserver : INetworkObserver
    {
        private readonly Lock _gate = new();

        public List<SeenFrame> Frames { get; } = [];

        public List<SeenAck> Acks { get; } = [];

        public List<SeenNotification> Notifications { get; } = [];

        public void OnFrame(in FrameInfo frame)
        {
            var seen = SeenFrame.From(frame);
            lock (_gate)
                Frames.Add(seen);
        }

        public void OnAck(uint messageNumber, SystemAddress remote, ulong timeMs)
        {
            lock (_gate)
                Acks.Add(new SeenAck(new UInt24(messageNumber), remote));
        }

        public void OnReliabilityNotification(ReadOnlySpan<char> message, int bitLength, SystemAddress remote, bool isError)
        {
            var seen = new SeenNotification(message.ToString(), bitLength, remote, isError);
            lock (_gate)
                Notifications.Add(seen);
        }
    }

    private sealed class RecordingObserverPlugin : RecordingObserver, IRakPlugin;

    private sealed class NullObserver : INetworkObserver;
}
