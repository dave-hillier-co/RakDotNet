namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>
/// The body of an ACK or NAK datagram: DataStructures::RangeList&lt;DatagramSequenceNumberType&gt;
/// (DS_RangeList.h:48-144).
/// </summary>
/// <remarks>
/// <para>
/// Wire layout: align to a byte, u16 count big-endian, then per range an unsigned char minEqualsMax (a whole byte,
/// DS_RangeList.h:83), the uint24 min and, when minEqualsMax is zero, the uint24 max. Every field is byte-aligned.
/// </para>
/// <para>
/// Decoding keeps ranges as ranges; nothing is expanded into per-number collections. The C++ ACK handler additionally
/// rejects a whole ACK when any range's max is 0xFFFFFF (ReliabilityLayer.cpp:738), because its per-number loop could
/// not terminate; NAKs have no such check (ReliabilityLayer.cpp:820). That rule belongs to the session layer.
/// </para>
/// </remarks>
public sealed record AckRangeList
{
    /// <summary>
    /// The most ranges a datagram of <see cref="DatagramLimits.MaximumMtuSize"/> bytes can carry: one header byte, the
    /// two-byte count, then at least four bytes per range. Larger counts are rejected before any range is read.
    /// </summary>
    public const int MaxRangesPerDatagram = (DatagramLimits.MaximumMtuSize - 1 - 2) / MinRangeBytes;

    private const int MinRangeBytes = 1 + 3;

    // sizeof(uint24_t) in the C++ is 4 (RakNetTypes.h:464-466), and Serialize budgets with it (DS_RangeList.h:76-89).
    private const int CppUInt24Bits = 4 * 8;

    private readonly AckRange[] _ranges;

    public AckRangeList(IEnumerable<AckRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        _ranges = [.. ranges];
    }

    private AckRangeList(AckRange[] ranges)
    {
        _ranges = ranges;
    }

    public static AckRangeList Empty { get; } = new(Array.Empty<AckRange>());

    /// <summary>The ranges in wire order.</summary>
    public IReadOnlyList<AckRange> Ranges => _ranges;

    /// <summary>The number of datagram numbers covered, counting overlaps more than once.</summary>
    public long TotalNumbers
    {
        get
        {
            long total = 0;
            foreach (var range in _ranges)
                total += range.Count;
            return total;
        }
    }

    public bool Contains(UInt24 number)
    {
        foreach (var range in _ranges)
        {
            if (range.Contains(number))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Condenses <paramref name="numbers"/> into ranges as DS_RangeList::Insert does (DS_RangeList.h:159-218): sorted by
    /// numeric value, duplicates dropped, consecutive numbers joined. 0xFFFFFF and 0 are never joined, so numbers
    /// either side of the wrap produce two ranges, the one starting at 0 first.
    /// </summary>
    public static AckRangeList FromNumbers(IEnumerable<UInt24> numbers)
    {
        ArgumentNullException.ThrowIfNull(numbers);
        var sorted = numbers.Select(n => n.Value).ToArray();
        if (sorted.Length == 0)
            return Empty;
        Array.Sort(sorted);

        var ranges = new List<AckRange>();
        var min = sorted[0];
        var max = min;
        for (var i = 1; i < sorted.Length; i++)
        {
            var n = sorted[i];
            if (n == max)
                continue;
            if (n == max + 1)
            {
                max = n;
                continue;
            }
            ranges.Add(new AckRange(new UInt24(min), new UInt24(max)));
            min = max = n;
        }
        ranges.Add(new AckRange(new UInt24(min), new UInt24(max)));
        return new AckRangeList(ranges.ToArray());
    }

    /// <summary>The ranges after the first <paramref name="count"/>: what a budgeted write left over.</summary>
    public AckRangeList Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _ranges.Length);
        return count == 0 ? this : new AckRangeList(_ranges[count..]);
    }

    /// <summary>Writes every range.</summary>
    public void Write(ref BitStreamWriter writer) => WriteRanges(ref writer, _ranges.Length);

    /// <summary>
    /// Writes as many ranges as RangeList::Serialize would fit in <paramref name="maxBits"/> and returns how many were
    /// written; use <see cref="Skip"/> for the rest.
    /// </summary>
    /// <remarks>
    /// Matches the C++ accounting exactly (DS_RangeList.h:74-92): before each range it stops when
    /// <c>16 + bitsWritten + 2 * 32 + 1 &gt; maxBits</c>, and it adds 40 bits per single range and 72 per ranged entry,
    /// because sizeof(uint24_t) is 4. The bits already in the stream (header, alignment) are not counted. RakNet passes
    /// <see cref="DatagramLimits.MaxPayloadBits"/> of the negotiated MTU (ReliabilityLayer.cpp:1823, 3618, 3649), which
    /// leaves out the 28 byte IP/UDP header and the 9 byte header budget; at MTU 1492 that fits 289 single ranges.
    /// </remarks>
    public int Write(ref BitStreamWriter writer, int maxBits)
    {
        var count = 0;
        var bitsWritten = 0;
        foreach (var range in _ranges)
        {
            if (16 + bitsWritten + CppUInt24Bits * 2 + 1 > maxBits)
                break;
            bitsWritten += CppUInt24Bits + 8;
            if (!range.IsSingle)
                bitsWritten += CppUInt24Bits;
            count++;
        }
        WriteRanges(ref writer, count);
        return count;
    }

    private void WriteRanges(ref BitStreamWriter writer, int count)
    {
        if (count > ushort.MaxValue)
            throw new InvalidOperationException($"A range list holds at most {ushort.MaxValue} ranges, not {count}.");
        writer.AlignToByte(); // DS_RangeList.h:94
        writer.WriteUInt16((ushort)count);
        for (var i = 0; i < count; i++)
        {
            var range = _ranges[i];
            writer.WriteByte(range.IsSingle ? (byte)1 : (byte)0);
            writer.WriteUInt24(range.Min);
            if (!range.IsSingle)
                writer.WriteUInt24(range.Max);
        }
    }

    /// <summary>
    /// RangeList::Deserialize (DS_RangeList.h:114-144) with truncation checks and a cap of
    /// <see cref="MaxRangesPerDatagram"/>. Any nonzero minEqualsMax byte means a single number, as in the C++.
    /// </summary>
    public static bool TryRead(ref BitStreamReader reader, out AckRangeList value)
    {
        value = Empty;
        reader.AlignToByte(); // DS_RangeList.h:119
        if (!reader.TryReadUInt16(out var count))
            return false;
        if (count > MaxRangesPerDatagram || count * MinRangeBytes * 8 > reader.BitsRemaining)
            return false;

        var ranges = new AckRange[count];
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadByte(out var minEqualsMax) || !reader.TryReadUInt24(out var min))
                return false;
            var max = min;
            if (minEqualsMax == 0)
            {
                if (!reader.TryReadUInt24(out max) || max.Value < min.Value)
                    return false;
            }
            ranges[i] = new AckRange(min, max);
        }
        value = count == 0 ? Empty : new AckRangeList(ranges);
        return true;
    }

    public bool Equals(AckRangeList? other) =>
        other is not null && _ranges.AsSpan().SequenceEqual(other._ranges);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var range in _ranges)
            hash.Add(range);
        return hash.ToHashCode();
    }

    public override string ToString() => string.Join(' ', _ranges);
}
