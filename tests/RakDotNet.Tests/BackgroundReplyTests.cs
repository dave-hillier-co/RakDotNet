using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Tests;

// A reply made while a cycle handles a message, such as ID_CONNECTED_PONG, is framed by the reliability layer's next
// update. RakNet asks for another update straight after queuing the pong (RakPeer.cpp:6090-6091), rather than waiting
// for the next update interval, so a Background peer runs its next cycle at once.
public sealed class BackgroundReplyTests
{
    [Fact]
    public async Task A_pong_goes_out_without_waiting_for_the_update_interval()
    {
        var serverTime = new SwitchableTimeProvider();
        using var server = RakPeer.Start(Options(serverTime, maxIncoming: 4));
        using var client = RakPeer.Start(Options(TimeProvider.System, maxIncoming: 0));
        var pongs = new PongWatch();
        server.LowLevel.Observers.Attach(pongs);
        var connected = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(connected.IsConnected);
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        // From here the server's update timer is silent, so only the ping's datagram wakes its loop.
        serverTime.TimersFire = false;
        pongs.Watching = true;
        connected.Connection!.Ping();
        var finished = await Task.WhenAny(pongs.Sent, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Assert.Same(pongs.Sent, finished);
    }

    private static RakPeerOptions Options(TimeProvider time, int maxIncoming) => new()
    {
        MaxConnections = 4,
        MaxIncomingConnections = maxIncoming,
        Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
        Threading = PeerThreading.Background,
        TimeProvider = time,
    };

    /// <summary>The system clock, with timers that can be silenced.</summary>
    private sealed class SwitchableTimeProvider : TimeProvider
    {
        private volatile bool _timersFire = true;

        public bool TimersFire
        {
            get => _timersFire;
            set => _timersFire = value;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            System.CreateTimer(s =>
            {
                if (_timersFire)
                    callback(s);
            }, state, dueTime, period);
    }

    private sealed class PongWatch : INetworkObserver
    {
        private readonly TaskCompletionSource _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Sent => _sent.Task;

        private volatile bool _watching;

        /// <summary>Set once the ping is sent; the handshake's own pongs come before.</summary>
        public bool Watching
        {
            get => _watching;
            set => _watching = value;
        }

        public void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote)
        {
            if (!Watching || (datagram[0] & 0x80) == 0 || !EncapsulatedMessageReader.TryOpenDatagram(datagram.ToArray(), out _, out var reader))
                return;
            while (reader.TryReadNext(out var message))
            {
                if (message.Payload.Span[0] == (byte)MessageId.ConnectedPong)
                    _sent.TrySetResult();
            }
        }
    }
}
