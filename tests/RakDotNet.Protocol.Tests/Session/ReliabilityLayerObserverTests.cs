using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// The PluginInterface2 hooks ReliabilityLayer.cpp raises: OnInternalPacket on send (line 2112), resend (line 1942) and
// receive (line 899), OnAck (line 2448) and OnReliabilityLayerNotification where the C++ calls it without
// LOG_TRIVIAL_NOTIFICATIONS, which RakNet never defines.
public class ReliabilityLayerObserverTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    private readonly ReliabilityLayer _sender = new(Mtu, Start);
    private readonly ReliabilityLayer _receiver = new(Mtu, Start);
    private readonly RecordingObserver _sent = new();
    private readonly RecordingObserver _received = new();

    public ReliabilityLayerObserverTests()
    {
        _sender.Observer = _sent;
        _receiver.Observer = _received;
    }

    [Fact]
    public void Each_message_packed_into_a_datagram_is_reported_as_a_sent_frame_numbered_by_its_datagram()
    {
        _sender.Send("hello"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, channel: 3, receipt: 0, Start);
        _sender.Send(new byte[] { 0x86 }, Reliability.Unreliable, Priority.Medium, channel: 0, receipt: 0, Start);

        _sender.Update(Start + 2_500);

        Assert.Equal(
            [
                new Frame(true, 0, 1_002, Reliability.ReliableOrdered, Priority.High, 3, 0, 0, 0, null, Start, "68656c6c6f", 40),
                new Frame(true, 0, 1_002, Reliability.Unreliable, Priority.Medium, 0, UInt24.Mask, 0, 0, null, Start, "86", 8),
            ],
            _sent.Frames);
    }

    [Fact]
    public void Frames_in_a_later_datagram_of_the_same_update_carry_that_datagram_number()
    {
        // Two messages that cannot share a datagram go out as datagrams 0 and 1 in one update.
        _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start);

        _sender.Update(Start + 1_000);

        Assert.Equal([0u, 1u], _sent.Frames.Select(f => f.FrameNumber));
        Assert.Equal([0u, 1u], _sent.Frames.Select(f => f.MessageNumber));
    }

    [Fact]
    public void A_resent_message_is_reported_again_with_the_datagram_it_is_resent_in()
    {
        _sender.Send("hello"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        TakeDatagrams(_sender);

        // Nothing acknowledged it, so it is resent once the retransmission timeout passes.
        _sender.Update(Start + 3_000_000);

        Assert.Equal(2, _sent.Frames.Count);
        var resent = _sent.Frames[1];
        Assert.True(resent.IsSend);
        Assert.Equal(1u, resent.FrameNumber);
        Assert.Equal(0u, resent.MessageNumber);
        Assert.Equal(4_000ul, resent.TimeMs);
    }

    [Fact]
    public void Every_part_of_a_split_message_is_reported_with_its_split_header()
    {
        // Two parts, which the slow-start window lets out in one update.
        _sender.Send(new byte[2000], Reliability.ReliableOrdered, Priority.High, 0, 0, Start);

        _sender.Update(Start + 1_000);

        Assert.Equal(2, _sent.Frames.Count);
        Assert.All(_sent.Frames, f => Assert.Equal(2u, f.Split!.Value.Count));
        Assert.Equal([0u, 1u], _sent.Frames.Select(f => f.Split!.Value.Index));
        Assert.Equal([0u, 1u], _sent.Frames.Select(f => f.FrameNumber));
        Assert.Equal(2000, _sent.Frames.Sum(f => f.BitLength) / 8);
    }

    [Fact]
    public void Each_received_message_is_reported_with_the_count_of_datagrams_received_before_it()
    {
        _sender.Send("one"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);
        _sender.Send("two"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start + 3_000);
        _sender.Update(Start + 4_000);

        Deliver(_sender, _receiver, Start + 5_000);

        Assert.Equal(
            [
                // The wire carries no priority; a received frame reports the default.
                new Frame(false, 0, 1_002, Reliability.ReliableOrdered, Priority.Immediate, 0, 0, 0, 0, null, Start + 2_000, "6f6e65", 24),
                new Frame(false, 1, 1_005, Reliability.Reliable, Priority.Immediate, 0, 1, 0, 0, null, Start + 5_000, "74776f", 24),
            ],
            _received.Frames);
    }

    [Fact]
    public void A_duplicate_is_reported_as_a_frame_and_then_as_a_warning()
    {
        _sender.Send("hello"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        var datagram = Assert.Single(TakeDatagrams(_sender));
        _receiver.Receive(datagram, Start + 2_000);

        _receiver.Receive(datagram, Start + 3_000);

        Assert.Equal(2, _received.Frames.Count);
        Assert.Equal(1u, _received.Frames[1].FrameNumber);
        // ReliabilityLayer.cpp:959-964: behind the base index, a warning rather than an error.
        Assert.Equal([new Notification("holeCount > typeRange/(DatagramSequenceNumberType) 2", datagram.Length * 8, false)], _received.Notifications);
    }

    [Fact]
    public void A_message_number_more_than_a_million_ahead_is_an_error()
    {
        var datagram = DataDatagram(new UInt24(0), new EncapsulatedMessage(Reliability.Reliable, new byte[] { 0x86 }, 8, new UInt24(1_000_001), default, default, 0, null));

        _receiver.Receive(datagram, Start + 1_000);

        Assert.Equal([new Notification("holeCount > 1000000", datagram.Length * 8, true)], _received.Notifications);
    }

    [Fact]
    public void Each_message_number_in_an_acknowledged_datagram_is_reported_with_the_time()
    {
        _sender.Send("one"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Send("two"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);
        _receiver.Update(Start + 3_000);

        Deliver(_receiver, _sender, Start + 4_000);

        Assert.Equal([new Ack(0, Start + 4_000), new Ack(1, Start + 4_000)], _sent.Acks);
    }

    [Fact]
    public void An_acknowledged_unreliable_receipt_reports_the_unassigned_message_number()
    {
        // ReliabilityLayer.cpp:2174: the datagram history records the receipt's (uint32_t)-1 message number.
        _sender.Send("one"u8.ToArray(), Reliability.UnreliableWithAckReceipt, Priority.High, 0, 7, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);
        _receiver.Update(Start + 3_000);

        Deliver(_receiver, _sender, Start + 4_000);

        Assert.Equal([new Ack(UInt24.Mask, Start + 4_000)], _sent.Acks);
    }

    [Fact]
    public void A_datagram_of_two_bytes_or_fewer_is_an_error()
    {
        _receiver.Receive(new byte[] { 0x84, 0x00 }, Start);

        Assert.Equal([new Notification("length <= 2 || buffer == 0", 16, true)], _received.Notifications);
    }

    [Fact]
    public void A_datagram_without_the_valid_flag_is_an_error()
    {
        _receiver.Receive(new byte[] { 0x00, 0x00, 0x00, 0x00 }, Start);

        Assert.Equal([new Notification("dhf.isValid==false", 32, true)], _received.Notifications);
    }

    [Fact]
    public void An_ack_whose_ranges_do_not_decode_is_an_error()
    {
        // ACK flag, then a range count of 1 with no range after it.
        var datagram = new byte[] { 0xC0, 0x00, 0x01 };

        Assert.False(_receiver.Receive(datagram, Start));

        Assert.Equal([new Notification("incomingAcks.Deserialize failed", 24, true)], _received.Notifications);
    }

    [Fact]
    public void An_ack_reaching_the_largest_datagram_number_is_an_error()
    {
        var writer = new BitStreamWriter(new byte[64]);
        DatagramHeader.ForAck().Write(ref writer);
        new AckRangeList([new AckRange(new UInt24(5), UInt24.MaxValue)]).Write(ref writer);
        var datagram = writer.Written.ToArray();

        Assert.False(_receiver.Receive(datagram, Start));

        Assert.Equal([new Notification("incomingAcks minIndex > maxIndex or maxIndex is max value", datagram.Length * 8, true)], _received.Notifications);
    }

    [Fact]
    public void A_nak_whose_ranges_do_not_decode_is_an_error()
    {
        var datagram = new byte[] { 0xA0, 0x00, 0x01 };

        Assert.False(_receiver.Receive(datagram, Start));

        Assert.Equal([new Notification("incomingNAKs.Deserialize failed", 24, true)], _received.Notifications);
    }

    [Fact]
    public void A_datagram_number_too_far_ahead_is_an_error()
    {
        // CCRakNetSlidingWindow.cpp:145-151: more than 50000 skipped numbers is treated as garbage.
        var datagram = DataDatagram(new UInt24(50_001), new EncapsulatedMessage(Reliability.Unreliable, new byte[] { 0x86 }, 8, default, default, default, 0, null));

        _receiver.Receive(datagram, Start);

        Assert.Equal([new Notification("congestionManager.OnGotPacket failed", datagram.Length * 8, true)], _received.Notifications);
        Assert.Empty(_received.Frames);
    }

    [Fact]
    public void A_data_datagram_whose_first_message_does_not_decode_is_an_error()
    {
        var writer = new BitStreamWriter(new byte[64]);
        DatagramHeader.ForData(new UInt24(0), isPacketPair: false, isContinuousSend: false, needsBAndAs: false).Write(ref writer);
        // RELIABLE with a zero bit length (ReliabilityLayer.cpp:2760-2770), padded past the 32-bit minimum.
        writer.WriteByte(0x40);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        writer.WriteByte(0x00);
        var datagram = writer.Written.ToArray();

        _receiver.Receive(datagram, Start);

        Assert.Equal([new Notification("CreateInternalPacketFromBitStream failed", datagram.Length * 8, true)], _received.Notifications);
    }

    [Fact]
    public void An_observer_that_is_not_observing_is_not_called()
    {
        _sent.IsObserving = false;
        _received.IsObserving = false;
        _sender.Send("hello"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);
        _receiver.Update(Start + 3_000);
        Deliver(_receiver, _sender, Start + 4_000);
        _receiver.Receive(new byte[] { 0x84 }, Start + 5_000);

        Assert.Empty(_sent.Frames);
        Assert.Empty(_sent.Acks);
        Assert.Empty(_received.Frames);
        Assert.Empty(_received.Notifications);
    }

    [Fact]
    public void Raising_frames_to_an_observer_allocates_nothing_more_than_running_without_one()
    {
        var withObserver = BytesForExchange(new NullObserver());
        var without = BytesForExchange(null);

        Assert.Equal(without, withObserver);
    }

    private static long BytesForExchange(ISessionObserver? observer)
    {
        var sender = new ReliabilityLayer(Mtu, Start) { Observer = observer };
        var receiver = new ReliabilityLayer(Mtu, Start) { Observer = observer };
        var payload = new byte[100];
        // Warm up once so lazily created state is not counted.
        Exchange(sender, receiver, payload, Start);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Exchange(sender, receiver, payload, Start + 1_000_000);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void Exchange(ReliabilityLayer sender, ReliabilityLayer receiver, byte[] payload, long time)
    {
        for (var i = 0; i < 10; i++)
            sender.Send(payload, Reliability.ReliableOrdered, Priority.High, 0, 0, time);
        sender.Update(time + 1_000);
        while (sender.TryDequeueDatagram(out var datagram))
            receiver.Receive(datagram, time + 2_000);
        while (receiver.TryDequeueEvent(out _))
        {
        }
        receiver.Update(time + 3_000);
        while (receiver.TryDequeueDatagram(out var datagram))
            sender.Receive(datagram, time + 4_000);
        while (sender.TryDequeueEvent(out _))
        {
        }
    }

    private static byte[] DataDatagram(UInt24 datagramNumber, EncapsulatedMessage message)
    {
        var writer = new BitStreamWriter(new byte[Mtu]);
        DatagramHeader.ForData(datagramNumber, isPacketPair: false, isContinuousSend: false, needsBAndAs: false).Write(ref writer);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    private static void Deliver(ReliabilityLayer from, ReliabilityLayer to, long nowUs)
    {
        foreach (var datagram in TakeDatagrams(from))
            to.Receive(datagram, nowUs);
    }

    private static List<byte[]> TakeDatagrams(ReliabilityLayer layer)
    {
        var datagrams = new List<byte[]>();
        while (layer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }

    private sealed record Frame(
        bool IsSend,
        uint FrameNumber,
        ulong TimeMs,
        Reliability Reliability,
        Priority Priority,
        byte OrderingChannel,
        uint MessageNumber,
        uint OrderingIndex,
        uint SequencingIndex,
        SplitHeader? Split,
        long CreationTimeUs,
        string Data,
        int BitLength);

    private sealed record Ack(uint MessageNumber, long TimeUs);

    private sealed record Notification(string Message, int BitLength, bool IsError);

    private sealed class RecordingObserver : ISessionObserver
    {
        public bool IsObserving { get; set; } = true;

        public List<Frame> Frames { get; } = [];

        public List<Ack> Acks { get; } = [];

        public List<Notification> Notifications { get; } = [];

        public void OnFrame(in SessionFrame frame) => Frames.Add(new Frame(
            frame.IsSend, frame.FrameNumber, (ulong)(frame.TimeUs / 1000), frame.Reliability, frame.Priority,
            frame.OrderingChannel, frame.MessageNumber.Value, frame.OrderingIndex.Value, frame.SequencingIndex.Value,
            frame.Split, frame.CreationTimeUs, Convert.ToHexStringLower(frame.Data), frame.BitLength));

        public void OnAck(UInt24 messageNumber, long nowUs) => Acks.Add(new Ack(messageNumber.Value, nowUs));

        public void OnNotification(string message, int bitLength, bool isError) =>
            Notifications.Add(new Notification(message, bitLength, isError));
    }

    private sealed class NullObserver : ISessionObserver
    {
        public bool IsObserving => true;

        public void OnFrame(in SessionFrame frame)
        {
        }

        public void OnAck(UInt24 messageNumber, long nowUs)
        {
        }

        public void OnNotification(string message, int bitLength, bool isError)
        {
        }
    }
}
