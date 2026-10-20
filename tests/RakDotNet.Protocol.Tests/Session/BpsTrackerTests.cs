using RakDotNet.Protocol.Session;

namespace RakDotNet.Protocol.Tests.Session;

public class BpsTrackerTests
{
    [Fact]
    public void An_entry_leaves_the_last_second_once_cleared_more_than_a_second_after_it_was_pushed()
    {
        var tracker = new BpsTracker();
        tracker.Push(1_000_000, 10);
        tracker.Push(1_500_000, 20);

        tracker.ClearExpired(2_000_000);
        Assert.Equal(30UL, tracker.LastSecond);

        tracker.ClearExpired(2_000_001);
        Assert.Equal(20UL, tracker.LastSecond);
        Assert.Equal(30UL, tracker.Total);
    }

    [Fact]
    public void Pushes_within_a_bucket_are_kept_as_one_entry_that_expires_with_the_first()
    {
        var tracker = new BpsTracker();
        tracker.Push(1_000_000, 1);
        tracker.Push(1_000_000 + BpsTracker.BucketUs - 1, 2);
        tracker.Push(1_000_000 + BpsTracker.BucketUs, 4);

        tracker.ClearExpired(2_000_001);
        Assert.Equal(4UL, tracker.LastSecond);
        tracker.ClearExpired(2_000_001 + BpsTracker.BucketUs);
        Assert.Equal(0UL, tracker.LastSecond);
    }

    [Fact]
    public void A_second_of_pushes_at_any_rate_allocates_nothing()
    {
        var tracker = new BpsTracker();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (long timeUs = 0; timeUs < 5_000_000; timeUs += 10)
        {
            tracker.Push(timeUs, 1);
            if (timeUs % 100_000 == 0)
                tracker.ClearExpired(timeUs);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
