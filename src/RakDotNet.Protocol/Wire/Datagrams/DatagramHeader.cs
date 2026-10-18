namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>What a connected datagram carries.</summary>
public enum DatagramKind : byte
{
    /// <summary>Encapsulated messages, numbered by <see cref="DatagramHeader.DatagramNumber"/>.</summary>
    Data = 0,

    /// <summary>An acknowledgement range list.</summary>
    Ack = 1,

    /// <summary>A negative acknowledgement range list.</summary>
    Nak = 2,
}

/// <summary>
/// The header at the start of every connected datagram: DatagramHeaderFormat (ReliabilityLayer.cpp:110-227).
/// </summary>
/// <remarks>
/// <para>
/// The default C++ build uses sliding-window congestion control (RakNetDefines.h:121-122), so
/// INCLUDE_TIMESTAMP_WITH_DATAGRAMS is 0 (ReliabilityLayer.h:43-49) and no source time is written. Only that build is
/// supported.
/// </para>
/// <para>
/// The isValid bit is always written set and distinguishes reliability-layer datagrams from other data
/// (ReliabilityLayer.cpp:126, 689). <see cref="TryRead"/> returns false when it is clear.
/// </para>
/// <para>
/// Layout: valid, isACK. For an ACK: hasBAndAS, align, then the AS float if hasBAndAS. For a NAK: isNAK, with no
/// alignment (the range list aligns itself). For data: isNAK=0, isPacketPair, isContinuousSend, needsBAndAs, align,
/// datagram number as a little-endian uint24.
/// </para>
/// <para>
/// Sliding-window congestion control never sets hasBAndAS (CCRakNetSlidingWindow.cpp:264-271), but the field is
/// decoded because Deserialize reads it.
/// </para>
/// </remarks>
public readonly record struct DatagramHeader
{
    private DatagramHeader(
        DatagramKind kind,
        UInt24 datagramNumber,
        bool isPacketPair,
        bool isContinuousSend,
        bool needsBAndAs,
        bool hasBAndAs,
        float arrivalRate
    )
    {
        Kind = kind;
        DatagramNumber = datagramNumber;
        IsPacketPair = isPacketPair;
        IsContinuousSend = isContinuousSend;
        NeedsBAndAs = needsBAndAs;
        HasBAndAs = hasBAndAs;
        ArrivalRate = arrivalRate;
    }

    public DatagramKind Kind { get; }

    public bool IsAck => Kind == DatagramKind.Ack;

    public bool IsNak => Kind == DatagramKind.Nak;

    /// <summary>Data datagrams only.</summary>
    public UInt24 DatagramNumber { get; }

    /// <summary>Data datagrams only.</summary>
    public bool IsPacketPair { get; }

    /// <summary>Data datagrams only.</summary>
    public bool IsContinuousSend { get; }

    /// <summary>Data datagrams only. The sender is in slow start (ReliabilityLayer.cpp:1828).</summary>
    public bool NeedsBAndAs { get; }

    /// <summary>ACK datagrams only. True when <see cref="ArrivalRate"/> follows the header.</summary>
    public bool HasBAndAs { get; }

    /// <summary>ACK datagrams only: AS, the data arrival rate, present when <see cref="HasBAndAs"/>.</summary>
    public float ArrivalRate { get; }

    public static DatagramHeader ForData(
        UInt24 datagramNumber,
        bool isPacketPair = false,
        bool isContinuousSend = false,
        bool needsBAndAs = false
    ) => new(DatagramKind.Data, datagramNumber, isPacketPair, isContinuousSend, needsBAndAs, false, 0);

    public static DatagramHeader ForAck() => new(DatagramKind.Ack, default, false, false, false, false, 0);

    public static DatagramHeader ForAck(float arrivalRate) =>
        new(DatagramKind.Ack, default, false, false, false, true, arrivalRate);

    public static DatagramHeader ForNak() => new(DatagramKind.Nak, default, false, false, false, false, 0);

    /// <summary>True when the first bit of <paramref name="datagram"/> (isValid) is set.</summary>
    public static bool HasValidFlag(ReadOnlySpan<byte> datagram)
    {
        var reader = new BitStreamReader(datagram);
        return reader.TryReadBit(out var isValid) && isValid;
    }

    /// <summary>DatagramHeaderFormat::Serialize, ReliabilityLayer.cpp:143-183.</summary>
    public void Write(ref BitStreamWriter writer)
    {
        writer.WriteBit(true); // isValid
        switch (Kind)
        {
            case DatagramKind.Ack:
                writer.WriteBit(true);
                writer.WriteBit(HasBAndAs);
                writer.AlignToByte();
                if (HasBAndAs)
                    writer.WriteSingle(ArrivalRate);
                break;
            case DatagramKind.Nak:
                writer.WriteBit(false);
                writer.WriteBit(true);
                break;
            default:
                writer.WriteBit(false);
                writer.WriteBit(false);
                writer.WriteBit(IsPacketPair);
                writer.WriteBit(IsContinuousSend);
                writer.WriteBit(NeedsBAndAs);
                writer.AlignToByte();
                writer.WriteUInt24(DatagramNumber);
                break;
        }
    }

    /// <summary>DatagramHeaderFormat::Deserialize, ReliabilityLayer.cpp:184-226, with truncation checks.</summary>
    public static bool TryRead(ref BitStreamReader reader, out DatagramHeader value)
    {
        value = default;
        if (!reader.TryReadBit(out var isValid) || !isValid || !reader.TryReadBit(out var isAck))
            return false;

        if (isAck)
        {
            if (!reader.TryReadBit(out var hasBAndAs))
                return false;
            reader.AlignToByte();
            if (!hasBAndAs)
            {
                value = ForAck();
                return true;
            }
            if (!reader.TryReadSingle(out var arrivalRate))
                return false;
            value = ForAck(arrivalRate);
            return true;
        }

        if (!reader.TryReadBit(out var isNak))
            return false;
        if (isNak)
        {
            value = ForNak();
            return true;
        }

        if (
            !reader.TryReadBit(out var isPacketPair)
            || !reader.TryReadBit(out var isContinuousSend)
            || !reader.TryReadBit(out var needsBAndAs)
        )
            return false;
        reader.AlignToByte();
        if (!reader.TryReadUInt24(out var datagramNumber))
            return false;
        value = ForData(datagramNumber, isPacketPair, isContinuousSend, needsBAndAs);
        return true;
    }
}
