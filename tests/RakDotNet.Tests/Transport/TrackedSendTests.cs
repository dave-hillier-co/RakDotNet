using System.Diagnostics;
using System.Threading.Tasks.Sources;
using RakDotNet.Transport;

namespace RakDotNet.Tests.Transport;

// GetResult here is the value-task source's own member, called on a completed use as an awaiter would; it never blocks.
#pragma warning disable xUnit1031

public sealed class TrackedSendTests
{
    private static readonly DeliveryOutcome Stale = new(DeliveryStatus.Acked, new SendReceipt(1), SendStatus.Queued);

    [Fact]
    public void Completing_a_use_ends_it_once()
    {
        var tracker = new TrackedSend(_ => { });
        var version = tracker.Version;

        Assert.True(tracker.TryComplete(version, Stale));
        Assert.False(tracker.TryComplete(version, Stale));
        Assert.False(tracker.TryFail(version, new InvalidOperationException()));
        Assert.Equal(Stale, tracker.GetResult(version));

        Assert.NotEqual(version, tracker.Version);
        Assert.False(tracker.TryComplete(version, Stale));
        Assert.Equal(ValueTaskSourceStatus.Pending, tracker.GetStatus(tracker.Version));
    }

    // A cancelled SendAndTrackAsync leaves its receipt record behind, and the I/O cycle completes it later with the old
    // version while the consumer may be resetting the source in GetResult for its next use.
    [Fact]
    public void A_late_completion_never_reaches_the_next_use()
    {
        var tracker = new TrackedSend(_ => { });
        var current = (int)tracker.Version;
        var stop = false;
        var failures = 0;
        var late = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                try
                {
                    tracker.TryComplete((short)Volatile.Read(ref current), Stale);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                }
            }
        });
        late.Start();

        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(1) && Volatile.Read(ref failures) == 0)
            {
                var version = tracker.Version;
                Volatile.Write(ref current, version);
                tracker.TryFail(version, new OperationCanceledException());
                // As an awaiter does, take the result only once the use has completed, by either thread.
                var spin = new SpinWait();
                while (tracker.GetStatus(version) == ValueTaskSourceStatus.Pending)
                    spin.SpinOnce();
                try
                {
                    tracker.GetResult(version);
                }
                catch (OperationCanceledException)
                {
                }

                if (tracker.GetStatus(tracker.Version) != ValueTaskSourceStatus.Pending)
                {
                    Interlocked.Increment(ref failures);
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            late.Join();
        }

        Assert.Equal(0, failures);
    }
}
