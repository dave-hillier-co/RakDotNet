using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Plugins;

/// <summary>A value recorded at a time in milliseconds: StatisticsHistory::TimeAndValue.</summary>
public readonly record struct TimeAndValue(ulong Time, double Value);

/// <summary>How two sets of values combine: StatisticsHistory::SHDataCategory.</summary>
public enum StatisticsDataCategory : byte
{
    /// <summary>DC_DISCRETE: the sets are interleaved in time order, so values at one time appear twice.</summary>
    Discrete,

    /// <summary>
    /// DC_CONTINUOUS: values are added at corresponding times, a missing value being extrapolated from the other set's
    /// last value and slope. The long-term figures of the result are unknown.
    /// </summary>
    Continuous,
}

/// <summary>
/// The values recorded for one key of one object, with running figures: StatisticsHistory::TimeAndValueQueue.
/// </summary>
/// <remarks>
/// Recent figures cover the values still held, which <see cref="CullExpiredValues"/> drops once older than
/// <see cref="TimeToTrackValues"/>; long-term figures cover every value ever added. Times are unsigned, so a value
/// recorded after the time passed to <see cref="CullExpiredValues"/> counts as expired, as in RakNet.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "RakNet's name for the type.")]
public sealed class TimeAndValueQueue
{
    private readonly ValueRing _values = new();

    public TimeAndValueQueue()
    {
        Clear();
    }

    /// <summary>The values held, oldest first.</summary>
    public IReadOnlyList<TimeAndValue> Values => _values;

    /// <summary>How long values are held, in milliseconds.</summary>
    public ulong TimeToTrackValues { get; set; }

    public string Key { get; set; } = "";

    public double RecentSum { get; private set; }

    public double RecentSumOfSquares { get; private set; }

    public double LongTermSum { get; private set; }

    public double LongTermCount { get; private set; }

    public double LongTermLowest { get; private set; }

    public double LongTermHighest { get; private set; }

    /// <summary>GetRecentAverage; 0 with no values.</summary>
    public double RecentAverage => _values.Count > 0 ? RecentSum / _values.Count : 0;

    /// <summary>GetRecentLowest; <see cref="double.MaxValue"/> with no values.</summary>
    public double RecentLowest
    {
        get
        {
            var lowest = double.MaxValue;
            for (var i = 0; i < _values.Count; i++)
            {
                if (_values[i].Value < lowest)
                    lowest = _values[i].Value;
            }
            return lowest;
        }
    }

    /// <summary>GetRecentHighest; -<see cref="double.MaxValue"/> with no values.</summary>
    public double RecentHighest
    {
        get
        {
            var highest = -double.MaxValue;
            for (var i = 0; i < _values.Count; i++)
            {
                if (_values[i].Value > highest)
                    highest = _values[i].Value;
            }
            return highest;
        }
    }

    /// <summary>
    /// GetRecentStandardDeviation (StatisticsHistory.cpp:297-305), which returns the mean of the squares less the square
    /// of the mean, the variance, without the square root its name implies.
    /// </summary>
    public double RecentStandardDeviation
    {
        get
        {
            if (_values.Count == 0)
                return 0;
            var mean = RecentAverage;
            return RecentSumOfSquares / _values.Count - mean * mean;
        }
    }

    /// <summary>GetLongTermAverage; 0 before any value.</summary>
    public double LongTermAverage => LongTermCount == 0 ? 0 : LongTermSum / LongTermCount;

    /// <summary>GetTimeRange: from the first value held to the last; 0 with fewer than two.</summary>
    public ulong TimeRange => _values.Count < 2 ? 0 : _values[^1].Time - _values[0].Time;

    /// <summary>GetSumSinceTime: the sum of the values held from <paramref name="time"/> on.</summary>
    public double GetSumSinceTime(ulong time)
    {
        double sum = 0;
        for (var i = _values.Count; i > 0; --i)
        {
            if (_values[i - 1].Time >= time)
                sum += _values[i - 1].Value;
        }
        return sum;
    }

    /// <summary>Clear: removes every value and resets the figures.</summary>
    public void Clear()
    {
        RecentSum = 0;
        RecentSumOfSquares = 0;
        LongTermSum = 0;
        LongTermCount = 0;
        LongTermLowest = double.MaxValue;
        LongTermHighest = -double.MaxValue;
        _values.Clear();
    }

    /// <summary>operator=: makes this queue a copy of <paramref name="source"/>.</summary>
    public void CopyFrom(TimeAndValueQueue source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (ReferenceEquals(source, this))
            return;
        _values.Clear();
        for (var i = 0; i < source._values.Count; i++)
            _values.PushBack(source._values[i]);
        TimeToTrackValues = source.TimeToTrackValues;
        Key = source.Key;
        RecentSum = source.RecentSum;
        RecentSumOfSquares = source.RecentSumOfSquares;
        LongTermSum = source.LongTermSum;
        LongTermCount = source.LongTermCount;
        LongTermLowest = source.LongTermLowest;
        LongTermHighest = source.LongTermHighest;
    }

    /// <summary>CullExpiredValues: drops values more than <see cref="TimeToTrackValues"/> older than <paramref name="now"/>.</summary>
    public void CullExpiredValues(ulong now)
    {
        while (_values.Count > 0)
        {
            var oldest = _values[0];
            if (now - oldest.Time <= TimeToTrackValues)
                break;
            RecentSum -= oldest.Value;
            RecentSumOfSquares -= oldest.Value * oldest.Value;
            _values.PopFront();
        }
    }

    /// <summary>
    /// Interpolate: the value at <paramref name="time"/> on the line through two samples, or their mean when they share
    /// a time.
    /// </summary>
    public static double Interpolate(TimeAndValue first, TimeAndValue second, ulong time)
    {
        if (second.Time == first.Time)
            return (first.Value + second.Value) / 2;
        var slope = (second.Value - first.Value) / ((double)second.Time - first.Time);
        return first.Value + slope * ((double)time - first.Time);
    }

    /// <summary>
    /// MergeSets (StatisticsHistory.cpp:511-654): combines two sets into <paramref name="output"/>, which may be either
    /// input. Discrete with discrete interleaves the values and adds the figures; any other pairing adds values at
    /// corresponding times, extrapolating from the other set's last value along its slope where a continuous set has one.
    /// </summary>
    /// <remarks>
    /// Where a discrete value of <paramref name="lhs"/> comes first, RakNet reads <paramref name="lhs"/> at the
    /// <paramref name="rhs"/> index (lines 545-555), which reads the wrong value or past the end; the value at the
    /// <paramref name="lhs"/> index is used here.
    /// </remarks>
    public static void MergeSets(TimeAndValueQueue lhs, StatisticsDataCategory lhsDataCategory, TimeAndValueQueue rhs,
                                 StatisticsDataCategory rhsDataCategory, TimeAndValueQueue output)
    {
        ArgumentNullException.ThrowIfNull(lhs);
        ArgumentNullException.ThrowIfNull(rhs);
        ArgumentNullException.ThrowIfNull(output);
        if (!ReferenceEquals(lhs, output))
        {
            output.Key = lhs.Key;
            output.TimeToTrackValues = lhs.TimeToTrackValues;
        }
        else
        {
            output.Key = rhs.Key;
            output.TimeToTrackValues = rhs.TimeToTrackValues;
        }

        var lhsValues = lhs._values;
        var rhsValues = rhs._values;
        var lhsIndex = 0;
        var rhsIndex = 0;
        // A separate list in case an input is the output.
        var merged = new List<TimeAndValue>(lhsValues.Count + rhsValues.Count);
        if (lhsDataCategory == StatisticsDataCategory.Discrete && rhsDataCategory == StatisticsDataCategory.Discrete)
        {
            while (rhsIndex < rhsValues.Count && lhsIndex < lhsValues.Count)
            {
                if (rhsValues[rhsIndex].Time < lhsValues[lhsIndex].Time)
                {
                    merged.Add(rhsValues[rhsIndex++]);
                }
                else if (rhsValues[rhsIndex].Time > lhsValues[lhsIndex].Time)
                {
                    merged.Add(lhsValues[lhsIndex++]);
                }
                else
                {
                    merged.Add(rhsValues[rhsIndex++]);
                    merged.Add(lhsValues[lhsIndex++]);
                }
            }
            while (rhsIndex < rhsValues.Count)
                merged.Add(rhsValues[rhsIndex++]);
            while (lhsIndex < lhsValues.Count)
                merged.Add(lhsValues[lhsIndex++]);

            var recentSum = lhs.RecentSum + rhs.RecentSum;
            var recentSumOfSquares = lhs.RecentSumOfSquares + rhs.RecentSumOfSquares;
            var longTermSum = lhs.LongTermSum + rhs.LongTermSum;
            var longTermCount = lhs.LongTermCount + rhs.LongTermCount;
            var longTermLowest = lhs.LongTermLowest < rhs.LongTermLowest ? lhs.LongTermLowest : rhs.LongTermLowest;
            var longTermHighest = lhs.LongTermHighest > rhs.LongTermHighest ? lhs.LongTermHighest : rhs.LongTermHighest;
            output.RecentSum = recentSum;
            output.RecentSumOfSquares = recentSumOfSquares;
            output.LongTermSum = longTermSum;
            output.LongTermCount = longTermCount;
            output.LongTermLowest = longTermLowest;
            output.LongTermHighest = longTermHighest;
        }
        else
        {
            var lastLhs = new TimeAndValue(0, 0);
            var lastRhs = new TimeAndValue(0, 0);
            double lastSlopeLhs = 0;
            double lastSlopeRhs = 0;
            while (rhsIndex < rhsValues.Count && lhsIndex < lhsValues.Count)
            {
                TimeAndValue next;
                if (rhsValues[rhsIndex].Time < lhsValues[lhsIndex].Time)
                {
                    next = Extrapolated(rhsValues[rhsIndex], lastLhs, lastSlopeLhs);
                    lastRhs = rhsValues[rhsIndex];
                    lastSlopeRhs = SlopeAt(rhsValues, rhsIndex, rhsDataCategory, lastSlopeRhs);
                    rhsIndex++;
                }
                else if (lhsValues[lhsIndex].Time < rhsValues[rhsIndex].Time)
                {
                    next = Extrapolated(lhsValues[lhsIndex], lastRhs, lastSlopeRhs);
                    lastLhs = lhsValues[lhsIndex];
                    lastSlopeLhs = SlopeAt(lhsValues, lhsIndex, lhsDataCategory, lastSlopeLhs);
                    lhsIndex++;
                }
                else
                {
                    next = new TimeAndValue(lhsValues[lhsIndex].Time, lhsValues[lhsIndex].Value + rhsValues[rhsIndex].Value);
                    lastRhs = rhsValues[rhsIndex];
                    lastLhs = lhsValues[lhsIndex];
                    lastSlopeRhs = SlopeAt(rhsValues, rhsIndex, rhsDataCategory, lastSlopeRhs);
                    lastSlopeLhs = SlopeAt(lhsValues, lhsIndex, lhsDataCategory, lastSlopeLhs);
                    lhsIndex++;
                    rhsIndex++;
                }
                merged.Add(next);
            }
            while (rhsIndex < rhsValues.Count)
                merged.Add(Extrapolated(rhsValues[rhsIndex++], lastLhs, lastSlopeLhs));
            while (lhsIndex < lhsValues.Count)
                merged.Add(Extrapolated(lhsValues[lhsIndex++], lastRhs, lastSlopeRhs));

            output.RecentSum = 0;
            output.RecentSumOfSquares = 0;
            foreach (var value in merged)
            {
                output.RecentSum += value.Value;
                output.RecentSumOfSquares += value.Value * value.Value;
            }
        }

        output._values.Clear();
        foreach (var value in merged)
            output._values.PushBack(value);
    }

    /// <summary>
    /// ResizeSampleSet (StatisticsHistory.cpp:655-775): about <paramref name="approximateSamples"/> samples into
    /// <paramref name="histogram"/>, each at the middle of an equal interval of the time range, summing the values in it
    /// (discrete) or averaging them with a value interpolated at the interval's end (continuous). A nonzero
    /// <paramref name="timeClipStart"/> drops the samples before that long after the first, and
    /// <paramref name="timeClipEnd"/> those after that long before the last, interpolating a sample at the clip.
    /// </summary>
    public void ResizeSampleSet(int approximateSamples, List<TimeAndValue> histogram, StatisticsDataCategory dataCategory,
                                ulong timeClipStart = 0, ulong timeClipEnd = 0)
    {
        ArgumentNullException.ThrowIfNull(histogram);
        ArgumentOutOfRangeException.ThrowIfNegative(approximateSamples);
        histogram.Clear();
        if (approximateSamples == 0)
            return;
        var timeRange = TimeRange;
        if (timeRange == 0)
            return;
        if (approximateSamples == 1)
        {
            histogram.Add(new TimeAndValue(timeRange, RecentSum));
            return;
        }

        var interval = timeRange / (ulong)approximateSamples;
        if (interval == 0)
            interval = 1;
        var endTime = _values[^1].Time;
        var dataIndex = 0;
        double numSamples = 0;
        double sum = 0;
        var sampleTime = _values[0].Time + interval / 2;
        var timeBoundary = _values[0].Time + interval;
        while (timeBoundary <= endTime)
        {
            while (dataIndex < _values.Count && _values[dataIndex].Time <= timeBoundary)
            {
                sum += _values[dataIndex].Value;
                dataIndex++;
                numSamples++;
            }
            if (dataCategory == StatisticsDataCategory.Continuous)
            {
                if (dataIndex > 0 && dataIndex < _values.Count && _values[dataIndex - 1].Time < timeBoundary
                    && _values[dataIndex].Time > timeBoundary)
                {
                    sum += Interpolate(_values[dataIndex - 1], _values[dataIndex], timeBoundary);
                    numSamples++;
                }
                if (numSamples > 1)
                    sum /= numSamples;
            }
            histogram.Add(new TimeAndValue(sampleTime, sum));
            sampleTime = timeBoundary + interval / 2;
            timeBoundary += interval;
            sum = 0;
            numSamples = 0;
        }

        if (timeClipStart != 0 && histogram.Count >= 1)
        {
            timeClipStart = histogram[0].Time + timeClipStart;
            if (histogram[^1].Time < timeClipStart)
            {
                histogram.Clear();
            }
            else if (histogram.Count >= 2 && histogram[0].Time < timeClipStart)
            {
                do
                {
                    var popped = histogram[0];
                    histogram.RemoveAt(0);
                    if (histogram[0].Time == timeClipStart)
                        break;
                    if (histogram[0].Time > timeClipStart)
                    {
                        histogram.Insert(0, new TimeAndValue(timeClipStart, Interpolate(popped, histogram[0], timeClipStart)));
                        break;
                    }
                }
                while (histogram.Count >= 2);
            }
        }

        if (timeClipEnd != 0 && histogram.Count >= 1)
        {
            timeClipEnd = histogram[^1].Time - timeClipEnd;
            if (histogram[0].Time > timeClipEnd)
            {
                histogram.Clear();
            }
            else if (histogram.Count >= 2 && histogram[^1].Time > timeClipEnd)
            {
                do
                {
                    var popped = histogram[^1];
                    histogram.RemoveAt(histogram.Count - 1);
                    if (histogram[^1].Time == timeClipEnd)
                        break;
                    if (histogram[^1].Time < timeClipEnd)
                    {
                        histogram.Add(new TimeAndValue(timeClipEnd, Interpolate(popped, histogram[^1], timeClipEnd)));
                        break;
                    }
                }
                while (histogram.Count >= 2);
            }
        }
    }

    // StatisticsHistory::AddValueByIndex (StatisticsHistory.cpp:289-329) once the queue is found.
    internal void Add(double value, ulong time, bool combineEqualTimes)
    {
        TimeAndValue entry;
        if (combineEqualTimes && _values.Count > 0 && _values[^1].Time == time)
        {
            entry = _values.PopBack();
            RecentSum -= entry.Value;
            RecentSumOfSquares -= entry.Value * entry.Value;
            LongTermSum -= entry.Value;
            LongTermCount--;
        }
        else
        {
            entry = new TimeAndValue(time, 0);
        }
        entry = entry with { Value = entry.Value + value };
        _values.PushBack(entry);
        RecentSum += entry.Value;
        RecentSumOfSquares += entry.Value * entry.Value;
        LongTermSum += entry.Value;
        LongTermCount++;
        if (LongTermLowest > entry.Value)
            LongTermLowest = entry.Value;
        if (LongTermHighest < entry.Value)
            LongTermHighest = entry.Value;
    }

    /// <summary>The sort key GetHistorySorted sets.</summary>
    internal double SortValue { get; set; }

    // value + the other set's last value + its slope times the time since it.
    private static TimeAndValue Extrapolated(TimeAndValue value, TimeAndValue otherLast, double otherSlope) =>
        new(value.Time, value.Value + otherLast.Value + otherSlope * (value.Time - otherLast.Time));

    // The slope into values[index] of a continuous set, or the previous slope.
    private static double SlopeAt(ValueRing values, int index, StatisticsDataCategory category, double previous)
    {
        if (index > 0 && values[index].Time != values[index - 1].Time && category == StatisticsDataCategory.Continuous)
            return (values[index].Value - values[index - 1].Value) / (values[index].Time - values[index - 1].Time);
        return previous;
    }

    /// <summary>DataStructures::Queue: a ring that grows, popped at either end and indexed from the front.</summary>
    private sealed class ValueRing : IReadOnlyList<TimeAndValue>
    {
        private TimeAndValue[] _items = new TimeAndValue[16];
        private int _head;

        public int Count { get; private set; }

        public TimeAndValue this[int index] => _items[(_head + index) % _items.Length];

        public void PushBack(TimeAndValue value)
        {
            if (Count == _items.Length)
            {
                var grown = new TimeAndValue[_items.Length * 2];
                for (var i = 0; i < Count; i++)
                    grown[i] = this[i];
                _items = grown;
                _head = 0;
            }
            _items[(_head + Count) % _items.Length] = value;
            Count++;
        }

        public void PopFront()
        {
            _head = (_head + 1) % _items.Length;
            Count--;
        }

        public TimeAndValue PopBack()
        {
            var value = this[Count - 1];
            Count--;
            return value;
        }

        public void Clear()
        {
            _head = 0;
            Count = 0;
        }

        public IEnumerator<TimeAndValue> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
