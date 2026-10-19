using System.Buffers;
using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Transport;

namespace RakDotNet.Tests;

// docs/api-design.md section 7: pending operations end rather than hang. A command can pass the state check just
// before shutdown and reach the queue after shutdown drained it; these tests enqueue such a command directly.
public sealed class ShutdownRaceTests
{
    private static readonly SystemAddress Remote = new(IPAddress.Parse("192.0.2.1"), 19132);

    [Fact]
    public void A_connect_that_reaches_the_queue_after_shutdown_is_cancelled()
    {
        var peer = Start();
        var completion = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingConnect(Remote, Remote.ToIPEndPoint(), new ConnectOptions(), completion);
        peer.Dispose();

        peer.Enqueue(new PeerCommand { Kind = CommandKind.Connect, Target = Remote, State = pending });

        Assert.True(completion.Task.IsCanceled);
    }

    [Fact]
    public async Task A_tracked_send_that_reaches_the_queue_after_shutdown_ends_with_PeerShutdown()
    {
        var peer = Start();
        var tracker = new TrackedSend(_ => { });
        var task = tracker.Task;
        peer.Dispose();

        peer.Enqueue(new PeerCommand
        {
            Kind = CommandKind.Send,
            Target = Remote,
            Payload = ArrayPool<byte>.Shared.Rent(1),
            BitLength = 8,
            Reliability = Reliability.ReliableWithAckReceipt,
            Receipt = new SendReceipt(7),
            State = tracker,
            Version = tracker.Version,
        });

        Assert.True(task.IsCompleted);
        var outcome = await task;
        Assert.Equal(DeliveryStatus.PeerShutdown, outcome.Status);
        Assert.Equal(new SendReceipt(7), outcome.Receipt);
    }

    [Fact]
    public void A_read_on_another_thread_while_shutdown_holds_the_dispatch_step_returns_false()
    {
        // Section 7: from the shutdown call on, a concurrent read finishes first and later reads return false. The
        // plugin step of shutdown holds the dispatch step, so a read from another thread then must not throw.
        var plugin = new ReadingOnShutdownPlugin();
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            Bindings = [ManualPeers.Loopback],
            Threading = PeerThreading.Manual,
            Plugins = [plugin],
        });

        peer.Dispose();

        Assert.Null(plugin.Failure);
        Assert.False(plugin.Read);
    }

    private static RakPeer Start() => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 1,
        Bindings = [ManualPeers.Loopback],
        Threading = PeerThreading.Manual,
    });

    private sealed class ReadingOnShutdownPlugin : IRakPlugin
    {
        private IPluginHost? _host;

        public bool? Read { get; private set; }

        public Exception? Failure { get; private set; }

        public void OnAttached(IPluginHost host) => _host = host;

        public void OnShutdown()
        {
            var peer = _host!.Peer!;
            // A thread of its own: a task waited on here could be inlined onto the shutdown thread.
            var reader = new Thread(() =>
            {
                try
                {
                    Read = peer.Events.TryRead(out _);
                }
                catch (InvalidOperationException exception)
                {
                    Failure = exception;
                }
            });
            reader.Start();
            reader.Join();
        }
    }
}
