using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// Resends, NAKs, congestion control, the unreliable timeout and bandwidth limits (ReliabilityLayer.cpp:635-886 and
// 1687-2242, CCRakNetSlidingWindow.cpp). Sociable: real layers, datagrams carried by hand or by a seeded lossy link.
public class ReliabilityLayerLossTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    // GetRTOForRetransmission before any round trip is measured (CCRakNetSlidingWindow.cpp:303-304).
    private const long InitialRtoUs = 2_000_000;

    private readonly ReliabilityLayer _sender = new(Mtu, Start);
    private readonly ReliabilityLayer _receiver = new(Mtu, Start);

    [Fact]
    public void An_unacknowledged_reliable_message_is_resent_in_a_new_datagram_once_its_retransmission_timeout_passes()
    {
        _sender.Send("again"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Assert.Single(TakeDatagrams(_sender));

        // ReliabilityLayer.cpp:1914: due once the time reaches nextActionTime = send time + RTO (line 2063).
        _sender.Update(Start + 1_000 + InitialRtoUs - 1);
        Assert.Empty(TakeDatagrams(_sender));

        _sender.Update(Start + 1_000 + InitialRtoUs);
        var (header, messages) = Decode(Assert.Single(TakeDatagrams(_sender)));
        Assert.Equal(new UInt24(1), header.DatagramNumber);
        var message = Assert.Single(messages);
        Assert.Equal(new UInt24(0), message.ReliableMessageNumber);
        Assert.Equal(new UInt24(0), message.OrderingIndex);
        Assert.Equal("again"u8.ToArray(), message.Payload.ToArray());
    }

    [Fact]
    public void Resends_repeat_every_retransmission_timeout_until_acknowledged()
    {
        _sender.Send("x"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        _sender.Update(Start + 1_000 + InitialRtoUs);
        _sender.Update(Start + 1_000 + 2 * InitialRtoUs);

        Assert.Equal([0u, 1u, 2u], TakeDatagrams(_sender).Select(d => Decode(d).Header.DatagramNumber.Value));
        Assert.Equal(1, _sender.MessagesInResendBuffer);
    }

    [Fact]
    public void The_retransmission_timeout_follows_the_measured_round_trip()
    {
        // A 10 ms sample sets the estimate and deviation to 10 ms: RTO = 2 * 10 + 4 * 10 + 30 = 90 ms
        // (CCRakNetSlidingWindow.cpp:211-215, 310).
        _sender.Send("a"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Deliver(_sender, _receiver, Start + 5_000);
        _receiver.Update(Start + 6_000);
        Deliver(_receiver, _sender, Start + 11_000);
        Assert.Equal(10_000, _sender.LastRttUs);

        var sentAt = Start + 20_000;
        _sender.Send("b"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, sentAt);
        _sender.Update(sentAt);
        TakeDatagrams(_sender);

        _sender.Update(sentAt + 90_000 - 1);
        Assert.Empty(TakeDatagrams(_sender));
        _sender.Update(sentAt + 90_000);
        Assert.Single(TakeDatagrams(_sender));
    }

    [Fact]
    public void A_receiver_naks_the_datagram_numbers_it_skipped_after_acking_what_arrived()
    {
        var datagrams = SendThreeDatagrams();

        _receiver.Receive(datagrams[0], Start + 10_000);
        _receiver.Receive(datagrams[2], Start + 10_000);
        _receiver.Update(Start + 11_000);

        // SendACKs, then one NAK datagram (ReliabilityLayer.cpp:1810-1825); the NAK holds the skipped numbers (871-877).
        var replies = TakeDatagrams(_receiver);
        Assert.Equal(2, replies.Count);
        var (ackHeader, acks) = DecodeRanges(replies[0]);
        Assert.Equal(DatagramKind.Ack, ackHeader.Kind);
        Assert.Equal([Range(0, 0), Range(2, 2)], acks.Ranges);
        var (nakHeader, naks) = DecodeRanges(replies[1]);
        Assert.Equal(DatagramKind.Nak, nakHeader.Kind);
        Assert.Equal([Range(1, 1)], naks.Ranges);

        _receiver.Update(Start + 12_000);
        Assert.Empty(TakeDatagrams(_receiver));
    }

    [Fact]
    public void A_late_datagram_is_still_accepted_and_delivered_after_its_number_was_naked()
    {
        var datagrams = SendThreeDatagrams();
        _receiver.Receive(datagrams[0], Start + 10_000);
        _receiver.Receive(datagrams[2], Start + 10_000);
        _receiver.Update(Start + 11_000);

        _receiver.Receive(datagrams[1], Start + 12_000);

        Assert.Equal(3, TakeEvents(_receiver).OfType<MessageDelivered>().Count());
    }

    [Fact]
    public void A_nak_brings_the_messages_of_that_datagram_forward_to_the_next_update()
    {
        _sender.Send("lost"u8.ToArray(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        TakeDatagrams(_sender);

        // ReliabilityLayer.cpp:841-854: nextActionTime becomes the time the NAK was read.
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForNak(), Range(0, 0)), Start + 20_000));
        _sender.Update(Start + 21_000);

        var (header, messages) = Decode(Assert.Single(TakeDatagrams(_sender)));
        Assert.Equal(new UInt24(1), header.DatagramNumber);
        Assert.Equal("lost"u8.ToArray(), Assert.Single(messages).Payload.ToArray());
    }

    [Fact]
    public void A_nak_for_a_datagram_never_sent_is_accepted_and_ignored()
    {
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForNak(), Range(0, 400)), Start + 1_000));

        _sender.Update(Start + 2_000);
        Assert.Empty(TakeDatagrams(_sender));
    }

    [Fact]
    public void A_nak_range_reaching_the_largest_datagram_number_is_accepted()
    {
        // Only ACKs reject a max of 0xFFFFFF (ReliabilityLayer.cpp:738 against 820).
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForNak(), new AckRange(new UInt24(0), UInt24.MaxValue)), Start + 1_000));
    }

    [Fact]
    public void A_message_acked_through_both_its_original_and_its_resent_datagram_reports_one_receipt()
    {
        _sender.Send("r"u8.ToArray(), Reliability.ReliableWithAckReceipt, Priority.High, 0, receipt: 5, Start);
        _sender.Update(Start + 1_000);
        _sender.Update(Start + 1_000 + InitialRtoUs);
        Assert.Equal(2, TakeDatagrams(_sender).Count);

        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(0, 1)), Start + 1_000 + InitialRtoUs + 5_000));

        Assert.Equal([new ReceiptAcknowledged(5)], TakeEvents(_sender));
        Assert.Equal(0, _sender.MessagesInResendBuffer);
    }

    [Fact]
    public void A_backlog_is_sent_faster_as_acknowledgements_grow_the_window()
    {
        for (var i = 0; i < 200; i++)
            _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start);

        var mostDatagramsInOneUpdate = 0;
        for (var now = Start + 10_000; _sender.IsOutgoingDataWaiting && now < Start + 10_000_000; now += 10_000)
        {
            _sender.Update(now);
            var datagrams = TakeDatagrams(_sender);
            mostDatagramsInOneUpdate = Math.Max(mostDatagramsInOneUpdate, datagrams.Count);
            foreach (var datagram in datagrams)
                _receiver.Receive(datagram, now + 1_000);
            _receiver.Update(now + 2_000);
            Deliver(_receiver, _sender, now + 3_000);
        }

        // The window starts at one datagram payload, so the first update sends two datagrams (ReliabilityLayer.cpp:1982);
        // acks during the continuous send grow it a datagram each in slow start (CCRakNetSlidingWindow.cpp:239-244).
        Assert.False(_sender.IsOutgoingDataWaiting);
        Assert.True(mostDatagramsInOneUpdate > 4, $"At most {mostDatagramsInOneUpdate} datagrams went out in one update.");
    }

    [Fact]
    public void Unreliable_messages_queued_longer_than_the_unreliable_timeout_are_discarded_unsent()
    {
        _sender.UnreliableTimeoutMs = 1_000;
        FillTheWindow();
        _sender.Send("stale"u8.ToArray(), Reliability.UnreliableWithAckReceipt, Priority.High, 0, receipt: 77, Start + 2_000);

        // ReliabilityLayer.cpp:1745-1793: culled once older than the timeout, at a cull pass every half timeout.
        for (var now = Start + 10_000; now <= Start + 1_500_000; now += 10_000)
            _sender.Update(now);
        Assert.Empty(TakeDatagrams(_sender));
        Assert.Equal([new ReceiptDiscarded(77)], TakeEvents(_sender));

        // Once acknowledgements open the window the reliable backlog goes, and the discarded message never does.
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(0, 1)), Start + 1_510_000));
        _sender.Update(Start + 1_520_000);
        var sent = TakeDatagrams(_sender).SelectMany(d => Decode(d).Messages).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, m => Assert.Equal(Reliability.Reliable, m.Reliability));
    }

    [Fact]
    public void Without_an_unreliable_timeout_queued_unreliable_messages_wait_for_the_window()
    {
        // ReliabilityLayer::InitializeVariables sets unreliableTimeout to 0 (ReliabilityLayer.cpp:404).
        FillTheWindow();
        _sender.Send("kept"u8.ToArray(), Reliability.Unreliable, Priority.High, 0, 0, Start + 2_000);
        for (var now = Start + 10_000; now <= Start + 1_500_000; now += 10_000)
            _sender.Update(now);

        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(0, 1)), Start + 1_510_000));
        _sender.Update(Start + 1_520_000);

        var sent = TakeDatagrams(_sender).SelectMany(d => Decode(d).Messages).ToList();
        Assert.Contains(sent, m => m.Reliability == Reliability.Unreliable && m.Payload.Span.SequenceEqual("kept"u8));
    }

    [Fact]
    public void An_outgoing_bandwidth_limit_holds_new_data_once_a_seconds_worth_has_been_sent()
    {
        // 8000 bits per second is 1000 bytes. The first datagram carries three 400-byte messages; after it the
        // last second's USER_MESSAGE_BYTES_SENT exceeds the limit and nothing more goes until it ages out
        // (ReliabilityLayer.cpp:1995-1999, BPSTracker::ClearExpired1 at 95-108).
        const uint limit = 8_000;
        for (var i = 0; i < 9; i++)
            _sender.Send(new byte[400], Reliability.Unreliable, Priority.High, 0, 0, Start);

        var datagramTimes = new List<long>();
        for (var now = Start + 1_000; now <= Start + 2_500_000; now += 10_000)
        {
            _sender.Update(now, limit);
            foreach (var datagram in TakeDatagrams(_sender))
            {
                Assert.Equal(3, Decode(datagram).Messages.Count);
                datagramTimes.Add(now);
            }
        }

        Assert.Equal(3, datagramTimes.Count);
        Assert.Equal(Start + 1_000, datagramTimes[0]);
        Assert.InRange(datagramTimes[1] - datagramTimes[0], 1_000_001, 1_120_000);
        Assert.InRange(datagramTimes[2] - datagramTimes[1], 1_000_001, 1_120_000);
    }

    [Fact]
    public void Statistics_count_bytes_pushed_sent_and_resent_and_what_waits_for_an_ack()
    {
        _sender.Send(new byte[100], Reliability.Reliable, Priority.Medium, 0, 0, Start);
        var queued = _sender.GetStatistics(Start);
        Assert.Equal(1u, queued.MessagesInSendBuffer[(int)Priority.Medium]);
        Assert.Equal(100.0, queued.BytesInSendBuffer[(int)Priority.Medium]);

        _sender.Update(Start + 1_000);
        _sender.Update(Start + 1_000 + InitialRtoUs);
        var datagrams = TakeDatagrams(_sender);

        var stats = _sender.GetStatistics(Start + 1_000 + InitialRtoUs);
        Assert.Equal(100ul, stats.RunningTotal[(int)SessionMetric.UserMessageBytesPushed]);
        Assert.Equal(100ul, stats.RunningTotal[(int)SessionMetric.UserMessageBytesSent]);
        Assert.Equal(100ul, stats.RunningTotal[(int)SessionMetric.UserMessageBytesResent]);
        Assert.Equal((ulong)datagrams.Sum(d => d.Length), stats.RunningTotal[(int)SessionMetric.ActualBytesSent]);
        Assert.Equal(0u, stats.MessagesInSendBuffer[(int)Priority.Medium]);
        Assert.Equal(1u, stats.MessagesInResendBuffer);
        Assert.Equal(100ul, stats.BytesInResendBuffer);
        Assert.Equal(Start, stats.ConnectionStartTimeUs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Reliable_ordered_messages_arrive_once_and_in_order_on_every_channel_over_a_lossy_link(int seed)
    {
        var link = new LossyLink(seed, lossProbability: 0.2, duplicateProbability: 0.05, maxDelayUs: 30_000);
        const int count = 300;
        const int channels = 4;
        for (var i = 0; i < count; i++)
        {
            var payload = new byte[200];
            payload[0] = (byte)(i % channels);
            payload[1] = (byte)(i / channels);
            link.A.Send(payload, Reliability.ReliableOrdered, Priority.High, (byte)(i % channels), 0, link.NowUs);
        }

        Assert.True(link.RunUntil(() => Delivered(link.EventsAtB).Count >= count && !link.A.IsOutgoingDataWaiting, 120_000_000));

        var delivered = Delivered(link.EventsAtB);
        Assert.Equal(count, delivered.Count);
        for (byte channel = 0; channel < channels; channel++)
        {
            var onChannel = delivered.Where(p => p[0] == channel).Select(p => (int)p[1]);
            Assert.Equal(Enumerable.Range(0, count / channels), onChannel);
        }
        Assert.True(link.Dropped > 0 && link.Duplicated > 0);
        Assert.False(link.A.IsDead || link.B.IsDead);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Reliable_messages_sent_both_ways_at_once_each_arrive_exactly_once_over_a_lossy_link(int seed)
    {
        var link = new LossyLink(seed, lossProbability: 0.25, duplicateProbability: 0.1, maxDelayUs: 50_000);
        const int count = 200;
        for (var i = 0; i < count; i++)
        {
            link.A.Send([0xA0, (byte)i, .. new byte[300]], Reliability.Reliable, Priority.High, 0, 0, link.NowUs);
            link.B.Send([0xB0, (byte)i, .. new byte[300]], Reliability.Reliable, Priority.Low, 0, 0, link.NowUs);
        }

        Assert.True(link.RunUntil(
            () => Delivered(link.EventsAtA).Count >= count && Delivered(link.EventsAtB).Count >= count
                && !link.A.IsOutgoingDataWaiting && !link.B.IsOutgoingDataWaiting,
            120_000_000));

        Assert.Equal(Enumerable.Range(0, count), Delivered(link.EventsAtB).Select(p => (int)p[1]).Order());
        Assert.Equal(Enumerable.Range(0, count), Delivered(link.EventsAtA).Select(p => (int)p[1]).Order());
        Assert.All(Delivered(link.EventsAtB), p => Assert.Equal(0xA0, p[0]));
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    public void Reliable_sequenced_messages_arrive_in_increasing_order_ending_with_the_last_over_a_lossy_link(int seed)
    {
        var link = new LossyLink(seed, lossProbability: 0.2, duplicateProbability: 0.05, maxDelayUs: 40_000);
        const int count = 150;

        for (var i = 0; i < count; i++)
        {
            link.A.Send([0x86, (byte)i], Reliability.ReliableSequenced, Priority.High, 2, 0, link.NowUs);
            link.Tick();
        }
        Assert.True(link.RunUntil(() => !link.A.IsOutgoingDataWaiting, 60_000_000));

        var sequence = Delivered(link.EventsAtB).Select(p => (int)p[1]).ToList();
        Assert.Equal(sequence.Order().Distinct(), sequence);
        Assert.Equal(count - 1, sequence[^1]);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    public void Every_receipt_is_reported_exactly_once_over_a_lossy_link(int seed)
    {
        var link = new LossyLink(seed, lossProbability: 0.2, duplicateProbability: 0.05, maxDelayUs: 30_000);
        const uint count = 100;
        for (uint i = 0; i < count; i++)
        {
            link.A.Send([0x86, (byte)i], Reliability.ReliableWithAckReceipt, Priority.High, 0, receipt: 1_000 + i, link.NowUs);
            link.A.Send([0x87, (byte)i], Reliability.UnreliableWithAckReceipt, Priority.High, 0, receipt: 2_000 + i, link.NowUs);
            link.Tick();
        }

        // Unreliable receipts resolve within the 2 s RTO cap of the last send.
        Assert.True(link.RunUntil(() => !link.A.IsOutgoingDataWaiting, 60_000_000));
        link.RunUntil(() => false, 3_000_000);

        var acked = link.EventsAtA.OfType<ReceiptAcknowledged>().Select(r => r.Receipt).ToList();
        var lost = link.EventsAtA.OfType<ReceiptLost>().Select(r => r.Receipt).ToList();
        Assert.Equal(Enumerable.Range(1_000, (int)count).Select(i => (uint)i), acked.Where(r => r < 2_000).Order());
        Assert.DoesNotContain(lost, r => r < 2_000);
        Assert.Equal(Enumerable.Range(2_000, (int)count).Select(i => (uint)i), acked.Concat(lost).Where(r => r >= 2_000).Order());
        Assert.NotEmpty(lost);
    }

    [Fact]
    public void A_resend_during_a_continuous_send_backs_the_window_off_to_half_for_the_following_updates()
    {
        var (now, lastRound) = GrowTheWindowInAContinuousSend();
        Assert.True(lastRound.Count > 4, $"Only {lastRound.Count} datagrams went out in the last round.");

        // Acknowledge all but the first datagram of the round so only its message waits for a resend.
        _sender.Update(now);
        var round = TakeDatagrams(_sender);
        Assert.True(round.Count > 4, $"Only {round.Count} datagrams went out.");
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(NumberOf(round[1]), NumberOf(round[^1]))), now + 10_000));

        // The bandwidth for new data is taken before the resend loop runs (ReliabilityLayer.cpp:1894-1895), so the update
        // that resends still fills the old window.
        var resendAt = now + InitialRtoUs;
        _sender.Update(resendAt);
        var resendRound = TakeDatagrams(_sender);
        var firstMessage = Decode(round[0]).Messages[0].ReliableMessageNumber;
        Assert.Single(resendRound, d => Decode(d).Messages.Any(m => m.ReliableMessageNumber == firstMessage));
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(NumberOf(resendRound[0]), NumberOf(resendRound[^1]))), resendAt + 10_000));

        // OnResend dropped cwnd to one datagram payload and ssThresh to half the old window (CCRakNetSlidingWindow.cpp:
        // 162-183), so the acknowledgements regrow it only to about half: the next update sends half as much.
        _sender.Update(resendAt + 20_000);
        var next = TakeDatagrams(_sender);
        Assert.InRange(next.Count, 1, resendRound.Count * 6 / 10);
    }

    [Fact]
    public void A_nak_during_a_continuous_send_ends_slow_start()
    {
        var (now, lastRound) = GrowTheWindowInAContinuousSend();
        Assert.All(lastRound, d => Assert.True(Decode(d).Header.NeedsBAndAs));

        _sender.Update(now);
        var round = TakeDatagrams(_sender);
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(NumberOf(round[1]), NumberOf(round[^1]))), now + 10_000));
        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForNak(), Range(NumberOf(round[0]), NumberOf(round[0]))), now + 10_000));

        // OnNAK halves ssThresh below cwnd (CCRakNetSlidingWindow.cpp:185-198), so the next datagrams no longer ask for
        // B and AS (ReliabilityLayer.cpp:1828).
        _sender.Update(now + 11_000);
        var afterNak = TakeDatagrams(_sender);
        Assert.NotEmpty(afterNak);
        Assert.All(afterNak, d => Assert.False(Decode(d).Header.NeedsBAndAs));
    }

    [Fact]
    public void An_unreliable_receipt_sent_after_a_resend_in_the_same_update_is_acknowledged_through_its_own_datagram()
    {
        _sender.Send("r"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Assert.Single(TakeDatagrams(_sender));

        var resendAt = Start + 1_000 + InitialRtoUs;
        _sender.Send("u"u8.ToArray(), Reliability.UnreliableWithAckReceipt, Priority.High, 0, receipt: 9, resendAt);
        _sender.Update(resendAt);
        var datagrams = TakeDatagrams(_sender);

        // The resend fills datagram 1 and the new message goes in datagram 2 (ReliabilityLayer.cpp:1905-2137).
        Assert.Equal([1u, 2u], datagrams.Select(d => Decode(d).Header.DatagramNumber.Value));
        Assert.Equal("u"u8.ToArray(), Assert.Single(Decode(datagrams[1]).Messages).Payload.ToArray());

        Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(2, 2)), resendAt + 5_000));
        Assert.Equal([new ReceiptAcknowledged(9)], TakeEvents(_sender));
    }

    // A backlog sent in rounds 20 ms apart, each acknowledged by hand 10 ms after it went, so the window grows in slow
    // start during a continuous send. Returns the time of the next round and the datagrams of the last.
    private (long Now, List<byte[]> LastRound) GrowTheWindowInAContinuousSend()
    {
        for (var i = 0; i < 2_000; i++)
            _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start);

        var now = Start + 10_000;
        var round = new List<byte[]>();
        for (var r = 0; r < 4; r++, now += 20_000)
        {
            _sender.Update(now);
            round = TakeDatagrams(_sender);
            Assert.True(_sender.Receive(RangeDatagram(DatagramHeader.ForAck(), Range(NumberOf(round[0]), NumberOf(round[^1]))), now + 10_000));
        }
        Assert.True(_sender.IsOutgoingDataWaiting);
        return (now, round);
    }

    private static uint NumberOf(byte[] datagram) => Decode(datagram).Header.DatagramNumber.Value;

    // Three datagrams of one reliable message each, numbered 0 to 2.
    private List<byte[]> SendThreeDatagrams()
    {
        var datagrams = new List<byte[]>();
        for (var i = 0; i < 3; i++)
        {
            _sender.Send([0x86, (byte)i], Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
            _sender.Update(Start + 1_000 + i);
            datagrams.AddRange(TakeDatagrams(_sender));
        }
        Assert.Equal(3, datagrams.Count);
        return datagrams;
    }

    // Two datagrams of reliable data leave over 2000 unacknowledged bytes against a 1464-byte window, so nothing new
    // goes until they are acknowledged (GetTransmissionBandwidth, CCRakNetSlidingWindow.cpp:82-85).
    private void FillTheWindow()
    {
        for (var i = 0; i < 4; i++)
            _sender.Send(new byte[1000], Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Update(Start + 1_000);
        Assert.Equal(2, TakeDatagrams(_sender).Count);
    }

    private static List<byte[]> Delivered(List<SessionEvent> events) =>
        events.OfType<MessageDelivered>().Select(m => m.Payload.ToArray()).ToList();

    private static void Deliver(ReliabilityLayer from, ReliabilityLayer to, long nowUs)
    {
        foreach (var datagram in TakeDatagrams(from))
            Assert.True(to.Receive(datagram, nowUs));
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

    private static (DatagramHeader Header, AckRangeList Ranges) DecodeRanges(byte[] datagram)
    {
        var reader = new BitStreamReader(datagram);
        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
        return (header, ranges);
    }

    private static byte[] RangeDatagram(DatagramHeader header, AckRange range)
    {
        var writer = new BitStreamWriter(new byte[64]);
        header.Write(ref writer);
        new AckRangeList([range]).Write(ref writer);
        return writer.Written.ToArray();
    }

    private static AckRange Range(uint min, uint max) => new(new UInt24(min), new UInt24(max));
}
