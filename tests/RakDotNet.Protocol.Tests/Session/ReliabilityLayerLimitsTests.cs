using System.Runtime.InteropServices;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// The hardening limits of docs/plan.md on the receive side of a session. Every limit is exceeded by datagrams a remote
// system could send, written by hand because no RakNet sender produces them.
public class ReliabilityLayerLimitsTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    [Fact]
    public void A_split_part_declaring_more_parts_than_the_limit_closes_the_connection()
    {
        var limits = SessionLimits.Default with { MaxSplitCount = 8 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        Assert.True(receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 8, index: 0, length: 10)), Start + 1));
        Assert.False(receiver.IsDead);

        Assert.True(receiver.Receive(remote.Datagram(remote.Part(id: 2, count: 9, index: 0, length: 10)), Start + 2));

        Assert.True(receiver.IsDead);
        Assert.Equal(new ConnectionDied(ConnectionDeathCause.SplitCountExceeded), Assert.Single(TakeEvents(receiver)));
    }

    [Fact]
    public void A_split_message_at_the_size_limit_is_delivered_and_one_byte_more_closes_the_connection()
    {
        var limits = SessionLimits.Default with { MaxSplitMessageBytes = 1_000 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        // Parts of 500 and 500 bytes: exactly the limit.
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 2, index: 0, length: 500)), Start + 1);
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 2, index: 1, length: 500)), Start + 2);
        Assert.Equal(1_000, Assert.Single(TakeEvents(receiver).OfType<MessageDelivered>()).Payload.Length);

        // Parts of 500 and 501 bytes.
        receiver.Receive(remote.Datagram(remote.Part(id: 2, count: 2, index: 1, length: 501)), Start + 3);
        Assert.False(receiver.IsDead);
        receiver.Receive(remote.Datagram(remote.Part(id: 2, count: 2, index: 0, length: 500)), Start + 4);

        Assert.True(receiver.IsDead);
        Assert.Equal([new ConnectionDied(ConnectionDeathCause.SplitMessageTooLarge)], TakeEvents(receiver));
    }

    [Fact]
    public void A_first_part_whose_count_implies_a_message_over_the_size_limit_closes_the_connection_at_once()
    {
        // Every part but the last fills a whole block, so (count - 1) times a full part's length is less than the whole
        // message for any RakNet sender. The connection closes before the rest of the message is buffered.
        var limits = SessionLimits.Default with { MaxSplitMessageBytes = 1_000 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: 500)), Start + 1);
        Assert.False(receiver.IsDead);
        receiver.Receive(remote.Datagram(remote.Part(id: 2, count: 4, index: 0, length: 500)), Start + 2);

        Assert.True(receiver.IsDead);
        Assert.Equal([new ConnectionDied(ConnectionDeathCause.SplitMessageTooLarge)], TakeEvents(receiver));
    }

    [Fact]
    public void More_incomplete_split_messages_than_the_limit_close_the_connection()
    {
        var limits = SessionLimits.Default with { MaxConcurrentSplits = 3 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        for (ushort id = 0; id < 3; id++)
            receiver.Receive(remote.Datagram(remote.Part(id, count: 2, index: 0, length: 10)), Start + id);
        Assert.Equal(3, receiver.IncompleteSplitMessages);
        Assert.False(receiver.IsDead);

        // Another part of a message already held is fine.
        receiver.Receive(remote.Datagram(remote.Part(id: 0, count: 2, index: 1, length: 10)), Start + 10);
        receiver.Receive(remote.Datagram(remote.Part(id: 3, count: 2, index: 0, length: 10)), Start + 11);
        Assert.False(receiver.IsDead);

        receiver.Receive(remote.Datagram(remote.Part(id: 4, count: 2, index: 0, length: 10)), Start + 12);

        Assert.True(receiver.IsDead);
        Assert.Equal(new ConnectionDied(ConnectionDeathCause.TooManyConcurrentSplits), TakeEvents(receiver).Last());
    }

    [Fact]
    public void A_split_message_that_gets_no_new_part_for_the_reassembly_timeout_closes_the_connection()
    {
        var limits = SessionLimits.Default with { SplitReassemblyTimeoutUs = 5_000_000 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: 10)), Start);
        receiver.Update(Start + 4_000_000);
        // A new part restarts the clock: a large message may take far longer than the timeout to arrive.
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 1, length: 10)), Start + 4_000_000);
        receiver.Update(Start + 9_000_000);
        Assert.False(receiver.IsDead);

        receiver.Update(Start + 9_000_001);

        Assert.True(receiver.IsDead);
        Assert.Equal(new ConnectionDied(ConnectionDeathCause.SplitReassemblyTimedOut), TakeEvents(receiver).Last());
    }

    [Fact]
    public void A_repeated_part_does_not_restart_the_reassembly_clock()
    {
        var limits = SessionLimits.Default with { SplitReassemblyTimeoutUs = 5_000_000 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: 10)), Start);
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: 10)), Start + 4_000_000);
        receiver.Update(Start + 5_000_001);

        Assert.True(receiver.IsDead);
    }

    [Fact]
    public void Ordered_messages_buffered_behind_a_gap_beyond_the_channel_limit_close_the_connection()
    {
        var limits = SessionLimits.Default with { MaxOrderingBufferMessagesPerChannel = 4 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        // Ordering index 0 never arrives on channel 3, so 1 to 4 wait.
        for (uint index = 1; index <= 4; index++)
            receiver.Receive(remote.Datagram(remote.Ordered(channel: 3, orderingIndex: index)), Start + index);
        // Another channel has its own buffer.
        receiver.Receive(remote.Datagram(remote.Ordered(channel: 4, orderingIndex: 1)), Start + 5);
        Assert.False(receiver.IsDead);
        Assert.Empty(TakeEvents(receiver));

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 3, orderingIndex: 5)), Start + 6);

        Assert.True(receiver.IsDead);
        Assert.Equal([new ConnectionDied(ConnectionDeathCause.OrderingBufferFull)], TakeEvents(receiver));
    }

    [Fact]
    public void A_full_channel_drops_an_unreliable_sequenced_message_and_keeps_the_connection()
    {
        var limits = SessionLimits.Default with { MaxOrderingBufferMessagesPerChannel = 2 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 1, payload: 'a')), Start + 1);
        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 2, payload: 'b')), Start + 2);
        receiver.Receive(remote.Datagram(RemoteSender.Sequenced(channel: 0, orderingIndex: 3, sequencingIndex: 0, payload: 'x')), Start + 3);
        Assert.False(receiver.IsDead);

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 0, payload: 'z')), Start + 4);

        Assert.Equal("zab", Delivered(receiver));
        Assert.False(receiver.IsDead);
    }

    [Fact]
    public void Buffered_bytes_over_the_receive_buffer_limit_close_the_connection()
    {
        // Ordering heaps and incomplete split messages share one budget per connection; each buffered message costs its
        // payload plus a fixed charge for its bookkeeping.
        const int payload = 200;
        var limits = SessionLimits.Default with { MaxReceiveBufferBytes = 3 * (payload + SessionLimits.BufferedEntryOverheadBytes) };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 1, length: payload)), Start + 1);
        receiver.Receive(remote.Datagram(remote.Ordered(channel: 1, orderingIndex: 1, length: payload)), Start + 2);
        receiver.Receive(remote.Datagram(remote.Part(id: 7, count: 2, index: 0, length: payload)), Start + 3);
        Assert.Equal(limits.MaxReceiveBufferBytes, receiver.BufferedReceiveBytes);
        Assert.False(receiver.IsDead);

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 2, orderingIndex: 1, length: 1)), Start + 4);

        Assert.True(receiver.IsDead);
        Assert.Equal([new ConnectionDied(ConnectionDeathCause.ReceiveBufferFull)], TakeEvents(receiver));
    }

    [Fact]
    public void A_split_part_over_the_receive_buffer_limit_closes_the_connection()
    {
        const int payload = 300;
        var limits = SessionLimits.Default with { MaxReceiveBufferBytes = 2 * (payload + SessionLimits.BufferedEntryOverheadBytes) };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 1, length: payload)), Start + 1);
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: payload)), Start + 2);
        Assert.False(receiver.IsDead);
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 1, length: payload)), Start + 3);

        Assert.True(receiver.IsDead);
        Assert.Equal([new ConnectionDied(ConnectionDeathCause.ReceiveBufferFull)], TakeEvents(receiver));
    }

    [Fact]
    public void Delivering_buffered_messages_and_completing_split_messages_returns_their_bytes_to_the_budget()
    {
        var receiver = new ReliabilityLayer(Mtu, Start);
        var remote = new RemoteSender();

        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 1, length: 100)), Start + 1);
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 2, index: 0, length: 100)), Start + 2);
        Assert.Equal(2 * (100 + SessionLimits.BufferedEntryOverheadBytes), receiver.BufferedReceiveBytes);

        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 2, index: 1, length: 100)), Start + 3);
        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 0, length: 100)), Start + 4);

        Assert.Equal(0, receiver.BufferedReceiveBytes);
        Assert.Equal(3, TakeEvents(receiver).OfType<MessageDelivered>().Count());
    }

    [Fact]
    public void A_buffered_message_holds_a_copy_of_its_payload_not_the_datagram_it_came_in()
    {
        // A one-byte message buffered behind a gap must not keep its whole datagram alive.
        var receiver = new ReliabilityLayer(Mtu, Start);
        var remote = new RemoteSender();

        receiver.Receive(
            remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 1, length: 1), remote.Message(length: 1_000)),
            Start + 1
        );
        Assert.Equal(1 + SessionLimits.BufferedEntryOverheadBytes, receiver.BufferedReceiveBytes);
        TakeEvents(receiver);
        receiver.Receive(remote.Datagram(remote.Ordered(channel: 0, orderingIndex: 0, length: 1)), Start + 2);

        var released = TakeEvents(receiver).OfType<MessageDelivered>().Last();
        Assert.True(MemoryMarshal.TryGetArray(released.Payload, out var segment));
        Assert.Single(segment.Array!);
    }

    [Fact]
    public void A_buffered_split_part_holds_a_copy_of_its_payload_not_the_datagram_it_came_in()
    {
        var receiver = new ReliabilityLayer(Mtu, Start);
        var remote = new RemoteSender();

        receiver.Receive(
            remote.Datagram(remote.Part(id: 1, count: 2, index: 0, length: 1), remote.Message(length: 1_000)),
            Start + 1
        );

        Assert.Equal(1 + SessionLimits.BufferedEntryOverheadBytes, receiver.BufferedReceiveBytes);
    }

    [Fact]
    public void Datagrams_arriving_after_the_connection_closed_are_ignored()
    {
        var limits = SessionLimits.Default with { MaxSplitCount = 2 };
        var receiver = new ReliabilityLayer(Mtu, Start, limits);
        var remote = new RemoteSender();
        receiver.Receive(remote.Datagram(remote.Part(id: 1, count: 3, index: 0, length: 10)), Start + 1);
        TakeEvents(receiver);

        receiver.Receive(remote.Datagram(remote.Message(length: 10)), Start + 2);
        receiver.Update(Start + 100_000);

        Assert.Empty(TakeEvents(receiver));
        Assert.False(receiver.TryDequeueDatagram(out _));
        Assert.Equal(0, receiver.BufferedReceiveBytes);
    }

    [Fact]
    public void Pending_NAK_ranges_stop_growing_at_their_limit_and_the_connection_stays_open()
    {
        // Every other datagram number skipped makes one NAK range each, and only one NAK datagram goes out per update
        // (ReliabilityLayer.cpp:1815-1825), so a remote system could otherwise grow the list without bound. NAKs only
        // hasten resends the retransmission timeout makes anyway, so the excess is dropped.
        var receiver = new ReliabilityLayer(Mtu, Start);
        var remote = new RemoteSender();

        for (var i = 0; i < SessionLimits.MaxPendingNakRanges + 10; i++)
        {
            remote.SkipDatagramNumber();
            receiver.Receive(remote.Datagram(remote.Message(length: 1)), Start + i);
        }

        Assert.Equal(SessionLimits.MaxPendingNakRanges, receiver.PendingNakRanges);
        Assert.False(receiver.IsDead);
    }

    [Fact]
    public void A_full_size_BigPacketTest_message_at_the_smallest_MTU_fits_the_default_split_limits()
    {
        // Samples/BigPacketTest sends BIG_PACKET_SIZE = 83296256 bytes (BigPacketTest.cpp:27), the largest message of any
        // RakNet sample. A message of the default size limit is split into no more than the default part limit even at
        // the smallest MTU.
        const int bigPacketSize = 83_296_256;
        var limits = SessionLimits.Default;
        Assert.True(bigPacketSize <= limits.MaxSplitMessageBytes);

        // The block size a RakNet sender uses at MTU 576 (SplitPacket, ReliabilityLayer.cpp:2939).
        var sender = new ReliabilityLayer(576, Start);
        sender.Send(new byte[2_000], Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        sender.Update(Start + 1_000);
        Assert.True(sender.TryDequeueDatagram(out var first));
        Assert.True(EncapsulatedMessageReader.TryOpenDatagram(first, out _, out var reader));
        Assert.True(reader.TryReadNext(out var part));
        var blockBytes = part.Payload.Length;
        var count = (uint)((limits.MaxSplitMessageBytes - 1) / blockBytes + 1);
        Assert.True(count <= limits.MaxSplitCount);

        // The first parts of such a message are accepted.
        var receiver = new ReliabilityLayer(576, Start);
        var remote = new RemoteSender();
        for (uint index = 0; index < 3; index++)
            Assert.True(receiver.Receive(remote.Datagram(remote.Part(id: 0, count, index, blockBytes)), Start + 2_000 + index));
        Assert.False(receiver.IsDead);
        Assert.Equal(1, receiver.IncompleteSplitMessages);
    }

    [Fact]
    public void BurstTests_default_burst_arrives_through_loss_and_reordering_within_the_default_limits()
    {
        // Samples/BurstTest defaults: 128 RELIABLE_ORDERED messages of 4096 bytes sent at once (BurstTest.cpp:99-120).
        var link = new LossyLink(seed: 7, lossProbability: 0.2, duplicateProbability: 0.05, maxDelayUs: 30_000);
        for (var i = 0; i < 128; i++)
        {
            var data = new byte[4096];
            data[0] = (byte)MessageId.UserPacketEnum;
            data[1] = (byte)i;
            link.A.Send(data, Reliability.ReliableOrdered, Priority.Medium, 0, 0, link.NowUs);
        }

        Assert.True(link.RunUntil(() => link.EventsAtB.OfType<MessageDelivered>().Count() == 128, 600_000_000, tickUs: 10_000));

        Assert.False(link.B.IsDead);
        Assert.Equal(Enumerable.Range(0, 128).Select(i => (byte)i), link.EventsAtB.OfType<MessageDelivered>().Select(d => d.Payload.Span[1]));
    }

    private static string Delivered(ReliabilityLayer layer) =>
        string.Concat(TakeEvents(layer).OfType<MessageDelivered>().Select(d => (char)d.Payload.Span[0]));

    private static List<SessionEvent> TakeEvents(ReliabilityLayer layer)
    {
        var events = new List<SessionEvent>();
        while (layer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }

    // A remote system writing datagrams by hand: each message gets the next reliable message number and each datagram
    // the next datagram number.
    private sealed class RemoteSender
    {
        private uint _datagramNumber;
        private uint _messageNumber;

        public void SkipDatagramNumber() => _datagramNumber++;

        public byte[] Datagram(params EncapsulatedMessage[] messages)
        {
            var writer = new BitStreamWriter(new byte[8_000]);
            DatagramHeader.ForData(new UInt24(_datagramNumber++)).Write(ref writer);
            foreach (var message in messages)
                message.Write(ref writer);
            return writer.Written.ToArray();
        }

        public EncapsulatedMessage Message(int length) =>
            new(Reliability.Reliable, Payload(length, 'm'), reliableMessageNumber: NextMessageNumber());

        public EncapsulatedMessage Part(ushort id, uint count, uint index, int length) =>
            new(
                Reliability.ReliableOrdered,
                Payload(length, 'p'),
                reliableMessageNumber: NextMessageNumber(),
                orderingIndex: new UInt24(0),
                orderingChannel: 9,
                split: new SplitHeader(count, id, index)
            );

        public EncapsulatedMessage Ordered(byte channel, uint orderingIndex, int length = 1, char payload = 'o') =>
            new(
                Reliability.ReliableOrdered,
                Payload(length, payload),
                reliableMessageNumber: NextMessageNumber(),
                orderingIndex: new UInt24(orderingIndex),
                orderingChannel: channel
            );

        public static EncapsulatedMessage Sequenced(byte channel, uint orderingIndex, uint sequencingIndex, char payload) =>
            new(
                Reliability.UnreliableSequenced,
                Payload(1, payload),
                sequencingIndex: new UInt24(sequencingIndex),
                orderingIndex: new UInt24(orderingIndex),
                orderingChannel: channel
            );

        private UInt24 NextMessageNumber() => new(_messageNumber++);

        private static byte[] Payload(int length, char first)
        {
            var data = new byte[length];
            data[0] = (byte)first;
            return data;
        }
    }
}
