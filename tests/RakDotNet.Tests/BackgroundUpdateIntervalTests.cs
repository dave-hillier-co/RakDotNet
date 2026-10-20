using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;

namespace RakDotNet.Tests;

// RakNet's update thread waits up to 10 ms after each RunUpdateCycle for data or a send to wake it
// (quitAndDataEvents.WaitOnEvent(10), RakPeer.cpp:6357-6358). The interval runs from the end of the last cycle, not on
// a fixed beat: a receiver that has just taken a burst of datagrams therefore runs its next cycle, and sends the
// delayed ACK, one interval after the burst rather than up to two.
public sealed class BackgroundUpdateIntervalTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

    [Fact]
    public void A_cycle_that_something_else_woke_pushes_the_next_timed_cycle_back_by_the_update_interval()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        using var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Threading = PeerThreading.Background,
            TimeProvider = time,
        });
        var cycles = new CycleCounter();
        peer.LowLevel.Observers.Attach(cycles);
        WakeWithACommand(peer);
        WaitFor(() => cycles.Count > 0);
        Thread.Sleep(Settle);
        var settled = cycles.Count;

        // 6 ms after the last cycle, a command wakes the loop: the next timed cycle is due 10 ms after this one.
        time.Advance(TimeSpan.FromMilliseconds(6));
        WakeWithACommand(peer);
        WaitFor(() => cycles.Count == settled + 1);

        // 11 ms after the settled cycle, when a fixed 10 ms beat would have run one, nothing runs.
        time.Advance(TimeSpan.FromMilliseconds(5));
        Thread.Sleep(Settle);
        Assert.Equal(settled + 1, cycles.Count);

        // 10 ms after the woken cycle, the timed one runs.
        time.Advance(TimeSpan.FromMilliseconds(5));
        WaitFor(() => cycles.Count == settled + 2);
    }

    // Any command wakes the loop; this one has no effect on the wire.
    private static void WakeWithACommand(RakPeer peer) => peer.OfflinePingResponse = "wake"u8.ToArray();

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!condition())
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "The loop did not run the expected cycle.");
            Thread.Sleep(1);
        }
    }

    private sealed class CycleCounter : INetworkObserver
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void OnUpdateCycle() => Interlocked.Increment(ref _count);
    }
}
