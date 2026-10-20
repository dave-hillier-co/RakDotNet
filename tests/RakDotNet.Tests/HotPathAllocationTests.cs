using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

/// <summary>
/// The allocation budget tests measure every allocation in the process while they run, so they run alone.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AllocationMeasurement
{
    public const string Name = "Allocation measurement";
}

// docs/api-design.md section 1: the hot path allocates nothing per message in steady state, in both threading modes.
// A client sends small RELIABLE_ORDERED messages to a server over loopback UDP and the server reads each one; the
// acknowledgements flow back. Everything both peers allocate after warm-up is charged to the messages.
[Collection(AllocationMeasurement.Name)]
public sealed class HotPathAllocationTests : IDisposable
{
    /// <summary>The allocation budget of the RakPeer hot path: bytes per non-split message, after warm-up.</summary>
    public const long BudgetBytesPerMessage = 0;

    private const int WarmUpMessages = 2_000;
    private const int MeasuredMessages = 2_000;

    // On the real clock how many messages are unacknowledged at once (ACKs wait up to 10 ms) varies with thread
    // scheduling, and the pools grow to the highest count seen, so a new high under load can still allocate a few KB
    // after warm-up. The longer windows keep the budget at 0 B per message; the test passes once one window is within
    // it, which a path that allocates per message never is.
    private const int BackgroundWarmUpMessages = 20_000;
    private const int BackgroundMeasuredMessages = 20_000;
    private const int BackgroundWindows = 3;
    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
    private readonly byte[] _message = new byte[32];
    private PeerThreading _threading;
    private RakPeer? _server;
    private RakPeer? _client;
    private RakConnection? _toServer;
    private Reliability _reliability = Reliability.ReliableOrdered;
    private int _receipts;

    public HotPathAllocationTests() => _message[0] = (byte)MessageId.UserPacketEnum;

    [Fact]
    public void Manual_mode_send_and_receive_of_a_non_split_message_allocates_nothing_in_steady_state()
    {
        Start(PeerThreading.Manual);
        Transfer(WarmUpMessages);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Transfer(MeasuredMessages);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertWithinBudget(allocated, MeasuredMessages, "RakPeer in Manual mode (Send, Update on both peers, Events.TryRead)");
    }

    [Fact]
    public void Manual_mode_send_with_an_ack_receipt_and_its_receipt_event_allocate_nothing_in_steady_state()
    {
        _reliability = Reliability.ReliableOrderedWithAckReceipt;
        Start(PeerThreading.Manual);
        Transfer(WarmUpMessages);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Transfer(MeasuredMessages);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(_receipts, WarmUpMessages, WarmUpMessages + MeasuredMessages);
        AssertWithinBudget(allocated, MeasuredMessages, "RakPeer in Manual mode with ID_SND_RECEIPT_ACKED (Send, Update on both peers, Events.TryRead)");
    }

    [Fact]
    public void Background_mode_send_and_receive_of_a_non_split_message_allocates_nothing_in_steady_state()
    {
        Start(PeerThreading.Background);
        Transfer(BackgroundWarmUpMessages);

        var allocated = long.MaxValue;
        for (var window = 0; window < BackgroundWindows && allocated / BackgroundMeasuredMessages > BudgetBytesPerMessage; window++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            Transfer(BackgroundMeasuredMessages);
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        AssertWithinBudget(allocated, BackgroundMeasuredMessages, "RakPeer in Background mode (Send, both I/O loop threads, Events.TryRead)");
    }

    public void Dispose()
    {
        _client?.Dispose();
        _server?.Dispose();
    }

    private static void AssertWithinBudget(long allocated, int messages, string path) =>
        Assert.True(
            allocated / messages <= BudgetBytesPerMessage,
            $"{path} allocated {allocated} B for {messages} messages ({allocated / messages} B per message); "
            + $"the budget is {BudgetBytesPerMessage} B per message."
        );

    private void Start(PeerThreading threading)
    {
        // Manual mode runs on a fake clock that the test advances; Background mode needs the real one for its loop.
        _threading = threading;
        var time = threading == PeerThreading.Manual ? _time : TimeProvider.System;
        var loopback = new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0));
        _server = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            MaxIncomingConnections = 1,
            Bindings = [loopback],
            Threading = threading,
            TimeProvider = time,
        });
        _client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1, Bindings = [loopback], Threading = threading, TimeProvider = time });

        Assert.Equal(ConnectAttemptResult.Started, _client.Connect(_server.Sockets[0].LocalAddress));
        var serverConnected = false;
        var deadline = Stopwatch.GetTimestamp() + (long)(MessageTimeout.TotalSeconds * Stopwatch.Frequency);
        while (_toServer is null || !serverConnected)
        {
            Assert.True(Stopwatch.GetTimestamp() < deadline, "The client and server did not connect.");
            Cycle();
            while (_client.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Connected)
                    _toServer = e.Connection;
            }
            while (_server.Events.TryRead(out var e))
                serverConnected |= e.Kind == PeerEventKind.Connected;
            Thread.Yield();
        }
    }

    // Each message is sent, then the cycles run until the server has read it.
    private void Transfer(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.True(_toServer!.Send(_message, _reliability).IsQueued);
            var deadline = Stopwatch.GetTimestamp() + (long)(MessageTimeout.TotalSeconds * Stopwatch.Frequency);
            var received = false;
            var spinner = default(SpinWait);
            while (!received)
            {
                // Not Assert.True with an interpolated message, which would allocate on every pass.
                if (Stopwatch.GetTimestamp() >= deadline)
                    Assert.Fail($"Message {i} was not delivered.");
                Cycle();
                while (_server!.Events.TryRead(out var e))
                    received |= e.Kind == PeerEventKind.Message;
                while (_client!.Events.TryRead(out var e))
                {
                    if (e.Kind == PeerEventKind.ReceiptAcked)
                        _receipts++;
                }
                spinner.SpinOnce(sleep1Threshold: -1);
            }
        }
    }

    private void Cycle()
    {
        if (_threading != PeerThreading.Manual)
            return;
        _time.Advance(TimeSpan.FromMilliseconds(1));
        _client!.Update();
        _server!.Update();
    }
}
