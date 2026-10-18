using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>
/// One message inside a data datagram: the on-wire form of InternalPacket, written by
/// ReliabilityLayer::WriteToBitStreamFromInternalPacket (ReliabilityLayer.cpp:2610-2677) and read by
/// CreateInternalPacketFromBitStream (ReliabilityLayer.cpp:2682-2801).
/// </summary>
/// <remarks>
/// <para>
/// Layout: align, 3-bit reliability, 1-bit hasSplitPacket, align, u16 payload length in bits (big-endian), uint24
/// reliable message number for reliable types, align, uint24 sequencing index for sequenced types, uint24 ordering index
/// and one ordering channel byte for ordered and sequenced types, the <see cref="SplitHeader"/> when split, then the
/// payload bytes aligned.
/// </para>
/// <para>
/// Fields a reliability does not carry are stored as zero. Ack-receipt reliabilities are accepted for writing and are
/// written as their base type (<see cref="ReliabilityExtensions.ToWireReliability"/>); decoding rejects them.
/// </para>
/// </remarks>
public sealed record EncapsulatedMessage
{
    /// <summary>
    /// GetMaxMessageHeaderLengthBits (ReliabilityLayer.cpp:2547-2553): a split RELIABLE_SEQUENCED message.
    /// </summary>
    public static readonly int MaxHeaderLengthBits = GetHeaderLengthBits(Reliability.ReliableSequenced, isSplit: true);

    // CreateInternalPacketFromBitStream needs sizeof(reliableMessageNumber)*8 unread bits to try a message
    // (ReliabilityLayer.cpp:2690); sizeof(uint24_t) is 4 in the C++.
    internal const int MinimumBitsToAttempt = 32;

    private const int NumberOfOrderedStreams = 32; // NUMBER_OF_ORDERED_STREAMS, ReliabilityLayer.h:52

    public EncapsulatedMessage(
        Reliability reliability,
        ReadOnlyMemory<byte> payload,
        int payloadBitLength = -1,
        UInt24 reliableMessageNumber = default,
        UInt24 sequencingIndex = default,
        UInt24 orderingIndex = default,
        byte orderingChannel = 0,
        SplitHeader? split = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            (int)reliability,
            ReliabilityExtensions.NumberOfReliabilities,
            nameof(reliability)
        );
        ArgumentOutOfRangeException.ThrowIfZero(payload.Length, nameof(payload));
        if (payloadBitLength == -1)
            payloadBitLength = payload.Length * 8;
        ArgumentOutOfRangeException.ThrowIfLessThan(payloadBitLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadBitLength, ushort.MaxValue);
        if ((payloadBitLength + 7) / 8 != payload.Length)
            throw new ArgumentOutOfRangeException(
                nameof(payloadBitLength),
                payloadBitLength,
                $"A payload of {payload.Length} bytes holds {payload.Length * 8 - 7} to {payload.Length * 8} bits."
            );
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(orderingChannel, NumberOfOrderedStreams);

        var wire = reliability.ToWireReliability();
        Reliability = reliability;
        Payload = payload;
        PayloadBitLength = payloadBitLength;
        ReliableMessageNumber = wire.IsReliable() ? reliableMessageNumber : default;
        SequencingIndex = wire.IsSequenced() ? sequencingIndex : default;
        OrderingIndex = wire.HasOrderingChannel() ? orderingIndex : default;
        OrderingChannel = wire.HasOrderingChannel() ? orderingChannel : (byte)0;
        Split = split;
    }

    public Reliability Reliability { get; }

    /// <summary>The payload, <c>ceil(PayloadBitLength / 8)</c> bytes. Decoded messages slice the input.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The payload length in bits, as on the wire (1 to 65535).</summary>
    public int PayloadBitLength { get; }

    public UInt24 ReliableMessageNumber { get; }

    public UInt24 SequencingIndex { get; }

    public UInt24 OrderingIndex { get; }

    /// <summary>0 to 31.</summary>
    public byte OrderingChannel { get; }

    public SplitHeader? Split { get; }

    /// <summary>The header size in bits. Matches GetMessageHeaderLengthBits (ReliabilityLayer.cpp:2555-2605).</summary>
    public static int GetHeaderLengthBits(Reliability reliability, bool isSplit)
    {
        var bits = 8 + 16;
        if (reliability.IsReliable())
            bits += 24;
        if (reliability.IsSequenced())
            bits += 24;
        if (reliability.HasOrderingChannel())
            bits += 24 + 8;
        if (isSplit)
            bits += 32 + 16 + 32;
        return bits;
    }

    /// <summary>WriteToBitStreamFromInternalPacket, ReliabilityLayer.cpp:2610-2677.</summary>
    public void Write(ref BitStreamWriter writer)
    {
        var wire = Reliability.ToWireReliability(); // ReliabilityLayer.cpp:2619-2626
        writer.AlignToByte(); // ReliabilityLayer.cpp:2618
        writer.WriteBits((byte)wire, 3);
        writer.WriteBit(Split is not null);
        writer.AlignToByte();
        writer.WriteUInt16((ushort)PayloadBitLength);
        if (wire.IsReliable())
            writer.WriteUInt24(ReliableMessageNumber);
        writer.AlignToByte(); // ReliabilityLayer.cpp:2641
        if (wire.IsSequenced())
            writer.WriteUInt24(SequencingIndex);
        if (wire.HasOrderingChannel())
        {
            writer.WriteUInt24(OrderingIndex);
            writer.WriteByte(OrderingChannel);
        }
        if (Split is { } split)
        {
            writer.WriteUInt32(split.Count);
            writer.WriteUInt16(split.Id);
            writer.WriteUInt32(split.Index);
        }
        writer.WriteAlignedBytes(Payload.Span); // ReliabilityLayer.cpp:2674
    }

    /// <summary>
    /// Reads one message, copying the payload. Use <see cref="EncapsulatedMessageReader"/> to slice payloads from a
    /// datagram instead.
    /// </summary>
    public static bool TryRead(ref BitStreamReader reader, [NotNullWhen(true)] out EncapsulatedMessage? value)
    {
        value = null;
        if (!TryReadHeader(ref reader, out var header))
            return false;
        if (!reader.TryReadAlignedBytes(header.PayloadByteLength, out var payload))
            return false;
        value = header.ToMessage(payload.ToArray());
        return true;
    }

    /// <summary>
    /// Reads one message whose bytes start at the start of <paramref name="buffer"/>, slicing the payload from it.
    /// </summary>
    internal static bool TryRead(
        ReadOnlyMemory<byte> buffer,
        [NotNullWhen(true)] out EncapsulatedMessage? value,
        out int bytesRead
    )
    {
        value = null;
        bytesRead = 0;
        var reader = new BitStreamReader(buffer.Span);
        if (!TryReadHeader(ref reader, out var header))
            return false;
        var payloadStart = (reader.BitPosition + 7) >> 3;
        if (!reader.TryReadAlignedBytes(header.PayloadByteLength, out _))
            return false;
        value = header.ToMessage(buffer.Slice(payloadStart, header.PayloadByteLength));
        bytesRead = payloadStart + header.PayloadByteLength;
        return true;
    }

    private static bool TryReadHeader(ref BitStreamReader reader, out Header header)
    {
        header = default;
        reader.AlignToByte(); // ReliabilityLayer.cpp:2703
        if (!reader.TryReadBits(3, out var rawReliability) || !reader.TryReadBit(out var hasSplit))
            return false;
        var reliability = (Reliability)rawReliability;
        // Values >= NUMBER_OF_RELIABILITIES are rejected by the C++ (ReliabilityLayer.cpp:2762); ack-receipt values
        // are never sent by it (line 2712) and are rejected here as well.
        if (!reliability.IsValidOnWire())
            return false;
        reader.AlignToByte();
        if (!reader.TryReadUInt16(out var bitLength) || bitLength == 0) // ReliabilityLayer.cpp:2708, 2761
            return false;

        UInt24 messageNumber = default, sequencingIndex = default, orderingIndex = default;
        byte channel = 0;
        if (reliability.IsReliable() && !reader.TryReadUInt24(out messageNumber))
            return false;
        reader.AlignToByte();
        if (reliability.IsSequenced() && !reader.TryReadUInt24(out sequencingIndex))
            return false;
        if (reliability.HasOrderingChannel())
        {
            if (!reader.TryReadUInt24(out orderingIndex) || !reader.TryReadByte(out channel))
                return false;
            if (channel >= NumberOfOrderedStreams) // ReliabilityLayer.cpp:2763
                return false;
        }

        SplitHeader? split = null;
        if (hasSplit)
        {
            if (
                !reader.TryReadUInt32(out var count)
                || !reader.TryReadUInt16(out var id)
                || !reader.TryReadUInt32(out var index)
            )
                return false;
            // ReliabilityLayer.cpp:2764 rejects splitPacketIndex >= splitPacketCount, which also excludes a count of 0.
            if (index >= count)
                return false;
            split = new SplitHeader(count, id, index);
        }

        header = new Header(reliability, bitLength, messageNumber, sequencingIndex, orderingIndex, channel, split);
        return true;
    }

    public bool Equals(EncapsulatedMessage? other) =>
        other is not null
        && Reliability == other.Reliability
        && PayloadBitLength == other.PayloadBitLength
        && ReliableMessageNumber == other.ReliableMessageNumber
        && SequencingIndex == other.SequencingIndex
        && OrderingIndex == other.OrderingIndex
        && OrderingChannel == other.OrderingChannel
        && Split == other.Split
        && Payload.Span.SequenceEqual(other.Payload.Span);

    public override int GetHashCode() =>
        HashCode.Combine(Reliability, PayloadBitLength, ReliableMessageNumber, SequencingIndex, OrderingIndex, OrderingChannel, Split);

    private readonly record struct Header(
        Reliability Reliability,
        ushort BitLength,
        UInt24 ReliableMessageNumber,
        UInt24 SequencingIndex,
        UInt24 OrderingIndex,
        byte OrderingChannel,
        SplitHeader? Split
    )
    {
        public int PayloadByteLength => (BitLength + 7) >> 3;

        public EncapsulatedMessage ToMessage(ReadOnlyMemory<byte> payload) =>
            new(Reliability, payload, BitLength, ReliableMessageNumber, SequencingIndex, OrderingIndex, OrderingChannel, Split);
    }
}
