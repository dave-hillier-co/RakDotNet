using System.Buffers.Binary;
using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class EventStreamTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void An_unconnected_ping_is_answered_with_a_pong_carrying_RakNets_rewrite()
    {
        using var peers = new ManualPeers();
        peers.Server.OfflinePingResponse = "hello"u8.ToArray();

        Assert.True(peers.Client.PingUnconnected(peers.ServerAddress));
        peers.RunUntil(() => peers.ClientEvents.Count > 0, "a pong");

        var pong = Assert.Single(peers.ClientEvents);
        Assert.Equal(PeerEventKind.UnconnectedPong, pong.Kind);
        Assert.Equal(MessageId.UnconnectedPong, pong.Id);
        Assert.Equal(peers.Server.RakNetGuid, pong.RakNetGuid);
        Assert.Equal(peers.ServerAddress, pong.Address);
        Assert.Null(pong.Connection);
        // RakPeer.cpp:4693-4699: ID, the ping time as a 4-byte big-endian TimeMS, then the response.
        Assert.Equal(1 + 4 + 5, pong.Data.Length);
        Assert.InRange(BinaryPrimitives.ReadUInt32BigEndian(pong.Data.AsSpan(1)), 1u, (uint)peers.Client.GetTime());
        Assert.Equal("hello"u8.ToArray(), pong.Data[5..]);
        // Unconnected pings are internal traffic and never reach the server's stream.
        Assert.Empty(peers.ServerEvents);
    }

    [Fact]
    public void The_pong_view_exposes_the_time_round_trip_and_response()
    {
        using var peers = new ManualPeers();
        peers.Server.OfflinePingResponse = "hi"u8.ToArray();
        peers.Client.PingUnconnected(peers.ServerAddress);

        for (var i = 0; i < 100; i++)
        {
            peers.Time.Advance(TimeSpan.FromMilliseconds(1));
            peers.Client.Update();
            peers.Server.Update();
            if (peers.Client.Events.TryRead(out var e))
            {
                Assert.Equal(PeerEventKind.UnconnectedPong, e.Kind);
                Assert.Equal("hi"u8.ToArray(), e.Pong.Response.ToArray());
                Assert.Equal(TimeSpan.FromMilliseconds(peers.Client.GetTime() - e.Pong.SentTime), e.Pong.RoundTrip);
                Assert.Equal(e.Data[5..].ToArray(), e.Body[4..].ToArray());
                return;
            }
        }
        Assert.Fail("No pong.");
    }

    [Fact]
    public async Task PingAsync_completes_with_the_pong_and_the_pong_still_reaches_the_stream()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback], OfflinePingResponse = "abc"u8.ToArray() });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var pong = await client.PingAsync(server.Sockets[0].LocalAddress, TimeSpan.FromSeconds(5), cancellationToken: Token);

        Assert.Equal(server.RakNetGuid, pong.RakNetGuid);
        Assert.Equal(server.Sockets[0].LocalAddress, pong.Address);
        Assert.Equal("abc"u8.ToArray(), pong.Response.ToArray());
        Assert.True(await client.Events.WaitToReadAsync(Token));
        Assert.True(client.Events.TryRead(out var e));
        Assert.Equal(PeerEventKind.UnconnectedPong, e.Kind);
    }

    [Fact]
    public async Task PingAsync_times_out_on_the_peers_clock()
    {
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        using var unanswered = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        unanswered.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        await Assert.ThrowsAsync<TimeoutException>(async () =>
            await client.PingAsync((IPEndPoint)unanswered.LocalEndPoint!, TimeSpan.FromMilliseconds(50), cancellationToken: Token));
    }

    [Fact]
    public void A_borrowed_event_is_released_by_the_next_read_and_a_detached_one_is_kept()
    {
        using var peers = new ManualPeers();
        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.SendLoopback([0x86, 2]);
        peers.Client.SendLoopback([0x86, 3]);
        peers.Client.Update();

        Assert.True(peers.Client.Events.TryRead(out var first));
        var owned = first.Detach();
        Assert.True(Throws<InvalidOperationException>(first, static e => e.Detach()));
        Assert.True(peers.Client.Events.TryRead(out var second));
        Assert.Equal([0x86, 2], second.ToArray());
        Assert.True(peers.Client.Events.TryRead(out _));

        Assert.True(Throws<ObjectDisposedException>(second, static e => e.Kind));
        Assert.True(owned.IsValid);
        Assert.Equal([0x86, 1], owned.Data.ToArray());
        Assert.Equal([0x86, 1], owned.Event.ToArray());
        var copy = owned;
        owned.Dispose();
        copy.Dispose();
        Assert.False(owned.IsValid);
        Assert.Throws<ObjectDisposedException>(() => owned.Event.Kind);
    }

    [Fact]
    public void Loopback_messages_and_injections_arrive_as_local_packets_in_order()
    {
        using var peers = new ManualPeers();
        var peer = peers.Client;

        peer.SendLoopback([0x86, 1]);
        peer.Inject([0x87, 2], new SystemAddress(IPAddress.Parse("192.0.2.1"), 5), new RakNetGuid(77));
        peer.Inject([0x88, 3], SystemAddress.Unassigned, RakNetGuid.Unassigned, atHead: true);
        peer.Update();
        var seen = new List<SeenEvent>();
        ManualPeers.Drain(peer, seen);

        Assert.Equal([0x88, 0x86, 0x87], seen.Select(e => (byte)e.Id));
        Assert.All(seen, e => Assert.Equal(PeerEventKind.Message, e.Kind));
        // RakPeer.cpp:1354-1364: SendLoopback comes from ipList[0] and this peer's GUID.
        Assert.Equal(peer.RakNetGuid, seen[1].RakNetGuid);
        Assert.Equal(new RakNetGuid(77), seen[2].RakNetGuid);
        Assert.Equal(new SystemAddress(IPAddress.Parse("192.0.2.1"), 5), seen[2].Address);
    }

    [Fact]
    public void A_send_to_this_peers_guid_becomes_loopback_with_RakNets_receipt_packet()
    {
        using var peers = new ManualPeers();
        var peer = peers.Client;

        var sent = peer.Send(new byte[] { 0x86, 9 }, peer.RakNetGuid, Reliability.ReliableWithAckReceipt);
        peer.Update();
        var seen = new List<SeenEvent>();
        ManualPeers.Drain(peer, seen);

        Assert.Equal(SendStatus.Queued, sent.Status);
        Assert.Equal([0x86, 9], seen[0].Data);
        // RakPeer.cpp:1336-1343 copies sendReceiptSerial, which is already the next receipt, into the packet.
        Assert.Equal(PeerEventKind.ReceiptAcked, seen[1].Kind);
        Assert.Equal(new SendReceipt(sent.Receipt.Value + 1), seen[1].Receipt);
    }

    [Fact]
    public void Injecting_an_owned_event_consumes_it_without_a_copy()
    {
        using var peers = new ManualPeers();
        peers.Client.SendLoopback([0x86, 1]);
        peers.Client.SendLoopback([0x86, 2]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var first));
        var owned = first.Detach();

        peers.Client.Inject(owned, atHead: true);

        Assert.False(owned.IsValid);
        owned.Dispose();
        Assert.Throws<ObjectDisposedException>(() => peers.Client.Inject(owned));
        var seen = new List<SeenEvent>();
        ManualPeers.Drain(peers.Client, seen);
        Assert.Equal([[0x86, 1], [0x86, 2]], seen.Select(e => e.Data));
    }

    [Fact]
    public void Update_releases_the_borrowed_event()
    {
        using var peers = new ManualPeers();
        peers.Client.SendLoopback([0x86]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        peers.Client.Update();

        Assert.True(Throws<ObjectDisposedException>(e, static e => e.Kind));
    }

    [Fact]
    public void A_full_stream_stops_the_cycle_reading_datagrams_until_the_consumer_drains_it()
    {
        using var peers = new ManualPeers(client: o => o with { Limits = new PeerLimits { EventQueueCapacity = 4 } });
        var connection = peers.Connect();
        peers.Settle();
        var toClient = peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;

        for (byte i = 0; i < 6; i++)
            toClient.Send(new byte[] { 0x86, i }, Reliability.ReliableOrdered);
        peers.Time.Advance(TimeSpan.FromMilliseconds(1));
        peers.Server.Update();
        for (var i = 0; i < 50 && peers.Client.Events.Count == 0; i++)
        {
            Thread.Sleep(1);
            peers.Client.Update();
        }
        Assert.Equal(6, peers.Client.Events.Count);
        for (byte i = 6; i < 12; i++)
            toClient.Send(new byte[] { 0x86, i }, Reliability.ReliableOrdered);
        peers.Time.Advance(TimeSpan.FromMilliseconds(1));
        peers.Server.Update();
        Thread.Sleep(20);
        peers.Client.Update();
        Assert.Equal(6, peers.Client.Events.Count);

        peers.RunUntil(() => peers.ClientEvents.Count(e => e.Id == (MessageId)0x86) == 12, "all twelve messages");
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (byte)i), peers.ClientEvents.Where(e => e.Id == (MessageId)0x86).Select(e => e.Data[1]));
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task ReadAllAsync_yields_owned_events()
    {
        await using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        peer.SendLoopback([0x86, 1]);
        peer.SendLoopback([0x86, 2]);

        var received = new List<byte[]>();
        await foreach (var owned in peer.Events.ReadAllAsync(Token))
        {
            using (owned)
            {
                await Task.Yield();
                received.Add(owned.Data.ToArray());
            }
            if (received.Count == 2)
                break;
        }

        Assert.Equal([[0x86, 1], [0x86, 2]], received);
        Assert.Equal(0, peer.Pools.OwnedEventsOutstanding);
    }

    [Fact]
    public async Task WaitToReadAsync_wakes_when_an_event_arrives()
    {
        await using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var waiting = peer.Events.WaitToReadAsync(Token);
        Assert.False(waiting.IsCompleted);
        peer.SendLoopback([0x86]);

        Assert.True(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.True(peer.Events.TryRead(out var e));
        Assert.Equal(1, e.Data.Length);
        Assert.Equal(0, peer.Events.Count);
    }

    [Fact]
    public async Task Shutdown_ends_a_pending_wait()
    {
        var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        var waiting = peer.Events.WaitToReadAsync(Token);

        await peer.DisposeAsync();

        Assert.False(await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token));
    }

    [Fact]
    public void Shutdown_releases_borrowed_events()
    {
        var peers = new ManualPeers();
        peers.Client.SendLoopback([0x86]);
        peers.Client.Update();
        Assert.True(peers.Client.Events.TryRead(out var e));

        peers.Dispose();

        Assert.True(Throws<ObjectDisposedException>(e, static e => e.Data.Length));
    }

    private delegate object EventAction(PeerEvent e);

    // PeerEvent is a ref struct, so it cannot be captured by Assert.Throws's lambda.
    private static bool Throws<TException>(PeerEvent e, EventAction action)
        where TException : Exception
    {
        try
        {
            action(e);
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
