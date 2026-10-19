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

    private static RakPeer Start() => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 1,
        Bindings = [ManualPeers.Loopback],
        Threading = PeerThreading.Manual,
    });
}
