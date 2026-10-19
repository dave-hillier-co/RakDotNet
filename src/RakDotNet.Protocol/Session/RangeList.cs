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

    /// <summary>RangeList::Insert (DS_RangeList.h:159-218).</summary>
    public void Insert(UInt24 number)
    {
        var n = number.Value;
        var index = IndexOfFirstRangeEndingAtOrAfter(n);

        if (index < _ranges.Count && _ranges[index].Min.Value <= n)
            return; // Already present.

        var joinsLeft = index > 0 && _ranges[index - 1].Max.Value + 1 == n;
        var joinsRight = index < _ranges.Count && n + 1 == _ranges[index].Min.Value;
        if (joinsLeft && joinsRight)
        {
            _ranges[index - 1] = new AckRange(_ranges[index - 1].Min, _ranges[index].Max);
            _ranges.RemoveAt(index);
        }
        else if (joinsLeft)
        {
            _ranges[index - 1] = new AckRange(_ranges[index - 1].Min, number);
        }
        else if (joinsRight)
        {
            _ranges[index] = new AckRange(number, _ranges[index].Max);
        }
        else
        {
            _ranges.Insert(index, new AckRange(number, number));
        }
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

    private int IndexOfFirstRangeEndingAtOrAfter(uint n)
    {
        int low = 0, high = _ranges.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (_ranges[mid].Max.Value < n)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }
}
