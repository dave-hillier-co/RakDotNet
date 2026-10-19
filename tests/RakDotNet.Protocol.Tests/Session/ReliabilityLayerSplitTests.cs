using System.Buffers.Binary;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// Split messages and reassembly: SplitPacket (ReliabilityLayer.cpp:2928-3033), InsertIntoSplitPacketList (3038-3175) and
// BuildPacketFromSplitPacketList (3180-3255). Sociable: real layers, datagrams carried by hand or by a seeded lossy link.
public class ReliabilityLayerSplitTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    // ReliabilityLayer.cpp:1604 and 2939: the datagram budget (1492 - 28 - 9) less the largest message header (23 bytes).
    private const int BlockBytes = 1432;

    private readonly ReliabilityLayer _sender = new(Mtu, Start);
    private readonly ReliabilityLayer _receiver = new(Mtu, Start);

    [Theory]
    [InlineData(576, 516)]
    [InlineData(1200, 1140)]
    [InlineData(1492, 1432)]
    public void A_message_exactly_at_the_limit_is_sent_whole_and_one_byte_more_is_split_in_two(int mtu, int limit)
    {
        var sender = new ReliabilityLayer(mtu, Start);

        Assert.True(sender.Send(Payload(limit), Reliability.ReliableOrdered, Priority.High, 0, 0, Start));
        Assert.True(sender.Send(Payload(limit + 1), Reliability.ReliableOrdered, Priority.High, 0, 0, Start));
        var messages = SendAll(sender, new ReliabilityLayer(mtu, Start));

        Assert.Null(messages[0].Split);
        Assert.Equal(limit, messages[0].Payload.Length);
        Assert.Equal([new SplitHeader(2, 0, 0), new SplitHeader(2, 0, 1)], messages.Skip(1).Select(m => m.Split!.Value));
        Assert.Equal([limit, 1], messages.Skip(1).Select(m => m.Payload.Length));
        // Every datagram holds at most budget bytes after its header (ReliabilityLayer.cpp:2018-2020).
        Assert.All(messages, m => Assert.True(m.Payload.Length + (EncapsulatedMessage.GetHeaderLengthBits(m.Reliability, m.Split is not null) + 7) / 8 <= mtu - 28 - 9));
    }

    [Fact]
    public void The_parts_carry_consecutive_message_numbers_and_share_the_ordering_index_and_channel()
    {
        _sender.Send("o"u8, Reliability.ReliableOrdered, Priority.High, channel: 5, 0, Start);
        _sender.Send(Payload(3 * BlockBytes), Reliability.ReliableOrdered, Priority.High, channel: 5, 0, Start);

        var parts = SendAll(_sender, _receiver).Skip(1).ToList();

        Assert.Equal([1u, 2u, 3u], parts.Select(p => p.ReliableMessageNumber.Value));
        Assert.All(parts, p => Assert.Equal(new UInt24(1), p.OrderingIndex));
        Assert.All(parts, p => Assert.Equal(5, p.OrderingChannel));
        Assert.Equal([0u, 1u, 2u], parts.Select(p => p.Split!.Value.Index));
        Assert.All(parts, p => Assert.Equal(3u, p.Split!.Value.Count));
    }

    [Theory]
    [InlineData(Reliability.Unreliable, Reliability.Reliable)]
    [InlineData(Reliability.UnreliableSequenced, Reliability.ReliableSequenced)]
    [InlineData(Reliability.UnreliableWithAckReceipt, Reliability.Reliable)]
    [InlineData(Reliability.Reliable, Reliability.Reliable)]
    [InlineData(Reliability.ReliableSequenced, Reliability.ReliableSequenced)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, Reliability.ReliableOrdered)]
    public void Every_part_of_a_split_message_is_sent_reliably(Reliability sent, Reliability onTheWire)
    {
        // ReliabilityLayer.cpp:1609-1622: "Split packets cannot be unreliable". Receipts are written as the base type.
        _sender.Send(Payload(BlockBytes + 1), sent, Priority.High, 0, 0, Start);

        var parts = SendAll(_sender, _receiver);

        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(onTheWire, p.Reliability));
    }

    [Fact]
    public void Each_split_message_takes_the_next_split_id_which_wraps_to_zero()
    {
        // splitPacketId++ "It's ok if this wraps to 0" (ReliabilityLayer.cpp:3001); SplitPacketIdType is 16 bits. Split
        // ids 0 to 65534 wait at low priority while 65535 and the wrapped 0 go first at immediate priority.
        var sender = new ReliabilityLayer(576, Start);
        var data = Payload(517);
        for (var i = 0; i < 65_535; i++)
            sender.Send(data, Reliability.Reliable, Priority.Low, 0, 0, Start);
        sender.Send(data, Reliability.Reliable, Priority.Immediate, 0, 0, Start);
        sender.Send(data, Reliability.Reliable, Priority.Immediate, 0, 0, Start);

        var parts = TakeDataDatagrams(sender, maxDatagrams: 4).SelectMany(Messages).ToList();

        Assert.Equal([(65_535, 0u), (65_535, 1u), (0, 0u), (0, 1u)], parts.Select(p => ((int)p.Split!.Value.Id, p.Split!.Value.Index)));
    }

    [Fact]
    public void A_split_message_is_reassembled_from_its_parts_and_delivered_once_with_its_bit_length()
    {
        var data = Payload(4000);

        _sender.Send(data, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        SendAll(_sender, _receiver);

        var delivered = Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>());
        Assert.Equal(data, delivered.Payload.ToArray());
        Assert.Equal(4000 * 8, delivered.BitLength);
    }

    [Fact]
    public void A_bit_length_that_is_not_whole_bytes_survives_splitting()
    {
        // ReliabilityLayer.cpp:2990-2991: a short last part carries the leftover bits.
        var data = Payload(BlockBytes + 1);
        data[^1] = 0xE0;

        _sender.Send(data, (BlockBytes + 1) * 8 - 5, Reliability.Reliable, Priority.High, 0, 0, Start);
        var parts = SendAll(_sender, _receiver);

        Assert.Equal([BlockBytes * 8, 3], parts.Select(p => p.PayloadBitLength));
        var delivered = Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>());
        Assert.Equal((BlockBytes + 1) * 8 - 5, delivered.BitLength);
        Assert.Equal(data, delivered.Payload.ToArray());
    }

    [Fact]
    public void A_last_part_that_fills_its_block_is_sent_as_whole_bytes_as_native_RakNet_does()
    {
        // ReliabilityLayer.cpp:2990-2993: a part of exactly maximumSendBlockBytes is given bytesToSend << 3 bits, so the
        // spare bits of the final byte are sent and the reassembled message is that much longer.
        _sender.Send(Payload(2 * BlockBytes), 2 * BlockBytes * 8 - 3, Reliability.Reliable, Priority.High, 0, 0, Start);
        var parts = SendAll(_sender, _receiver);

        Assert.Equal([BlockBytes * 8, BlockBytes * 8], parts.Select(p => p.PayloadBitLength));
        Assert.Equal(2 * BlockBytes * 8, Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>()).BitLength);
    }

    [Fact]
    public void Parts_arriving_in_any_order_are_reassembled_by_split_index()
    {
        var data = Payload(3 * BlockBytes + 10);
        _sender.Send(data, Reliability.Reliable, Priority.High, 0, 0, Start);
        var datagrams = TakeDataDatagrams(_sender);
        Assert.Equal(4, datagrams.Count);

        var now = Start + 100_000;
        foreach (var index in new[] { 2, 0, 3, 1 })
            Assert.True(_receiver.Receive(datagrams[index], now++));

        Assert.Equal(data, Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>()).Payload.ToArray());
    }

    [Fact]
    public void A_duplicated_part_is_ignored()
    {
        var data = Payload(BlockBytes + 50);
        _sender.Send(data, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        var datagrams = TakeDataDatagrams(_sender);

        var now = Start + 100_000;
        foreach (var index in new[] { 0, 0, 1, 1, 0 })
            Assert.True(_receiver.Receive(datagrams[index], now++));

        Assert.Equal(data, Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>()).Payload.ToArray());
    }

    [Fact]
    public void Acknowledgements_go_out_as_soon_as_the_last_part_arrives()
    {
        // BuildPacketFromSplitPacketList: "Ack immediately, because for large files this can take a long time"
        // (ReliabilityLayer.cpp:3243-3244).
        _sender.Send(Payload(BlockBytes + 1), Reliability.Reliable, Priority.High, 0, 0, Start);
        var datagrams = TakeDataDatagrams(_sender);

        Assert.True(_receiver.Receive(datagrams[0], Start + 100_000));
        Assert.Empty(TakeDatagrams(_receiver));
        Assert.Equal(1, _receiver.IncompleteSplitMessages);
        Assert.True(_receiver.Receive(datagrams[1], Start + 100_001));
        Assert.Equal(0, _receiver.IncompleteSplitMessages);

        var reader = new BitStreamReader(Assert.Single(TakeDatagrams(_receiver)));
        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Ack, header.Kind);
        Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
        Assert.Equal([new AckRange(new UInt24(0), new UInt24(1))], ranges.Ranges);
    }

    [Fact]
    public void A_split_message_with_a_receipt_reports_it_once_when_the_last_part_is_acknowledged()
    {
        // RemovePacketFromResendListAndDeleteOlderReliableSequenced (ReliabilityLayer.cpp:2486-2488): only the part with
        // the last split index reports the receipt, whether or not the others were acknowledged first.
        _sender.Send(Payload(2 * BlockBytes + 1), Reliability.ReliableOrderedWithAckReceipt, Priority.High, 0, receipt: 77, Start);
        // The first datagram fills less than the initial window, so parts 0 and 1 go out together.
        _sender.Update(Start + 1_000);
        Assert.Equal(2, TakeDatagrams(_sender).Count);
        Assert.True(_sender.Receive(AckDatagram(1, 1), Start + 2_000));
        Assert.Empty(TakeEvents(_sender));

        _sender.Update(Start + 3_000);
        Assert.Single(TakeDatagrams(_sender));
        Assert.True(_sender.Receive(AckDatagram(2, 2), Start + 4_000));
        Assert.Equal([new ReceiptAcknowledged(77)], TakeEvents(_sender));
        Assert.True(_sender.Receive(AckDatagram(0, 0), Start + 5_000));
        Assert.Empty(TakeEvents(_sender));
    }

    [Fact]
    public void Split_messages_interleave_with_other_messages_and_each_arrives_intact_and_in_channel_order()
    {
        var big = Payload(5 * BlockBytes + 7, seed: 1);
        var low = Payload(4 * BlockBytes + 3, seed: 2);
        var small = new List<byte[]>();

        _sender.Send(low, Reliability.Reliable, Priority.Low, 0, 0, Start);
        _sender.Send(big, Reliability.ReliableOrdered, Priority.Medium, channel: 2, 0, Start);
        for (var i = 0; i < 6; i++)
        {
            byte[] message = [(byte)MessageId.UserPacketEnum, (byte)i];
            small.Add(message);
            _sender.Send(message, Reliability.ReliableOrdered, Priority.High, channel: 2, 0, Start);
        }

        var messages = SendAll(_sender, _receiver);

        // Higher priorities go first and lower ones are interleaved with them (GetNextWeight), so the parts of the two
        // split messages share datagrams and alternate.
        var splitIds = messages.Where(m => m.Split is not null).Select(m => m.Split!.Value.Id).ToList();
        Assert.Contains(splitIds.Zip(splitIds.Skip(1)), pair => pair.First != pair.Second);
        var delivered = TakeEvents(_receiver).OfType<MessageDelivered>().Select(d => d.Payload.ToArray()).ToList();
        Assert.Equal(8, delivered.Count);
        Assert.Contains(delivered, d => d.AsSpan().SequenceEqual(low));
        // Channel 2: the big message was sent first, so it is delivered before the small ones that were sent after it.
        var channel2 = delivered.Where(d => !d.AsSpan().SequenceEqual(low)).ToList();
        Assert.Equal([big, .. small], channel2);
    }

    [Fact]
    public void A_split_sequenced_message_is_delivered_when_it_is_the_newest()
    {
        var data = Payload(BlockBytes * 2);
        _sender.Send(data, Reliability.UnreliableSequenced, Priority.High, 3, 0, Start);

        SendAll(_sender, _receiver);

        Assert.Equal(data, Assert.Single(TakeEvents(_receiver).OfType<MessageDelivered>()).Payload.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Messages_of_several_megabytes_arrive_intact_through_loss_duplication_and_reordering(int seed)
    {
        var link = new LossyLink(seed, lossProbability: 0.1, duplicateProbability: 0.05, maxDelayUs: 20_000);
        var first = Payload(3 * 1024 * 1024, seed: 3);
        var second = Payload(200_000, seed: 4);
        link.A.Send(first, Reliability.ReliableOrdered, Priority.High, 0, 0, link.NowUs);
        link.A.Send(second, Reliability.ReliableOrdered, Priority.High, 0, 0, link.NowUs);

        Assert.True(link.RunUntil(() => link.EventsAtB.OfType<MessageDelivered>().Count() == 2, 600_000_000, tickUs: 10_000));

        var delivered = link.EventsAtB.OfType<MessageDelivered>().ToList();
        Assert.True(first.AsSpan().SequenceEqual(delivered[0].Payload.Span));
        Assert.True(second.AsSpan().SequenceEqual(delivered[1].Payload.Span));
        Assert.True(link.Dropped > 0);
    }

    [Fact]
    public void Download_progress_is_reported_every_interval_parts_once_the_first_part_has_arrived()
    {
        // InsertIntoSplitPacketList, ReliabilityLayer.cpp:3135-3172: ID_DOWNLOAD_PROGRESS, then parts so far, part count
        // and the first part's byte length as host-order (little-endian) 32-bit values, then the first part's data.
        var data = Payload(4 * BlockBytes, seed: 5);
        _receiver.SplitMessageProgressInterval = 2;
        _sender.Send(data, Reliability.Reliable, Priority.High, 0, 0, Start);
        var datagrams = TakeDataDatagrams(_sender);

        var now = Start + 100_000;
        foreach (var index in new[] { 1, 2, 0, 3 })
            Assert.True(_receiver.Receive(datagrams[index], now++));

        var delivered = TakeEvents(_receiver).OfType<MessageDelivered>().Select(d => d.Payload.ToArray()).ToList();
        // After two parts the first is missing, so nothing; after three the count is not a multiple of two; after four
        // the message is complete.
        Assert.Equal([data], delivered);

        _receiver.SplitMessageProgressInterval = 1;
        _sender.Send(data, Reliability.Reliable, Priority.High, 0, 0, Start);
        datagrams = TakeDataDatagrams(_sender, Start + 200_000);
        foreach (var index in new[] { 1, 0, 2, 3 })
            Assert.True(_receiver.Receive(datagrams[index], now++));

        delivered = TakeEvents(_receiver).OfType<MessageDelivered>().Select(d => d.Payload.ToArray()).ToList();
        Assert.Equal(3, delivered.Count);
        Assert.Equal(data, delivered[2]);
        Assert.Equal(Progress(arrived: 2, count: 4, data.AsSpan(0, BlockBytes)), delivered[0]);
        Assert.Equal(Progress(arrived: 3, count: 4, data.AsSpan(0, BlockBytes)), delivered[1]);
    }

    [Fact]
    public void The_native_clients_split_message_is_reassembled_from_the_golden_vector()
    {
        // tests/vectors/split-packet.json: datagrams 13, 14 and 16 carry the three parts of a 4000-byte RELIABLE_ORDERED
        // message (ScenarioFlows.SplitPacketAsync); 4, 8 and 10 carry the handshake before it.
        var captured = GoldenVectors.Load("split-packet");
        var server = new ReliabilityLayer(Mtu, Start);
        var now = Start;
        foreach (var index in new[] { 4, 8, 10, 13, 14, 16 })
            Assert.True(server.Receive(captured.Single(d => d.Index == index).Bytes, now += 1_000));

        var last = TakeEvents(server).OfType<MessageDelivered>().Last();
        Assert.Equal(ScenarioMessage(), last.Payload.ToArray());
    }

    [Fact]
    public void The_parts_match_the_native_clients_byte_for_byte()
    {
        // The native client sent a RELIABLE connection request and a RELIABLE_ORDERED NEW_INCOMING_CONNECTION first
        // (golden vector datagrams 4 and 8), so the split message has message numbers 2 to 4 and ordering index 1.
        var captured = GoldenVectors.Load("split-packet");
        int[] partDatagrams = [13, 14, 16];
        var expected = partDatagrams
            .SelectMany(i => Messages(captured.Single(d => d.Index == i).Bytes))
            .Select(Encode)
            .ToList();

        _sender.Send("r"u8, Reliability.Reliable, Priority.High, 0, 0, Start);
        _sender.Send("o"u8, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        _sender.Send(ScenarioMessage(), Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        var actual = SendAll(_sender, _receiver).Where(m => m.Split is not null).Select(Encode).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Split_parts_are_counted_in_the_send_buffer_statistics_one_by_one()
    {
        // SplitPacket queues each part with its own statistics (ReliabilityLayer.cpp:3017-3018).
        _sender.Send(Payload(BlockBytes + 1), Reliability.Reliable, Priority.Medium, 0, 0, Start);

        var statistics = _sender.GetStatistics(Start);

        Assert.Equal(2u, statistics.MessagesInSendBuffer[(int)Priority.Medium]);
        Assert.Equal(BlockBytes + 1, statistics.BytesInSendBuffer[(int)Priority.Medium]);
        Assert.Equal((ulong)BlockBytes + 1, statistics.RunningTotal[(int)SessionMetric.UserMessageBytesPushed]);
    }

    private static byte[] Progress(uint arrived, uint count, ReadOnlySpan<byte> firstPart)
    {
        var progress = new byte[13 + firstPart.Length];
        progress[0] = (byte)MessageId.DownloadProgress;
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(1), arrived);
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(5), count);
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(9), (uint)firstPart.Length);
        firstPart.CopyTo(progress.AsSpan(13));
        return progress;
    }

    // The 4000-byte message of ScenarioFlows.SplitPacketAsync.
    private static byte[] ScenarioMessage()
    {
        var data = new byte[4000];
        data[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 1; i < data.Length; i++)
            data[i] = (byte)(i % 251);
        return data;
    }

    private static byte[] Payload(int length, int seed = 0)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        data[0] = (byte)MessageId.UserPacketEnum;
        return data;
    }

    private static string Encode(EncapsulatedMessage message)
    {
        var writer = new BitStreamWriter(new byte[message.Payload.Length + 32]);
        message.Write(ref writer);
        return Convert.ToHexStringLower(writer.Written);
    }

    // Updates the sender until it has sent everything, carrying data to the receiver and acknowledgements back, and
    // returns the messages of every data datagram the sender sent, in order.
    private static List<EncapsulatedMessage> SendAll(ReliabilityLayer sender, ReliabilityLayer receiver)
    {
        var messages = new List<EncapsulatedMessage>();
        var now = Start;
        for (var i = 0; i < 10_000 && (sender.IsOutgoingDataWaiting || i == 0); i++)
        {
            now += 10_000;
            sender.Update(now);
            foreach (var datagram in TakeDatagrams(sender))
            {
                messages.AddRange(Messages(datagram));
                Assert.True(receiver.Receive(datagram, now));
            }
            receiver.Update(now + 1);
            foreach (var reply in TakeDatagrams(receiver))
                Assert.True(sender.Receive(reply, now + 2));
        }
        Assert.False(sender.IsOutgoingDataWaiting);
        return messages;
    }

    // The sender's data datagrams for everything queued, up to maxDatagrams. A separate layer acknowledges them so the
    // congestion window opens; the receiver under test sees none of them.
    private static List<byte[]> TakeDataDatagrams(ReliabilityLayer sender, long from = Start, int maxDatagrams = int.MaxValue)
    {
        var acker = new ReliabilityLayer(sender.Mtu, from);
        var datagrams = new List<byte[]>();
        var now = from;
        for (var i = 0; i < 10_000 && sender.IsOutgoingDataWaiting && datagrams.Count < maxDatagrams; i++)
        {
            now += 1_000;
            sender.Update(now);
            foreach (var datagram in TakeDatagrams(sender))
            {
                if (!EncapsulatedMessageReader.TryOpenDatagram(datagram, out _, out _))
                    continue;
                datagrams.Add(datagram);
                Assert.True(acker.Receive(datagram, now));
            }
            acker.Update(now + 1);
            foreach (var reply in TakeDatagrams(acker))
                Assert.True(sender.Receive(reply, now + 2));
        }
        return datagrams.Take(maxDatagrams).ToList();
    }

    private static List<EncapsulatedMessage> Messages(byte[] datagram)
    {
        var messages = new List<EncapsulatedMessage>();
        if (!EncapsulatedMessageReader.TryOpenDatagram(datagram, out _, out var reader))
            return messages;
        while (reader.TryReadNext(out var message))
            messages.Add(message);
        Assert.False(reader.IsMalformed);
        return messages;
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

    private static byte[] AckDatagram(uint min, uint max)
    {
        var writer = new BitStreamWriter(new byte[64]);
        DatagramHeader.ForAck().Write(ref writer);
        new AckRangeList([new AckRange(new UInt24(min), new UInt24(max))]).Write(ref writer);
        return writer.Written.ToArray();
    }
}
