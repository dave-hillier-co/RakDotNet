using System.Diagnostics;

namespace RakDotNet.Plugins;

/// <summary>
/// An object whose values are tracked: StatisticsHistory::TrackedObjectData. <see cref="UserData"/> is anything the
/// application keeps with the object, RakNet's void* userData.
/// </summary>
public readonly record struct TrackedObjectData(ulong ObjectId, int ObjectType, object? UserData);

/// <summary>StatisticsHistory::SHErrorCode.</summary>
public enum StatisticsHistoryError : byte
{
    /// <summary>SH_OK</summary>
    Ok,

    /// <summary>SH_UKNOWN_OBJECT</summary>
    UnknownObject,

    /// <summary>SH_UKNOWN_KEY</summary>
    UnknownKey,

    /// <summary>SH_INVALID_PARAMETER</summary>
    InvalidParameter,
}

/// <summary>StatisticsHistory::SHSortOperation: the figure keys are sorted by, then by key.</summary>
public enum StatisticsSortOperation : byte
{
    /// <summary>SH_DO_NOT_SORT, which RakNet sorts by the long-term lowest value, descending.</summary>
    DoNotSort,
    ByRecentSumAscending,
    ByRecentSumDescending,
    ByLongTermSumAscending,
    ByLongTermSumDescending,
    ByRecentSumOfSquaresAscending,
    ByRecentSumOfSquaresDescending,
    ByRecentAverageAscending,
    ByRecentAverageDescending,
    ByLongTermAverageAscending,
    ByLongTermAverageDescending,
    ByRecentHighestAscending,
    ByRecentHighestDescending,
    ByRecentLowestAscending,
    ByRecentLowestDescending,
    ByLongTermHighestAscending,
    ByLongTermHighestDescending,
    ByLongTermLowestAscending,
    ByLongTermLowestDescending,
}

/// <summary>
/// StatisticsHistory: numeric values recorded over time per object and key, with sums, averages, extremes and
/// histograms over recent or all-time values.
/// </summary>
/// <remarks>
/// Ported from StatisticsHistory.cpp. Times are milliseconds on the clock given at construction (RakNet::GetTime), which
/// <see cref="GetHistorySorted"/> and <see cref="MergeAllObjectsOnKey"/> read to cull expired values; the default is a
/// monotonic clock that starts with the process. Objects are kept in ascending ID order and keys in the order first
/// added. Not thread-safe.
/// </remarks>
public sealed class StatisticsHistory
{
    private static readonly long ProcessStart = Stopwatch.GetTimestamp();

    private readonly Func<ulong> _clock;
    private readonly List<TrackedObject> _objects = [];

    /// <param name="clock">The time in milliseconds; the process's monotonic clock when null.</param>
    public StatisticsHistory(Func<ulong>? clock = null)
    {
        _clock = clock ?? (static () => (ulong)Stopwatch.GetElapsedTime(ProcessStart).TotalMilliseconds);
    }

    /// <summary>SetDefaultTimeToTrack: how long new keys hold values, in milliseconds. Default 30000.</summary>
    public ulong DefaultTimeToTrack { get; set; } = 30_000;

    /// <summary>GetObjectCount</summary>
    public int ObjectCount => _objects.Count;

    /// <summary>AddObject: false when an object with the ID exists.</summary>
    public bool AddObject(TrackedObjectData data)
    {
        var index = Search(data.ObjectId);
        if (index >= 0)
            return false;
        _objects.Insert(~index, new TrackedObject(data));
        return true;
    }

    /// <summary>RemoveObject: false when there is no such object.</summary>
    public bool RemoveObject(ulong objectId, out object? userData)
    {
        var index = GetObjectIndex(objectId);
        if (index < 0)
        {
            userData = null;
            return false;
        }
        userData = _objects[index].Data.UserData;
        RemoveObjectAtIndex(index);
        return true;
    }

    /// <inheritdoc cref="RemoveObject(ulong, out object?)"/>
    public bool RemoveObject(ulong objectId) => RemoveObject(objectId, out _);

    /// <summary>RemoveObjectAtIndex</summary>
    public void RemoveObjectAtIndex(int index) => _objects.RemoveAt(index);

    /// <summary>Clear: removes every object.</summary>
    public void Clear() => _objects.Clear();

    /// <summary>GetObjectAtIndex, in ascending ID order.</summary>
    public TrackedObjectData GetObjectAtIndex(int index) => _objects[index].Data;

    /// <summary>GetObjectIndex: the object's index, or -1.</summary>
    public int GetObjectIndex(ulong objectId)
    {
        var index = Search(objectId);
        return index >= 0 ? index : -1;
    }

    /// <summary>
    /// AddValueByObjectID: false when there is no such object. With <paramref name="combineEqualTimes"/>, a value at the
    /// same time as the key's last one is added to it instead of recorded beside it.
    /// </summary>
    public bool AddValueByObjectId(ulong objectId, string key, double value, ulong time, bool combineEqualTimes)
    {
        var index = GetObjectIndex(objectId);
        if (index < 0)
            return false;
        AddValueByIndex(index, key, value, time, combineEqualTimes);
        return true;
    }

    /// <summary>AddValueByIndex: records a value for the key, creating the key with <see cref="DefaultTimeToTrack"/>.</summary>
    public void AddValueByIndex(int index, string key, double value, ulong time, bool combineEqualTimes)
    {
        ArgumentNullException.ThrowIfNull(key);
        var queues = _objects[index].Queues;
        if (!queues.TryGetValue(key, out var queue))
        {
            queue = new TimeAndValueQueue { Key = key, TimeToTrackValues = DefaultTimeToTrack };
            queues.Add(key, queue);
        }
        queue.Add(value, time, combineEqualTimes);
    }

    /// <summary>
    /// GetHistoryForKey: the object's values for the key, culled to <paramref name="now"/>. The queue is the history's
    /// own, not a copy.
    /// </summary>
    public StatisticsHistoryError GetHistoryForKey(ulong objectId, string key, out TimeAndValueQueue? values, ulong now)
    {
        ArgumentNullException.ThrowIfNull(key);
        values = null;
        var index = GetObjectIndex(objectId);
        if (index < 0)
            return StatisticsHistoryError.UnknownObject;
        if (!_objects[index].Queues.TryGetValue(key, out values))
            return StatisticsHistoryError.UnknownKey;
        values.CullExpiredValues(now);
        return StatisticsHistoryError.Ok;
    }

    /// <summary>
    /// GetHistorySorted (StatisticsHistory.cpp:345-395): appends the object's queues to <paramref name="values"/>, culled
    /// to the clock, sorted by the chosen figure and then by key. False when there is no such object.
    /// </summary>
    public bool GetHistorySorted(ulong objectId, StatisticsSortOperation sortType, List<TimeAndValueQueue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var index = GetObjectIndex(objectId);
        if (index < 0)
            return false;
        var now = _clock();
        var sorted = new List<TimeAndValueQueue>(_objects[index].Queues.Values);
        foreach (var queue in sorted)
        {
            queue.CullExpiredValues(now);
            queue.SortValue = SortValue(queue, sortType);
        }
        var ascending = IsAscending(sortType);
        sorted.Sort((a, b) =>
        {
            var order = a.SortValue < b.SortValue ? -1 : a.SortValue > b.SortValue ? 1 : string.CompareOrdinal(a.Key, b.Key);
            return ascending ? order : -order;
        });
        values.AddRange(sorted);
        return true;
    }

    /// <summary>
    /// MergeAllObjectsOnKey: clears <paramref name="output"/> and merges into it every object's values for the key,
    /// each culled to the clock.
    /// </summary>
    public void MergeAllObjectsOnKey(string key, TimeAndValueQueue output, StatisticsDataCategory dataCategory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        var now = _clock();
        foreach (var tracked in _objects)
        {
            if (!tracked.Queues.TryGetValue(key, out var input))
                continue;
            input.CullExpiredValues(now);
            TimeAndValueQueue.MergeSets(output, dataCategory, input, dataCategory, output);
        }
    }

    /// <summary>GetUniqueKeyList: replaces <paramref name="keys"/> with every key any object has, once each.</summary>
    public void GetUniqueKeyList(List<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Clear();
        foreach (var tracked in _objects)
        {
            foreach (var key in tracked.Queues.Keys)
            {
                if (!keys.Contains(key))
                    keys.Add(key);
            }
        }
    }

    private static double SortValue(TimeAndValueQueue queue, StatisticsSortOperation sortType) => sortType switch
    {
        StatisticsSortOperation.ByRecentSumAscending or StatisticsSortOperation.ByRecentSumDescending => queue.RecentSum,
        StatisticsSortOperation.ByLongTermSumAscending or StatisticsSortOperation.ByLongTermSumDescending => queue.LongTermSum,
        StatisticsSortOperation.ByRecentSumOfSquaresAscending or StatisticsSortOperation.ByRecentSumOfSquaresDescending => queue.RecentSumOfSquares,
        StatisticsSortOperation.ByRecentAverageAscending or StatisticsSortOperation.ByRecentAverageDescending => queue.RecentAverage,
        StatisticsSortOperation.ByLongTermAverageAscending or StatisticsSortOperation.ByLongTermAverageDescending => queue.LongTermAverage,
        StatisticsSortOperation.ByRecentHighestAscending or StatisticsSortOperation.ByRecentHighestDescending => queue.RecentHighest,
        StatisticsSortOperation.ByRecentLowestAscending or StatisticsSortOperation.ByRecentLowestDescending => queue.RecentLowest,
        StatisticsSortOperation.ByLongTermHighestAscending or StatisticsSortOperation.ByLongTermHighestDescending => queue.LongTermHighest,
        _ => queue.LongTermLowest,
    };

    private static bool IsAscending(StatisticsSortOperation sortType) => sortType is StatisticsSortOperation.ByRecentSumAscending
        or StatisticsSortOperation.ByLongTermSumAscending or StatisticsSortOperation.ByRecentSumOfSquaresAscending
        or StatisticsSortOperation.ByRecentAverageAscending or StatisticsSortOperation.ByLongTermAverageAscending
        or StatisticsSortOperation.ByRecentHighestAscending or StatisticsSortOperation.ByRecentLowestAscending
        or StatisticsSortOperation.ByLongTermHighestAscending or StatisticsSortOperation.ByLongTermLowestAscending;

    // DataStructures::OrderedList by objectId: the index, or the complement of where it would go.
    private int Search(ulong objectId)
    {
        int low = 0, high = _objects.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var id = _objects[middle].Data.ObjectId;
            if (id == objectId)
                return middle;
            if (id < objectId)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }

    private sealed class TrackedObject(TrackedObjectData data)
    {
        public TrackedObjectData Data { get; } = data;

        public Dictionary<string, TimeAndValueQueue> Queues { get; } = new(StringComparer.Ordinal);
    }
}
