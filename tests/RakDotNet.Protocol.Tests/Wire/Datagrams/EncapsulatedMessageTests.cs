using System.Runtime.InteropServices;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using static RakDotNet.Protocol.Tests.Wire.Datagrams.WireWriter;

namespace RakDotNet.Protocol.Tests.Wire.Datagrams;

// Expected bytes are derived by hand from ReliabilityLayer::WriteToBitStreamFromInternalPacket
// (ReliabilityLayer.cpp:2610-2677): align, 3-bit reliability, 1-bit hasSplitPacket, align, u16 bit length big-endian,
// uint24 reliable message number (reliable types), align, uint24 sequencing index (sequenced types), uint24 ordering
// index and one-byte channel (ordered and sequenced types), u32 split count, u16 split id, u32 split index (all
// big-endian), then the payload bytes aligned.
public class EncapsulatedMessageTests
{
    private static readonly byte[] Aa = [0xAA];

    [Fact]
    public void Unreliable_message_is_flags_length_in_bits_and_payload()
    {
        var message = new EncapsulatedMessage(Reliability.Unreliable, new byte[] { 0x86 });

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("00 0008 86"), bytes);
    }

    [Fact]
    public void Reliable_message_adds_the_reliable_message_number()
    {
        // reliability 2 = 010, split 0 -> 0100 0000.
        var message = new EncapsulatedMessage(
            Reliability.Reliable,
            new byte[] { 0x86, 0x01 },
            reliableMessageNumber: new UInt24(0x010203)
        );

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("40 0010 030201 8601"), bytes);
    }

    [Fact]
    public void Reliable_ordered_message_adds_ordering_index_and_channel()
    {
        // reliability 3 = 011 -> 0110 0000.
        var message = new EncapsulatedMessage(
            Reliability.ReliableOrdered,
            new byte[] { 0x86, 0x01 },
            reliableMessageNumber: new UInt24(1),
            orderingIndex: new UInt24(2),
            orderingChannel: 3
        );

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("60 0010 010000 020000 03 8601"), bytes);
    }

    [Fact]
    public void Unreliable_sequenced_message_has_sequencing_and_ordering_but_no_message_number()
    {
        // reliability 1 = 001 -> 0010 0000. ReliabilityLayer.cpp:2643-2658.
        var message = new EncapsulatedMessage(
            Reliability.UnreliableSequenced,
            Aa,
            sequencingIndex: new UInt24(5),
            orderingIndex: new UInt24(6),
            orderingChannel: 7
        );

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("20 0008 050000 060000 07 AA"), bytes);
    }

    [Fact]
    public void Split_reliable_sequenced_message_carries_every_field()
    {
        // reliability 4 = 100, split 1 -> 1001 0000.
        var message = new EncapsulatedMessage(
            Reliability.ReliableSequenced,
            Aa,
            reliableMessageNumber: new UInt24(9),
            sequencingIndex: new UInt24(10),
            orderingIndex: new UInt24(11),
            orderingChannel: 0,
            split: new SplitHeader(2, 0x1234, 1)
        );

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("90 0008 090000 0A0000 0B0000 00 00000002 1234 00000001 AA"), bytes);
    }

    [Theory]
    [InlineData(Reliability.UnreliableWithAckReceipt, Reliability.Unreliable)]
    [InlineData(Reliability.ReliableWithAckReceipt, Reliability.Reliable)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, Reliability.ReliableOrdered)]
    public void Ack_receipt_variants_are_written_as_their_base_type(Reliability receipt, Reliability wire)
    {
        // ReliabilityLayer.cpp:2619-2626
        EncapsulatedMessage Make(Reliability r) =>
            new(r, Aa, reliableMessageNumber: new UInt24(4), orderingIndex: new UInt24(5), orderingChannel: 6);

        var receiptBytes = Write((ref BitStreamWriter w) => Make(receipt).Write(ref w));
        var wireBytes = Write((ref BitStreamWriter w) => Make(wire).Write(ref w));

        Assert.Equal(wireBytes, receiptBytes);
        var reader = new BitStreamReader(receiptBytes);
        Assert.True(EncapsulatedMessage.TryRead(ref reader, out var decoded));
        Assert.Equal(wire, decoded.Reliability);
    }

    [Fact]
    public void Length_is_in_bits_and_the_payload_is_rounded_up_to_whole_bytes()
    {
        var message = new EncapsulatedMessage(Reliability.Unreliable, new byte[] { 0xAB, 0xC0 }, payloadBitLength: 12);

        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w));

        Assert.Equal(Hex("00 000C ABC0"), bytes);
        var reader = new BitStreamReader(bytes);
        Assert.True(EncapsulatedMessage.TryRead(ref reader, out var decoded));
        Assert.Equal(12, decoded.PayloadBitLength);
        Assert.Equal(Hex("ABC0"), decoded.Payload.ToArray());
    }

    [Fact]
    public void Message_starts_on_a_byte_boundary()
    {
        // ReliabilityLayer.cpp:2618 aligns before the flags.
        var message = new EncapsulatedMessage(Reliability.Unreliable, Aa);

        var bytes = Write((ref BitStreamWriter w) =>
        {
            w.WriteBit(true);
            message.Write(ref w);
        });

        Assert.Equal(Hex("80 00 0008 AA"), bytes);
    }

    public static TheoryData<EncapsulatedMessage> Messages =>
        new()
        {
            new EncapsulatedMessage(Reliability.Unreliable, Hex("0102030405")),
            new EncapsulatedMessage(Reliability.UnreliableSequenced, Aa, sequencingIndex: UInt24.MaxValue, orderingIndex: new UInt24(1), orderingChannel: 31),
            new EncapsulatedMessage(Reliability.Reliable, Aa, reliableMessageNumber: new UInt24(0xABCDEF)),
            new EncapsulatedMessage(Reliability.ReliableOrdered, Aa, reliableMessageNumber: new UInt24(1), orderingIndex: new UInt24(2), orderingChannel: 3),
            new EncapsulatedMessage(Reliability.ReliableSequenced, Aa, reliableMessageNumber: new UInt24(1), sequencingIndex: new UInt24(2), orderingIndex: new UInt24(3), orderingChannel: 4),
            new EncapsulatedMessage(Reliability.Reliable, Hex("FFFF"), payloadBitLength: 9, reliableMessageNumber: new UInt24(7), split: new SplitHeader(uint.MaxValue, ushort.MaxValue, uint.MaxValue - 1)),
        };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Messages_round_trip(EncapsulatedMessage message)
    {
        var bytes = Write((ref BitStreamWriter w) => message.Write(ref w), out var bits);
        var reader = new BitStreamReader(bytes);

        Assert.True(EncapsulatedMessage.TryRead(ref reader, out var decoded));
        Assert.Equal(message, decoded);
        Assert.Equal(bits, reader.BitPosition);
        Assert.Equal(EncapsulatedMessage.GetHeaderLengthBits(message.Reliability, message.Split is not null), bits - message.Payload.Length * 8);
    }

    [Fact]
    public void Every_truncation_of_a_message_fails()
    {
        var bytes = Hex("90 0008 090000 0A0000 0B0000 00 00000002 1234 00000001 AA");

        for (var length = 0; length < bytes.Length; length++)
        {
            var reader = new BitStreamReader(bytes.AsSpan(0, length));
            Assert.False(EncapsulatedMessage.TryRead(ref reader, out _), $"prefix of {length} bytes");
        }
    }

    [Fact]
    public void Zero_bit_length_is_rejected()
    {
        // ReliabilityLayer.cpp:2761
        var reader = new BitStreamReader(Hex("00 0000 AA"));

        Assert.False(EncapsulatedMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void Length_longer_than_the_remaining_bits_is_rejected()
    {
        var reader = new BitStreamReader(Hex("00 0010 AA"));

        Assert.False(EncapsulatedMessage.TryRead(ref reader, out _));
    }

    [Theory]
    [InlineData("A0 0008 AA")] // 5 UNRELIABLE_WITH_ACK_RECEIPT
    [InlineData("C0 0008 AA")] // 6 RELIABLE_WITH_ACK_RECEIPT
    [InlineData("E0 0008 010000 020000 03 AA")] // 7 RELIABLE_ORDERED_WITH_ACK_RECEIPT
    public void Ack_receipt_reliabilities_are_rejected_because_cpp_never_sends_them(string hex)
    {
        // The C++ never writes them (ReliabilityLayer.cpp:2619-2626, "I don't write ACK_RECEIPT to the remote system",
        // line 2712). Values >= 8 cannot occur in 3 bits.
        var reader = new BitStreamReader(Hex(hex));

        Assert.False(EncapsulatedMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void Ordering_channel_of_32_or_more_is_rejected()
    {
        // ReliabilityLayer.cpp:2763
        var reader = new BitStreamReader(Hex("60 0008 010000 020000 20 AA"));

        Assert.False(EncapsulatedMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void Ordering_channel_31_is_accepted()
    {
        var reader = new BitStreamReader(Hex("60 0008 010000 020000 1F AA"));

        Assert.True(EncapsulatedMessage.TryRead(ref reader, out var message));
        Assert.Equal(31, message.OrderingChannel);
    }

    [Theory]
    [InlineData("50 0008 010000 00000000 0000 00000000 AA")] // split count 0
    [InlineData("50 0008 010000 00000002 0000 00000002 AA")] // split index == count
    [InlineData("50 0008 010000 00000002 0000 FFFFFFFF AA")] // split index > count
    public void Split_index_must_be_below_a_nonzero_split_count(string hex)
    {
        // ReliabilityLayer.cpp:2764: splitPacketIndex >= splitPacketCount is garbage, which also rejects count 0.
        var reader = new BitStreamReader(Hex(hex));

        Assert.False(EncapsulatedMessage.TryRead(ref reader, out _));
    }

    [Fact]
    public void Header_length_matches_cpp_GetMessageHeaderLengthBits()
    {
        // ReliabilityLayer.cpp:2547-2605
        Assert.Equal(24, EncapsulatedMessage.GetHeaderLengthBits(Reliability.Unreliable, isSplit: false));
        Assert.Equal(48, EncapsulatedMessage.GetHeaderLengthBits(Reliability.Reliable, isSplit: false));
        Assert.Equal(80, EncapsulatedMessage.GetHeaderLengthBits(Reliability.ReliableOrdered, isSplit: false));
        Assert.Equal(80, EncapsulatedMessage.GetHeaderLengthBits(Reliability.ReliableOrderedWithAckReceipt, isSplit: false));
        Assert.Equal(80, EncapsulatedMessage.GetHeaderLengthBits(Reliability.UnreliableSequenced, isSplit: false));
        Assert.Equal(184, EncapsulatedMessage.GetHeaderLengthBits(Reliability.ReliableSequenced, isSplit: true));
        Assert.Equal(184, EncapsulatedMessage.MaxHeaderLengthBits);
    }

    [Fact]
    public void Invalid_messages_are_programming_errors()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage(Reliability.Unreliable, ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage(Reliability.Unreliable, Aa, payloadBitLength: 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage(Reliability.Unreliable, Aa, payloadBitLength: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage(Reliability.Unreliable, new byte[8192]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage(Reliability.ReliableOrdered, Aa, orderingChannel: 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncapsulatedMessage((Reliability)8, Aa));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitHeader(0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SplitHeader(2, 0, 2));
    }

    [Fact]
    public void Reader_yields_every_message_in_a_datagram_with_payloads_sliced_from_the_input()
    {
        var first = new EncapsulatedMessage(Reliability.Reliable, Hex("0102"), reliableMessageNumber: new UInt24(1));
        var second = new EncapsulatedMessage(Reliability.Unreliable, Hex("03"));
        var third = new EncapsulatedMessage(Reliability.ReliableOrdered, Hex("040506"), reliableMessageNumber: new UInt24(2), orderingIndex: new UInt24(0), orderingChannel: 1);
        var datagram = Write((ref BitStreamWriter w) =>
        {
            DatagramHeader.ForData(new UInt24(42), needsBAndAs: true).Write(ref w);
            first.Write(ref w);
            second.Write(ref w);
            third.Write(ref w);
        });

        Assert.True(EncapsulatedMessageReader.TryOpenDatagram(datagram, out var header, out var reader));
        Assert.Equal(new UInt24(42), header.DatagramNumber);

        var messages = new List<EncapsulatedMessage>();
        while (reader.TryReadNext(out var message))
            messages.Add(message);

        Assert.False(reader.IsMalformed);
        Assert.Equal([first, second, third], messages);
        Assert.True(MemoryMarshal.TryGetArray(messages[2].Payload, out var segment));
        Assert.Same(datagram, segment.Array);
        Assert.Equal(datagram.Length - 3, segment.Offset);
    }

    [Fact]
    public void Fewer_than_32_trailing_bits_end_the_datagram_without_error()
    {
        // CreateInternalPacketFromBitStream stops when fewer than sizeof(reliableMessageNumber)*8 = 32 bits are left
        // (ReliabilityLayer.cpp:2690-2691; sizeof(uint24_t) is 4).
        var body = Hex("00 0008 AA 000000");
        var reader = new EncapsulatedMessageReader(body);

        Assert.True(reader.TryReadNext(out _));
        Assert.False(reader.TryReadNext(out _));
        Assert.False(reader.IsMalformed);
    }

    [Fact]
    public void Malformed_message_stops_the_reader_after_the_good_ones()
    {
        // The C++ processes each message as it is parsed and stops at the first one that fails
        // (ReliabilityLayer.cpp:888-895, 1493), so earlier messages are still delivered.
        var body = Hex("00 0008 AA 00 0000 BB");
        var reader = new EncapsulatedMessageReader(body);

        Assert.True(reader.TryReadNext(out var good));
        Assert.Equal(Aa, good.Payload.ToArray());
        Assert.False(reader.TryReadNext(out _));
        Assert.True(reader.IsMalformed);
        Assert.False(reader.TryReadNext(out _));
    }

    [Fact]
    public void ReadAll_collects_messages_and_reports_malformed_bodies()
    {
        var messages = new List<EncapsulatedMessage>();

        Assert.True(EncapsulatedMessageReader.TryReadAll(Hex("00 0008 AA 00 0008 BB"), messages));
        Assert.Equal(2, messages.Count);

        messages.Clear();
        Assert.False(EncapsulatedMessageReader.TryReadAll(Hex("00 0008 AA 00 0010 BB"), messages));
        Assert.Single(messages);
    }

    [Theory]
    [InlineData("C0 0001 01 050000")] // ACK
    [InlineData("A0 0001 01 050000")] // NAK
    [InlineData("04 000000")] // isValid clear
    [InlineData("84 0000")] // truncated header
    public void Only_data_datagrams_open_for_reading_messages(string hex)
    {
        Assert.False(EncapsulatedMessageReader.TryOpenDatagram(Hex(hex), out _, out _));
    }
}
