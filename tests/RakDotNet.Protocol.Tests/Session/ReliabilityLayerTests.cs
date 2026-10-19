using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// Sociable tests: two reliability layers exchange datagrams directly, with time driven explicitly.
public class ReliabilityLayerTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    private readonly ReliabilityLayer _sender = new(Mtu, Start);
    private readonly ReliabilityLayer _receiver = new(Mtu, Start);

    [Fact]
    public void A_reliable_ordered_message_is_sent_in_a_data_datagram_on_the_next_update()
    {
        Assert.True(_sender.Send("hello"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, channel: 0, receipt: 0, Start));

        _sender.Update(Start + 1_000);

        var datagram = Assert.Single(TakeDatagrams(_sender));
        var (header, messages) = Decode(datagram);
        Assert.Equal(DatagramKind.Data, header.Kind);
        Assert.Equal(new UInt24(0), header.DatagramNumber);
        // ReliabilityLayer.cpp:1828: needsBAndAs is set while in slow start, which is always the case at first.
        Assert.True(header.NeedsBAndAs);
        Assert.False(header.IsContinuousSend);
        var message = Assert.Single(messages);
        Assert.Equal(Reliability.ReliableOrdered, message.Reliability);
        Assert.Equal(new UInt24(0), message.ReliableMessageNumber);
        Assert.Equal(new UInt24(0), message.OrderingIndex);
        Assert.Equal("hello"u8.ToArray(), message.Payload.ToArray());
    }

    [Fact]
    public void An_update_no_later_than_the_previous_one_does_nothing()
    {
        // ReliabilityLayer.cpp:1728-1733.
        _sender.Send("hello"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);

        _sender.Update(Start);

        Assert.Empty(TakeDatagrams(_sender));
    }

    [Fact]
    public void Received_messages_are_delivered_with_their_content_and_bit_length()
    {
        _sender.Send(new byte[] { 0x86, 0xF0 }, bitLength: 12, Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);

        Deliver(_sender, _receiver, Start + 2_000);

        var delivered = Assert.IsType<MessageDelivered>(Assert.Single(TakeEvents(_receiver)));
        Assert.Equal(12, delivered.BitLength);
        Assert.Equal(new byte[] { 0x86, 0xF0 }, delivered.Payload.ToArray());
    }

    [Fact]
    public void Acks_go_out_on_the_next_update_while_the_round_trip_time_is_unknown()
    {
        _sender.Send("hello"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);

        _receiver.Update(Start + 2_001);

        var (header, ranges) = DecodeAck(Assert.Single(TakeDatagrams(_receiver)));
        Assert.False(header.HasBAndAs);
        Assert.Equal([new AckRange(new UInt24(0), new UInt24(0))], ranges.Ranges);
    }

    [Fact]
    public void Once_an_ack_has_measured_the_round_trip_acks_wait_for_the_syn_interval()
    {
        GiveReceiverARoundTripTime();

        _sender.Send("hello"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start + 5_000);
        _sender.Update(Start + 6_000);
        Deliver(_sender, _receiver, Start + 7_000);

        _receiver.Update(Start + 7_000 + SlidingWindow.SynUs - 1);
        Assert.Empty(TakeDatagrams(_receiver));

        _receiver.Update(Start + 7_000 + SlidingWindow.SynUs);
        var (_, ranges) = DecodeAck(Assert.Single(TakeDatagrams(_receiver)));
        Assert.Equal([new AckRange(new UInt24(0), new UInt24(0))], ranges.Ranges);
    }

    [Fact]
    public void Acks_held_back_for_several_datagrams_are_sent_as_one_range()
    {
        GiveReceiverARoundTripTime();

        for (var i = 0; i < 2; i++)
        {
            _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start + 10_000);
            _sender.Update(Start + 10_000 + i);
            Deliver(_sender, _receiver, Start + 11_000);
            _receiver.Update(Start + 11_000 + i);
        }
        Assert.Empty(TakeDatagrams(_receiver));

        _receiver.Update(Start + 11_000 + SlidingWindow.SynUs);

        var (_, ranges) = DecodeAck(Assert.Single(TakeDatagrams(_receiver)));
        Assert.Equal([new AckRange(new UInt24(0), new UInt24(1))], ranges.Ranges);
    }

    [Fact]
    public void Small_messages_are_packed_into_one_datagram()
    {
        for (var i = 0; i < 8; i++)
            _sender.Send(new byte[] { (byte)i }, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);

        _sender.Update(Start + 1_000);

        var (_, messages) = Decode(Assert.Single(TakeDatagrams(_sender)));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (uint)i), messages.Select(m => m.ReliableMessageNumber.Value));
    }

    [Fact]
    public void Messages_that_overflow_the_mtu_go_in_further_datagrams_and_the_window_holds_back_the_rest()
    {
        // Each message costs 200 bytes of data and 10 of header (ReliabilityLayer.cpp:3464), so six fit the
        // (1492 - 28 - 9) * 8 bit budget. The window starts at 1464 bytes; the loop keeps filling while the bytes
        // pushed this update are below it (ReliabilityLayer.cpp:1982-1987), so two datagrams leave.
        for (var i = 0; i < 20; i++)
            _sender.Send(new byte[200], Reliability.ReliableOrdered, Priority.High, 0, 0, Start);

        _sender.Update(Start + 1_000);

        var datagrams = TakeDatagrams(_sender);
        Assert.Equal(2, datagrams.Count);
        Assert.All(datagrams, d => Assert.Equal(6, Decode(d).Messages.Count));
        Assert.All(datagrams, d => Assert.True(d.Length <= Mtu - 28));
        Assert.False(Decode(datagrams[0]).Header.IsContinuousSend);
        Assert.True(Decode(datagrams[1]).Header.IsContinuousSend);
        Assert.Equal(new UInt24(1), Decode(datagrams[1]).Header.DatagramNumber);
    }

    [Fact]
    public void Everything_queued_is_delivered_in_order_once_acks_open_the_window()
    {
        for (var i = 0; i < 50; i++)
        {
            var payload = new byte[200];
            payload[0] = (byte)i;
            _sender.Send(payload, Reliability.ReliableOrdered, Priority.High, 3, 0, Start);
        }

        var delivered = Exchange(Start + 1_000, Start + 200_000).OfType<MessageDelivered>().ToList();

        Assert.Equal(Enumerable.Range(0, 50).Select(i => (byte)i), delivered.Select(m => m.Payload.Span[0]));
    }

    [Fact]
    public void Every_reliability_type_is_delivered_on_any_channel()
    {
        var sent = new List<byte[]>();
        foreach (var channel in new byte[] { 0, 3, 31 })
        {
            foreach (var reliability in Enum.GetValues<Reliability>())
            {
                byte[] payload = [channel, (byte)reliability];
                sent.Add(payload);
                _sender.Send(payload, reliability, Priority.High, channel, receipt: 0, Start);
            }
        }

        var delivered = Exchange(Start + 1_000, Start + 100_000).OfType<MessageDelivered>().Select(m => m.Payload.ToArray());

        Assert.Equal(sent, delivered);
    }

    [Fact]
    public void Sequenced_and_ordered_messages_keep_send_order_on_each_channel()
    {
        // The connect-send-disconnect scenario, ReliabilityLayer.cpp:1625-1657 assigning the indexes.
        Reliability[] order = [Reliability.ReliableOrdered, Reliability.Unreliable, Reliability.ReliableSequenced, Reliability.ReliableOrderedWithAckReceipt];
        foreach (var channel in new byte[] { 0, 3 })
        foreach (var reliability in order)
            _sender.Send(new[] { channel, (byte)reliability }, reliability, Priority.High, channel, 0, Start);

        _sender.Update(Start + 1_000);
        var (_, messages) = Decode(Assert.Single(TakeDatagrams(_sender)));

        // Matches tests/vectors/connect-send-disconnect.json datagram 12 (offset by the one ordered message the
        // native client had already sent on channel 0).
        Assert.Equal([0u, 0u, 1u, 1u, 0u, 0u, 1u, 1u], messages.Select(m => m.OrderingIndex.Value));
        Assert.Equal([0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u], messages.Select(m => m.SequencingIndex.Value));
        Assert.Equal([0u, 0u, 1u, 2u, 3u, 0u, 4u, 5u], messages.Select(m => m.ReliableMessageNumber.Value));
        // Ack-receipt types travel as their base type (ReliabilityLayer.cpp:2619-2626).
        Assert.Equal(Reliability.ReliableOrdered, messages[3].Reliability);
    }

    [Fact]
    public void A_duplicated_datagram_delivers_its_reliable_messages_once()
    {
        _sender.Send("once"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        var datagram = Assert.Single(TakeDatagrams(_sender));

        _receiver.Receive(datagram, Start + 2_000);
        _receiver.Receive(datagram, Start + 2_000);

        Assert.Single(TakeEvents(_receiver));
    }

    [Fact]
    public void An_ack_receipt_is_reported_when_the_message_is_acknowledged_and_not_before()
    {
        _sender.Send("receipt"u8.ToArray(), Reliability.ReliableOrderedWithAckReceipt, Priority.High, 0, receipt: 42, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 2_000);
        Assert.Empty(TakeEvents(_sender));

        _receiver.Update(Start + 3_000);
        Deliver(_receiver, _sender, Start + 4_000);

        Assert.Equal([new ReceiptAcknowledged(42)], TakeEvents(_sender));
        Assert.Equal(0, _sender.MessagesInResendBuffer);
    }

    [Fact]
    public void Plain_reliable_messages_produce_no_receipt()
    {
        _sender.Send("plain"u8.ToArray(), Reliability.Reliable, Priority.High, 0, receipt: 7, Start);

        Exchange(Start + 1_000, Start + 50_000);

        Assert.Empty(TakeEvents(_sender));
        Assert.Equal(0, _sender.MessagesInResendBuffer);
    }

    [Fact]
    public void An_unreliable_ack_receipt_is_reported_acked_when_its_datagram_is_acknowledged()
    {
        _sender.Send("u"u8.ToArray(), Reliability.UnreliableWithAckReceipt, Priority.High, 0, receipt: 9, Start);

        Exchange(Start + 1_000, Start + 50_000);

        Assert.Contains(new ReceiptAcknowledged(9), TakeEvents(_sender));
    }

    [Fact]
    public void An_unreliable_ack_receipt_is_reported_lost_when_no_ack_arrives_within_the_retransmission_timeout()
    {
        // ReliabilityLayer.cpp:2092-2099 and 1860-1881: the deadline is the RTO (2 s before any RTT sample).
        _sender.Send("u"u8.ToArray(), Reliability.UnreliableWithAckReceipt, Priority.High, 0, receipt: 9, Start);
        _sender.Update(Start + 1_000);
        TakeDatagrams(_sender);

        _sender.Update(Start + 1_000 + 2_000_000 - 1);
        Assert.Empty(TakeEvents(_sender));

        _sender.Update(Start + 1_000 + 2_000_000);
        Assert.Equal([new ReceiptLost(9)], TakeEvents(_sender));
    }

    [Fact]
    public void An_ack_for_a_datagram_never_sent_is_ignored()
    {
        Assert.True(_sender.Receive(AckDatagram(new AckRange(new UInt24(5), new UInt24(9))), Start + 1_000));

        Assert.Empty(TakeEvents(_sender));
    }

    [Fact]
    public void An_ack_range_ending_at_the_largest_datagram_number_is_rejected()
    {
        // ReliabilityLayer.cpp:738: the C++ refuses such an ACK because its per-number loop could not end.
        Assert.False(_sender.Receive(AckDatagram(new AckRange(new UInt24(0), UInt24.MaxValue)), Start + 1_000));
    }

    [Fact]
    public void Datagrams_of_two_bytes_or_fewer_are_ignored()
    {
        Assert.True(_receiver.Receive(new byte[] { 0x84, 0x00 }, Start + 1_000));

        _receiver.Update(Start + 2_000);
        Assert.Empty(TakeDatagrams(_receiver));
    }

    [Fact]
    public void Channels_out_of_range_are_sent_on_channel_zero()
    {
        // ReliabilityLayer.cpp:1563-1564.
        _sender.Send("x"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, channel: 40, 0, Start);
        _sender.Update(Start + 1_000);

        var (_, messages) = Decode(Assert.Single(TakeDatagrams(_sender)));
        Assert.Equal(0, Assert.Single(messages).OrderingChannel);
    }

    [Fact]
    public void Outgoing_data_is_waiting_until_reliable_messages_are_acknowledged()
    {
        Assert.False(_sender.IsOutgoingDataWaiting);
        _sender.Send("x"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        Assert.True(_sender.IsOutgoingDataWaiting);

        _sender.Update(Start + 1_000);
        Assert.True(_sender.IsOutgoingDataWaiting);

        Deliver(_sender, _receiver, Start + 2_000);
        Assert.True(_receiver.AreAcksWaiting);
        _receiver.Update(Start + 3_000);
        Assert.False(_receiver.AreAcksWaiting);
        Deliver(_receiver, _sender, Start + 4_000);

        Assert.False(_sender.IsOutgoingDataWaiting);
    }

    [Fact]
    public void Unacknowledged_reliable_messages_kill_the_connection_after_the_timeout_without_incoming_datagrams()
    {
        // ReliabilityLayer.cpp:1799-1808 and AckTimeout at 3428-3433; the default timeout is 10 s.
        _sender.Send("x"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        TakeDatagrams(_sender);

        _sender.Update(Start + 10_000_000);
        Assert.False(_sender.IsDead);

        _sender.Update(Start + 10_001_000);
        Assert.True(_sender.IsDead);
        Assert.Equal([new ConnectionDied()], TakeEvents(_sender));
    }

    [Fact]
    public void Without_reliable_messages_outstanding_silence_does_not_kill_the_connection()
    {
        _sender.Update(Start + 60_000_000);

        Assert.False(_sender.IsDead);
    }

    // Runs both layers until nothing is in flight, returning the receiver's events.
    private List<SessionEvent> Exchange(long from, long until)
    {
        var events = new List<SessionEvent>();
        for (var now = from; now <= until; now += 1_000)
        {
            _sender.Update(now);
            Deliver(_sender, _receiver, now);
            _receiver.Update(now);
            Deliver(_receiver, _sender, now);
            events.AddRange(TakeEvents(_receiver));
        }
        return events;
    }

    private static void Deliver(ReliabilityLayer from, ReliabilityLayer to, long nowUs)
    {
        foreach (var datagram in TakeDatagrams(from))
            Deliver(datagram, to, nowUs);
    }

    private static void Deliver(byte[] datagram, ReliabilityLayer to, long nowUs) => Assert.True(to.Receive(datagram, nowUs));

    // The receiver learns its RTT from an ack of its own reliable datagram, finishing before Start + 5 ms.
    private void GiveReceiverARoundTripTime()
    {
        _receiver.Send("ping"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _receiver.Update(Start + 1_000);
        Deliver(_receiver, _sender, Start + 2_000);
        _sender.Update(Start + 3_000);
        Deliver(_sender, _receiver, Start + 4_000);
        TakeEvents(_sender);
    }

    private static List<byte[]> TakeDatagrams(ReliabilityLayer layer)
    {
        var datagrams = new List<byte[]>();
        while (layer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }

    private static List<SessionEvent> TakeEvents(ReliabilityLayer layer)
    {
        var events = new List<SessionEvent>();
        while (layer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }

    private static (DatagramHeader Header, List<EncapsulatedMessage> Messages) Decode(byte[] datagram)
    {
        Assert.True(EncapsulatedMessageReader.TryOpenDatagram(datagram, out var header, out var reader));
        var messages = new List<EncapsulatedMessage>();
        while (reader.TryReadNext(out var message))
            messages.Add(message);
        Assert.False(reader.IsMalformed);
        return (header, messages);
    }

    private static (DatagramHeader Header, AckRangeList Ranges) DecodeAck(byte[] datagram)
    {
        var reader = new BitStreamReader(datagram);
        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Ack, header.Kind);
        Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
        return (header, ranges);
    }

    private static byte[] AckDatagram(AckRange range)
    {
        var writer = new BitStreamWriter(new byte[64]);
        DatagramHeader.ForAck().Write(ref writer);
        new AckRangeList([range]).Write(ref writer);
        return writer.Written.ToArray();
    }
}
