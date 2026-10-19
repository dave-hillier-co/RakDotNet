using System.Buffers;
using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class SendTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void A_send_to_an_unconnected_target_still_takes_a_receipt()
    {
        using var peers = new ManualPeers();

        // RakPeer.cpp:1326-1349: the receipt is taken before the update thread looks for the target.
        var first = peers.Client.Send(new byte[] { 0x86 }, new RakNetGuid(1234), Reliability.ReliableOrdered);
        var second = peers.Client.Send(new byte[] { 0x86 }, new SystemAddress(IPAddress.Loopback, 9), Reliability.ReliableOrdered);

        Assert.Equal(new SendResult(SendStatus.NotConnected, new SendReceipt(1)), first);
        Assert.Equal(new SendResult(SendStatus.NotConnected, new SendReceipt(2)), second);
        Assert.Equal(new SendReceipt(3), peers.Client.PeekNextReceipt());
        Assert.Equal(new SendReceipt(3), peers.Client.ReserveReceipt());
        Assert.Equal(new SendReceipt(4), peers.Client.PeekNextReceipt());
        Assert.Equal(new SendReceipt(99), peers.Client.Send(new byte[] { 0x86 }, new RakNetGuid(1), Reliability.Reliable, forceReceipt: new SendReceipt(99)).Receipt);
    }

    [Fact]
    public void A_send_with_bad_arguments_throws()
    {
        using var peers = new ManualPeers();
        var target = new RakNetGuid(1);

        Assert.Throws<ArgumentException>(() => peers.Client.Send(Array.Empty<byte>(), target, Reliability.Reliable));
        Assert.Throws<ArgumentException>(() => peers.Client.Send(new byte[] { 0x86 }, default(AddressOrGuid), Reliability.Reliable));
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Client.Send(new byte[] { 0x86 }, target, Reliability.Reliable, orderingChannel: 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Client.Send(new byte[] { 0x86 }, target, (Reliability)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Client.Send(new byte[] { 0x86 }, target, Reliability.Reliable, (Priority)9));
    }

    [Fact]
    public void An_ack_receipt_send_is_followed_by_ReceiptAcked_carrying_its_receipt()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        var sent = server.Send(new byte[] { 0x86, 5 }, Reliability.ReliableOrderedWithAckReceipt);
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ReceiptAcked), "the receipt");

        var acked = peers.ClientEvents.Single(e => e.Kind == PeerEventKind.ReceiptAcked);
        Assert.Equal(sent.Receipt, acked.Receipt);
        // ReliabilityLayer.cpp:2493-2494: the receipt follows the ID in host order.
        Assert.Equal([(byte)MessageId.SndReceiptAcked, .. BitConverter.GetBytes(sent.Receipt.Value)], acked.Data);
        Assert.Same(server, acked.Connection);
        Assert.Contains(peers.ServerEvents, e => e.Data.SequenceEqual(new byte[] { 0x86, 5 }));
    }

    [Fact]
    public void Several_sends_in_one_cycle_share_a_datagram()
    {
        var observer = new DatagramCounter();
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();
        peers.Client.LowLevel.Observers.Attach(observer);
        peers.Client.Update();
        observer.Sent.Clear();

        for (byte i = 0; i < 5; i++)
            server.Send(new byte[] { 0x86, i }, Reliability.ReliableOrdered);
        peers.Time.Advance(TimeSpan.FromMilliseconds(1));
        peers.Client.Update();

        Assert.Single(observer.Sent);
        peers.RunUntil(() => peers.ServerEvents.Count(e => e.Id == (MessageId)0x86) == 5, "five messages");
    }

    [Fact]
    public void A_broadcast_reaches_every_connected_system_but_the_excepted_one()
    {
        using var peers = new ManualPeers();
        using var third = RakPeer.Start(peers.Options(maxIncoming: 0));
        var thirdEvents = new List<SeenEvent>();
        var toServer = peers.Connect();
        third.Connect(peers.ServerAddress);
        peers.RunUntil(() =>
        {
            third.Update();
            ManualPeers.Drain(third, thirdEvents);
            return peers.Server.ConnectionCount == 2 && thirdEvents.Any(e => e.Kind == PeerEventKind.Connected);
        }, "two clients");

        var broadcast = peers.Server.Broadcast(new byte[] { 0x86, 7 }, Reliability.ReliableOrdered, except: peers.Client.RakNetGuid);
        Assert.Equal(SendStatus.Queued, broadcast.Status);
        peers.RunUntil(() =>
        {
            third.Update();
            ManualPeers.Drain(third, thirdEvents);
            return thirdEvents.Any(e => e.Id == (MessageId)0x86);
        }, "the broadcast");
        peers.Settle();

        Assert.DoesNotContain(peers.ClientEvents, e => e.Id == (MessageId)0x86);
        Assert.NotNull(toServer);
    }

    [Fact]
    public void A_message_writer_writes_straight_into_the_send()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        server.Send(Reliability.ReliableOrdered, Priority.High, 0, sizeHint: 8, state: 0x0102u, static (ref BitStreamWriter w, uint s) =>
        {
            w.WriteByte(0x86);
            w.WriteUInt32(s);
        });
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Id == (MessageId)0x86), "the message");

        Assert.Equal([0x86, 0, 0, 1, 2], peers.ServerEvents.Single(e => e.Id == (MessageId)0x86).Data);
    }

    [Fact]
    public void Outgoing_messages_keep_their_bit_length_and_join_sequence_parts()
    {
        Assert.Equal(16, new OutgoingMessage(new byte[] { 1, 2 }).BitLength);
        Assert.Equal(13, new OutgoingMessage(new byte[] { 1, 2 }, 13).BitLength);
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutgoingMessage(new byte[] { 1, 2 }, 17));
        var parts = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 });
        Assert.Equal(24, new OutgoingMessage(parts).BitLength);
    }

    [Fact]
    public async Task SendAndTrackAsync_completes_with_the_ack_instead_of_an_event()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, MaxIncomingConnections = 2, Bindings = [ManualPeers.Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        var connection = (await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token)).EnsureConnected();

        var outcome = await connection.SendAndTrackAsync(new byte[] { 0x86 }, Reliability.ReliableOrdered, cancellationToken: Token);

        Assert.Equal(DeliveryStatus.Acked, outcome.Status);
        Assert.Equal(SendStatus.Queued, outcome.SendStatus);
        Assert.NotEqual(SendReceipt.None, outcome.Receipt);
        while (client.Events.TryRead(out var e))
            Assert.NotEqual(PeerEventKind.ReceiptAcked, e.Kind);
    }

    [Fact]
    public async Task SendAndTrackAsync_reports_an_unsent_message_and_rejects_sequenced_reliabilities()
    {
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var outcome = await client.SendAndTrackAsync(new byte[] { 0x86 }, new RakNetGuid(5), Reliability.Reliable, cancellationToken: Token);

        Assert.Equal(DeliveryStatus.NotSent, outcome.Status);
        Assert.Equal(SendStatus.NotConnected, outcome.SendStatus);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.SendAndTrackAsync(new byte[] { 0x86 }, new RakNetGuid(5), Reliability.ReliableSequenced, cancellationToken: Token));
    }

    [Fact]
    public async Task A_tracked_send_whose_connection_ends_is_abandoned()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();

        var tracked = server.SendAndTrackAsync(new byte[] { 0x86 }, Reliability.Reliable, cancellationToken: Token);
        server.Close(notify: false);
        peers.Client.Update();

        Assert.True(tracked.IsCompleted);
        Assert.Equal(DeliveryStatus.Abandoned, (await tracked).Status);
    }

    [Fact]
    public void Statistics_count_what_the_connection_sent_and_received()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        var message = new byte[100];
        message[0] = 0x86;
        server.Send(message, Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Data.Length == 100), "the message");
        var stats = server.GetStatistics();

        // The reliability layer counts every message it is given, the handshake and pings included
        // (ReliabilityLayer.cpp:1587-1588).
        Assert.True(stats.RunningTotal[RnsPerSecondMetric.UserMessageBytesPushed] >= 100);
        Assert.True(stats.RunningTotal[RnsPerSecondMetric.UserMessageBytesSent] >= 100);
        Assert.True(stats.RunningTotal[RnsPerSecondMetric.ActualBytesSent] > 100);
        Assert.True(stats.RunningTotal[RnsPerSecondMetric.ActualBytesReceived] > 0);
        var serverSide = peers.ServerEvents.Single(e => e.Data.Length == 100).Connection!.GetStatistics();
        Assert.True(serverSide.RunningTotal[RnsPerSecondMetric.UserMessageBytesReceivedProcessed] >= 100);
        Span<ConnectionStatistics> all = new ConnectionStatistics[4];
        Assert.Equal(1, peers.Client.CopyStatistics(all));
        Assert.Same(server, all[0].Connection);
        Assert.Contains("Bytes per second sent", stats.ToString(StatisticsVerbosity.Low), StringComparison.Ordinal);
    }

    private sealed class DatagramCounter : INetworkObserver
    {
        public List<byte[]> Sent { get; } = [];

        public void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote) => Sent.Add(datagram.ToArray());
    }
}
