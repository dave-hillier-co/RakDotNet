using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// The session reuses its send state, its delivery events and its output buffer so that the hot path allocates nothing.
// These tests pin down what reuse must not change: every message arrives with exactly its own bytes, and what a caller
// keeps stays as it was.
public class ReliabilityLayerReuseTests
{
    private const int Mtu = 1492;
    private const long Start = 1_000_000;

    private readonly ReliabilityLayer _sender = new(Mtu, Start);
    private readonly ReliabilityLayer _receiver = new(Mtu, Start);
    private long _nowUs = Start;

    [Fact]
    public void A_delivery_that_is_not_recycled_keeps_its_payload_while_later_messages_arrive()
    {
        var first = Exchange([(byte)MessageId.UserPacketEnum, 1, 2, 3]);
        for (var i = 0; i < 50; i++)
            Exchange([(byte)MessageId.UserPacketEnum, 9, 9, 9, 9, 9]);

        Assert.Equal([(byte)MessageId.UserPacketEnum, 1, 2, 3], first.Payload.ToArray());
    }

    [Fact]
    public void A_recycled_delivery_is_reused_for_a_later_message_with_that_messages_payload_and_length()
    {
        var first = Exchange([(byte)MessageId.UserPacketEnum, 1, 2, 3, 4, 5, 6, 7]);
        _receiver.Recycle(first);

        var second = Exchange([(byte)MessageId.UserPacketEnum, 8]);

        Assert.Same(first, second);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 8], second.Payload.ToArray());
        Assert.Equal(16, second.BitLength);
    }

    [Fact]
    public void Messages_of_every_size_and_reliability_arrive_intact_while_send_state_is_reused()
    {
        int[] sizes = [200, 3, 1_000, 1, 64, 3_000, 65, 1_400, 2];
        Reliability[] reliabilities =
        [
            Reliability.Reliable,
            Reliability.Unreliable,
            Reliability.ReliableOrdered,
            Reliability.UnreliableSequenced,
            Reliability.ReliableSequenced,
        ];
        var sent = new List<byte[]>();
        var received = new List<byte[]>();
        for (var round = 0; round < 40; round++)
        {
            for (var i = 0; i < sizes.Length; i++)
            {
                var message = new byte[sizes[i]];
                message[0] = (byte)MessageId.UserPacketEnum;
                for (var b = 1; b < message.Length; b++)
                    message[b] = (byte)(round * 31 + i * 7 + b);
                sent.Add(message);
                _sender.Send(message, reliabilities[(round + i) % reliabilities.Length], Priority.High, channel: 0, receipt: 0, _nowUs);
            }
            for (var step = 0; step < 4; step++)
            {
                foreach (var delivered in Step())
                {
                    received.Add(delivered.Payload.ToArray());
                    _receiver.Recycle(delivered);
                }
            }
        }

        Assert.Equal(sent, received);
    }

    [Fact]
    public void An_ack_covering_far_more_numbers_than_were_sent_reports_the_receipts_of_the_datagrams_that_were_in_order()
    {
        var receipts = SendOneDatagramEachWithReceipts(3);

        _sender.Receive(Ack(new AckRange(new UInt24(0), new UInt24(100_000))), _nowUs);

        Assert.Equal(receipts, AcknowledgedReceipts());
    }

    [Fact]
    public void An_ack_naming_fewer_numbers_than_the_history_holds_reports_only_their_receipts()
    {
        var receipts = SendOneDatagramEachWithReceipts(5);

        _sender.Receive(Ack(new AckRange(new UInt24(1), new UInt24(2))), _nowUs);

        Assert.Equal(receipts[1..3], AcknowledgedReceipts());
    }

    [Fact]
    public void A_recycled_receipt_event_is_reused_for_the_next_receipt_and_one_kept_keeps_its_receipt()
    {
        var receipts = SendOneDatagramEachWithReceipts(3);
        _sender.Receive(Ack(new AckRange(new UInt24(0), new UInt24(0))), _nowUs);
        Assert.True(_sender.TryDequeueEvent(out var first));
        _sender.Recycle(Assert.IsType<ReceiptAcknowledged>(first));
        _sender.Receive(Ack(new AckRange(new UInt24(1), new UInt24(1))), _nowUs);
        Assert.True(_sender.TryDequeueEvent(out var second));
        _sender.Receive(Ack(new AckRange(new UInt24(2), new UInt24(2))), _nowUs);
        Assert.True(_sender.TryDequeueEvent(out var third));

        Assert.Same(first, second);
        Assert.Equal(receipts[1], Assert.IsType<ReceiptAcknowledged>(second).Receipt);
        Assert.NotSame(second, third);
        Assert.Equal(receipts[2], Assert.IsType<ReceiptAcknowledged>(third).Receipt);
    }

    [Fact]
    public void Datagrams_copied_into_a_caller_buffer_are_the_datagrams_returned_as_arrays()
    {
        var other = new ReliabilityLayer(Mtu, Start);
        foreach (var layer in new[] { _sender, other })
        {
            for (var i = 0; i < 40; i++)
                layer.Send(new byte[300], Reliability.ReliableOrdered, Priority.High, channel: 0, receipt: 0, Start);
            layer.Update(Start + 1_000);
        }

        var asArrays = new List<byte[]>();
        while (_sender.TryDequeueDatagram(out var datagram))
            asArrays.Add(datagram);
        var copied = new List<byte[]>();
        var buffer = new byte[Mtu];
        while (other.TryDequeueDatagram(buffer, out var length))
            copied.Add(buffer[..length]);

        Assert.True(asArrays.Count > 1);
        Assert.Equal(asArrays, copied);
    }

    private MessageDelivered Exchange(byte[] message)
    {
        _sender.Send(message, Reliability.ReliableOrdered, Priority.High, channel: 0, receipt: 0, _nowUs);
        return Assert.Single(Step());
    }

    // One millisecond: the sender updates, its datagrams reach the receiver, the receiver updates and its ACKs come back.
    private List<MessageDelivered> Step()
    {
        _nowUs += 1_000;
        _sender.Update(_nowUs);
        Move(_sender, _receiver);
        _receiver.Update(_nowUs);
        Move(_receiver, _sender);
        while (_sender.TryDequeueEvent(out _))
        {
        }
        var delivered = new List<MessageDelivered>();
        while (_receiver.TryDequeueEvent(out var e))
            delivered.Add(Assert.IsType<MessageDelivered>(e));
        return delivered;
    }

    private void Move(ReliabilityLayer from, ReliabilityLayer to)
    {
        while (from.TryDequeueDatagram(out var datagram))
            to.Receive(datagram, _nowUs);
    }

    // Each update sends one datagram holding one message whose receipt is reported when that datagram is acknowledged.
    private uint[] SendOneDatagramEachWithReceipts(int count)
    {
        var receipts = new uint[count];
        for (var i = 0; i < count; i++)
        {
            receipts[i] = (uint)(100 + i);
            _sender.Send([(byte)MessageId.UserPacketEnum], Reliability.ReliableWithAckReceipt, Priority.High, 0, receipts[i], _nowUs);
            _nowUs += 1_000;
            _sender.Update(_nowUs);
            Assert.True(_sender.TryDequeueDatagram(out _));
            Assert.False(_sender.TryDequeueDatagram(out _));
        }
        return receipts;
    }

    private uint[] AcknowledgedReceipts()
    {
        var receipts = new List<uint>();
        while (_sender.TryDequeueEvent(out var e))
            receipts.Add(Assert.IsType<ReceiptAcknowledged>(e).Receipt);
        return [.. receipts];
    }

    private static byte[] Ack(AckRange range)
    {
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        DatagramHeader.ForAck().Write(ref writer);
        new AckRangeList([range]).Write(ref writer);
        return writer.Written.ToArray();
    }
}
