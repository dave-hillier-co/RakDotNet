namespace RakDotNet.Plugins.Tests.StatisticsHistory;

// Expected values are worked by hand from StatisticsHistory.cpp.
public class StatisticsHistoryTests
{
    private ulong _now = 100_000;

    [Fact]
    public void Objects_are_kept_in_id_order_and_an_id_is_added_once()
    {
        var history = NewHistory();

        Assert.True(history.AddObject(new TrackedObjectData(20, 1, "b")));
        Assert.True(history.AddObject(new TrackedObjectData(10, 2, "a")));
        Assert.False(history.AddObject(new TrackedObjectData(10, 3, "c")));

        Assert.Equal(2, history.ObjectCount);
        Assert.Equal(new TrackedObjectData(10, 2, "a"), history.GetObjectAtIndex(0));
        Assert.Equal(1, history.GetObjectIndex(20));
        Assert.Equal(-1, history.GetObjectIndex(30));
        Assert.True(history.RemoveObject(20, out var userData));
        Assert.Equal("b", userData);
        Assert.False(history.RemoveObject(20, out _));
    }

    [Fact]
    public void Added_values_update_the_recent_and_long_term_sums_and_extremes()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));

        Assert.True(history.AddValueByObjectId(1, "k", 3, 10, combineEqualTimes: false));
        history.AddValueByObjectId(1, "k", -1, 20, combineEqualTimes: false);
        history.AddValueByObjectId(1, "k", 4, 30, combineEqualTimes: false);
        Assert.False(history.AddValueByObjectId(2, "k", 4, 30, combineEqualTimes: false));

        var queue = History(history, 1, "k", 30);
        Assert.Equal(6, queue.RecentSum);
        Assert.Equal(26, queue.RecentSumOfSquares);
        Assert.Equal(6, queue.LongTermSum);
        Assert.Equal(3, queue.LongTermCount);
        Assert.Equal(-1, queue.LongTermLowest);
        Assert.Equal(4, queue.LongTermHighest);
        Assert.Equal(2, queue.RecentAverage);
        Assert.Equal(-1, queue.RecentLowest);
        Assert.Equal(4, queue.RecentHighest);
        Assert.Equal(2, queue.LongTermAverage);
        Assert.Equal(20ul, queue.TimeRange);
        Assert.Equal(3, queue.GetSumSinceTime(20));
    }

    [Fact]
    public void The_standard_deviation_is_the_variance_as_RakNet_computes_it()
    {
        // GetRecentStandardDeviation returns meanOfSquares - squareOfMean, with no square root (line 297-305).
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddValueByObjectId(1, "k", 2, 10, false);
        history.AddValueByObjectId(1, "k", 6, 20, false);

        Assert.Equal(4, History(history, 1, "k", 20).RecentStandardDeviation);
    }

    [Fact]
    public void Values_at_the_same_time_combine_when_asked()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddValueByObjectId(1, "k", 2, 10, true);
        history.AddValueByObjectId(1, "k", 3, 10, true);
        history.AddValueByObjectId(1, "k", 4, 10, false);

        var queue = History(history, 1, "k", 10);
        Assert.Equal([new TimeAndValue(10, 5), new TimeAndValue(10, 4)], queue.Values);
        Assert.Equal(9, queue.LongTermSum);
        Assert.Equal(2, queue.LongTermCount);
        Assert.Equal(41, queue.RecentSumOfSquares);
        // The extremes are not taken back when a value is combined (lines 312-328).
        Assert.Equal(2, queue.LongTermLowest);
    }

    [Fact]
    public void Values_older_than_the_time_to_track_are_culled_from_the_recent_figures_only()
    {
        var history = NewHistory();
        history.DefaultTimeToTrack = 100;
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddValueByObjectId(1, "k", 5, 1_000, false);
        history.AddValueByObjectId(1, "k", 7, 1_050, false);

        var queue = History(history, 1, "k", 1_101);

        Assert.Equal([new TimeAndValue(1_050, 7)], queue.Values);
        Assert.Equal(7, queue.RecentSum);
        Assert.Equal(12, queue.LongTermSum);
        Assert.Equal(100ul, queue.TimeToTrackValues);
    }

    [Fact]
    public void Asking_for_an_unknown_object_or_key_says_which()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));

        Assert.Equal(StatisticsHistoryError.UnknownObject, history.GetHistoryForKey(2, "k", out _, 0));
        Assert.Equal(StatisticsHistoryError.UnknownKey, history.GetHistoryForKey(1, "k", out var values, 0));
        Assert.Null(values);
    }

    [Fact]
    public void An_empty_queue_has_RakNets_extremes()
    {
        var queue = new TimeAndValueQueue();

        Assert.Equal(double.MaxValue, queue.RecentLowest);
        Assert.Equal(-double.MaxValue, queue.RecentHighest);
        Assert.Equal(double.MaxValue, queue.LongTermLowest);
        Assert.Equal(-double.MaxValue, queue.LongTermHighest);
        Assert.Equal(0, queue.RecentAverage);
        Assert.Equal(0, queue.RecentStandardDeviation);
        Assert.Equal(0ul, queue.TimeRange);
    }

    [Fact]
    public void Resizing_a_discrete_set_sums_each_interval_at_its_midpoint()
    {
        var queue = Ramp();
        var histogram = new List<TimeAndValue>();

        queue.ResizeSampleSet(5, histogram, StatisticsDataCategory.Discrete);

        Assert.Equal([new(1, 3), new(3, 7), new(5, 11), new(7, 15), new(9, 19)], histogram);
    }

    [Fact]
    public void Resizing_a_continuous_set_averages_each_interval()
    {
        var queue = Ramp();
        var histogram = new List<TimeAndValue>();

        queue.ResizeSampleSet(5, histogram, StatisticsDataCategory.Continuous);

        Assert.Equal([new(1, 1), new(3, 3.5), new(5, 5.5), new(7, 7.5), new(9, 9.5)], histogram);
    }

    [Fact]
    public void Resizing_to_one_sample_gives_the_time_range_and_recent_sum()
    {
        var histogram = new List<TimeAndValue>();

        Ramp().ResizeSampleSet(1, histogram, StatisticsDataCategory.Discrete);

        Assert.Equal([new TimeAndValue(10, 55)], histogram);
    }

    [Fact]
    public void Clipping_the_start_interpolates_a_first_sample_at_the_clip_time()
    {
        // Samples at 1, 3, 5, 7, 9; a clip of 3 from the first sample's time starts the histogram at 4.
        var histogram = new List<TimeAndValue>();

        Ramp().ResizeSampleSet(5, histogram, StatisticsDataCategory.Discrete, timeClipStart: 3);

        Assert.Equal([new(4, 9), new(5, 11), new(7, 15), new(9, 19)], histogram);
    }

    [Fact]
    public void Clipping_the_end_interpolates_a_last_sample_at_the_clip_time()
    {
        var histogram = new List<TimeAndValue>();

        Ramp().ResizeSampleSet(5, histogram, StatisticsDataCategory.Discrete, timeClipEnd: 3);

        Assert.Equal([new(1, 3), new(3, 7), new(5, 11), new(6, 13)], histogram);
    }

    [Fact]
    public void Merging_discrete_sets_interleaves_them_by_time_and_adds_their_figures()
    {
        var lhs = Queue(("k", [(0, 1), (10, 1)]));
        var rhs = Queue(("k", [(5, 2)]));
        var output = new TimeAndValueQueue();

        TimeAndValueQueue.MergeSets(lhs, StatisticsDataCategory.Discrete, rhs, StatisticsDataCategory.Discrete, output);

        Assert.Equal([new(0, 1), new(5, 2), new(10, 1)], output.Values);
        Assert.Equal(4, output.RecentSum);
        Assert.Equal(3, output.LongTermCount);
        Assert.Equal(1, output.LongTermLowest);
        Assert.Equal(2, output.LongTermHighest);
        Assert.Equal("k", output.Key);
    }

    [Fact]
    public void Merging_continuous_sets_adds_each_value_to_the_other_sets_last_value()
    {
        var lhs = Queue(("k", [(0, 1), (10, 1)]));
        var rhs = Queue(("k", [(5, 2)]));
        var output = new TimeAndValueQueue();

        TimeAndValueQueue.MergeSets(lhs, StatisticsDataCategory.Continuous, rhs, StatisticsDataCategory.Continuous, output);

        Assert.Equal([new(0, 1), new(5, 3), new(10, 3)], output.Values);
        Assert.Equal(7, output.RecentSum);
        Assert.Equal(19, output.RecentSumOfSquares);
    }

    [Fact]
    public void Merging_continuous_sets_extrapolates_along_the_other_sets_slope()
    {
        var lhs = Queue(("k", [(0, 0), (10, 10), (30, 0)]));
        var rhs = Queue(("k", [(20, 1)]));
        var output = new TimeAndValueQueue();

        TimeAndValueQueue.MergeSets(lhs, StatisticsDataCategory.Continuous, rhs, StatisticsDataCategory.Continuous, output);

        // At 20 the last lhs value (10 at 10) plus its slope of 1 per ms for 10 ms: 1 + 10 + 10.
        Assert.Equal([new(0, 0), new(10, 10), new(20, 21), new(30, 1)], output.Values);
    }

    [Fact]
    public void Merging_every_object_on_a_key_combines_their_values()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddObject(new TrackedObjectData(2, 0, null));
        history.AddObject(new TrackedObjectData(3, 0, null));
        history.AddValueByObjectId(1, "k", 1, 99_990, false);
        history.AddValueByObjectId(2, "k", 2, 99_995, false);
        history.AddValueByObjectId(3, "other", 9, 99_995, false);
        var output = new TimeAndValueQueue();

        history.MergeAllObjectsOnKey("k", output, StatisticsDataCategory.Discrete);

        Assert.Equal([new(99_990, 1), new(99_995, 2)], output.Values);
        Assert.Equal(3, output.LongTermSum);
    }

    [Fact]
    public void Sorted_history_orders_an_objects_keys_by_the_chosen_figure()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddValueByObjectId(1, "a", 5, 99_999, false);
        history.AddValueByObjectId(1, "b", 1, 99_999, false);
        history.AddValueByObjectId(1, "c", 3, 99_999, false);
        var ascending = new List<TimeAndValueQueue>();
        var descending = new List<TimeAndValueQueue>();
        var unsorted = new List<TimeAndValueQueue>();

        Assert.True(history.GetHistorySorted(1, StatisticsSortOperation.ByRecentSumAscending, ascending));
        history.GetHistorySorted(1, StatisticsSortOperation.ByRecentSumDescending, descending);
        history.GetHistorySorted(1, StatisticsSortOperation.DoNotSort, unsorted);

        Assert.Equal(["b", "c", "a"], ascending.Select(q => q.Key));
        Assert.Equal(["a", "c", "b"], descending.Select(q => q.Key));
        // SH_DO_NOT_SORT falls through to the long-term lowest, descending (lines 376-390).
        Assert.Equal(["a", "c", "b"], unsorted.Select(q => q.Key));
        Assert.False(history.GetHistorySorted(2, StatisticsSortOperation.DoNotSort, unsorted));
    }

    [Fact]
    public void Equal_figures_sort_by_key()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddValueByObjectId(1, "b", 1, 99_999, false);
        history.AddValueByObjectId(1, "a", 1, 99_999, false);
        var ascending = new List<TimeAndValueQueue>();
        var descending = new List<TimeAndValueQueue>();

        history.GetHistorySorted(1, StatisticsSortOperation.ByLongTermSumAscending, ascending);
        history.GetHistorySorted(1, StatisticsSortOperation.ByLongTermSumDescending, descending);

        Assert.Equal(["a", "b"], ascending.Select(q => q.Key));
        Assert.Equal(["b", "a"], descending.Select(q => q.Key));
    }

    [Fact]
    public void The_unique_key_list_holds_each_key_once()
    {
        var history = NewHistory();
        history.AddObject(new TrackedObjectData(1, 0, null));
        history.AddObject(new TrackedObjectData(2, 0, null));
        history.AddValueByObjectId(1, "x", 1, 1, false);
        history.AddValueByObjectId(1, "y", 1, 1, false);
        history.AddValueByObjectId(2, "y", 1, 1, false);
        history.AddValueByObjectId(2, "z", 1, 1, false);
        var keys = new List<string> { "stale" };

        history.GetUniqueKeyList(keys);

        Assert.Equal(["x", "y", "z"], keys);
    }

    [Fact]
    public void Interpolating_between_samples_at_one_time_averages_them()
    {
        Assert.Equal(3, TimeAndValueQueue.Interpolate(new(5, 2), new(5, 4), 9));
        Assert.Equal(15, TimeAndValueQueue.Interpolate(new(10, 10), new(20, 20), 15));
    }

    private Plugins.StatisticsHistory NewHistory() => new(() => _now);

    private static TimeAndValueQueue History(Plugins.StatisticsHistory history, ulong objectId, string key, ulong now)
    {
        Assert.Equal(StatisticsHistoryError.Ok, history.GetHistoryForKey(objectId, key, out var values, now));
        return values!;
    }

    // Values equal to their times, 0 to 10.
    private static TimeAndValueQueue Ramp() => Queue(("k", Enumerable.Range(0, 11).Select(t => ((ulong)t, (double)t)).ToArray()));

    private static TimeAndValueQueue Queue((string Key, (ulong Time, double Value)[] Values) set)
    {
        var history = new Plugins.StatisticsHistory(() => 0) { DefaultTimeToTrack = ulong.MaxValue };
        history.AddObject(new TrackedObjectData(1, 0, null));
        foreach (var (time, value) in set.Values)
            history.AddValueByObjectId(1, set.Key, value, time, false);
        history.GetHistoryForKey(1, set.Key, out var queue, 0);
        return queue!;
    }
}
