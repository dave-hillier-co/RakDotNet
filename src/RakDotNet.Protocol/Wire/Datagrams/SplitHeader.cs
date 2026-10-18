namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>
/// The split fields of an encapsulated message: splitPacketCount (u32), splitPacketId (u16) and splitPacketIndex (u32),
/// each aligned and big-endian (ReliabilityLayer.cpp:2660-2671; types from InternalPacket.h:32-33).
/// </summary>
public readonly record struct SplitHeader
{
    public SplitHeader(uint count, ushort id, uint index)
    {
        ArgumentOutOfRangeException.ThrowIfZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        Count = count;
        Id = id;
        Index = index;
    }

    /// <summary>Number of parts, at least 1.</summary>
    public uint Count { get; }

    public ushort Id { get; }

    /// <summary>This part's index, below <see cref="Count"/>.</summary>
    public uint Index { get; }
}
