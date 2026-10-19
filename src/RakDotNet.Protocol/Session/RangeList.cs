using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// Datagram numbers waiting to be acknowledged or NAKed, condensed into sorted ranges: the acknowlegements and NAKs
/// members of ReliabilityLayer, each a DataStructures::RangeList&lt;DatagramSequenceNumberType&gt; (DS_RangeList.h).
/// </summary>
/// <remarks>
/// Numbers are ordered by numeric value, as uint24_t compares (RakNetTypes.h:483-484), so 0xFFFFFF and 0 are never
/// joined. Memory is one entry per range, not per number.
/// </remarks>
internal sealed class RangeList
{
    private readonly List<AckRange> _ranges = [];

    public int Count => _ranges.Count;

    public IReadOnlyList<AckRange> Ranges => _ranges;

    /// <summary>
    /// RangeList::Insert (DS_RangeList.h:159-218), ported branch for branch. The lookup finds the first range whose
    /// minimum is above the number (RangeNodeComp compares against minIndex only, DS_RangeList.h:39-46), and only that
    /// range and, when joining, the one before it are examined. A number that extends or repeats the preceding range
    /// therefore gets a range of its own unless it is past the last range, so the list can hold overlapping and
    /// adjacent ranges after out-of-order or duplicate arrivals, exactly as RakNet's ACK and NAK lists do.
    /// </summary>
    public void Insert(UInt24 number)
    {
        var n = number.Value;
        if (_ranges.Count == 0)
        {
            _ranges.Add(new AckRange(number, number));
            return;
        }

        var index = IndexOfFirstRangeWithMinAtOrAbove(n, out var exists);
        if (index == _ranges.Count)
        {
            var last = _ranges[index - 1];
            var next = Wrap(last.Max.Value + 1);
            if (n == next)
                _ranges[index - 1] = new AckRange(last.Min, new UInt24(next)); // maxIndex++ (DS_RangeList.h:172-173).
            else if (n > next)
                _ranges.Add(new AckRange(number, number)); // Insert at end (DS_RangeList.h:174-178).
            return;
        }

        var range = _ranges[index];
        if (!exists && n < Wrap(range.Min.Value - 1))
        {
            _ranges.Insert(index, new AckRange(number, number)); // Insert here (DS_RangeList.h:182-188).
        }
        else if (!exists && n == Wrap(range.Min.Value - 1))
        {
            // Decrease minIndex and join left (DS_RangeList.h:189-199).
            _ranges[index] = new AckRange(number, range.Max);
            if (index > 0 && Wrap(_ranges[index - 1].Max.Value + 1) == n)
            {
                _ranges[index - 1] = new AckRange(_ranges[index - 1].Min, range.Max);
                _ranges.RemoveAt(index);
            }
        }
        // Otherwise the number is the range's minimum, already present (DS_RangeList.h:200-204); the join-right branch
        // that follows cannot be reached, because every number left here is below the range's minimum.
    }

    /// <summary>
    /// RangeList::Serialize with clearSerialized (DS_RangeList.h:64-110): writes as many ranges as fit in
    /// <paramref name="maxBits"/>, removes them, and returns how many were written.
    /// </summary>
    public int Write(ref BitStreamWriter writer, int maxBits)
    {
        var list = new AckRangeList(_ranges);
        var written = list.Write(ref writer, maxBits);
        _ranges.RemoveRange(0, written);
        return written;
    }

    public void Clear() => _ranges.Clear();

    private static uint Wrap(uint value) => value & UInt24.MaxValue.Value;

    // OrderedList::GetIndexFromKey with RangeNodeComp (DS_OrderedList.h:119-170): the range whose minimum equals n, or
    // the first whose minimum is above it.
    private int IndexOfFirstRangeWithMinAtOrAbove(uint n, out bool exists)
    {
        int low = 0, high = _ranges.Count - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var min = _ranges[mid].Min.Value;
            if (n == min)
            {
                exists = true;
                return mid;
            }
            if (n < min)
                high = mid - 1;
            else
                low = mid + 1;
        }
        exists = false;
        return low;
    }
}
