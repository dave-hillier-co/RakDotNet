using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// The Background I/O loop must see shutdown whenever it arrives, including while a wake is being consumed.
public sealed class LoopShutdownTests
{
    [Fact]
    public void Shutdown_while_the_loop_is_woken_continuously_always_ends_the_loop()
    {
        // Many peers at once, so loop threads are preempted at every point of a wake, including between seeing no
        // shutdown and consuming the wake that shutdown sets.
        var hung = new List<int>();
        var workers = Enumerable.Range(0, 2 * Environment.ProcessorCount).Select(worker => new Thread(() =>
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (!ShutDownWhileWoken(attempt))
                {
                    lock (hung)
                        hung.Add(worker);
                    return;
                }
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());

        Assert.Empty(hung);
    }

    private static bool ShutDownWhileWoken(int attempt)
    {
        var factory = new WakingTransportFactory();
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Threading = PeerThreading.Background,
            Transport = factory,
        });
        // The last wake lands just before shutdown, as a timer tick or a datagram can, and nothing wakes the loop after.
        Thread.SpinWait(attempt % 32 * 100);
        factory.Transport!.Signal!();
        Thread.SpinWait(attempt % 7 * 10);

        var disposal = new Thread(peer.Dispose) { IsBackground = true };
        disposal.Start();
        return disposal.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class WakingTransportFactory : IDatagramTransportFactory
    {
        public WakingTransport? Transport { get; private set; }

        public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
        {
            Transport = new WakingTransport();
            transport = Transport;
            startupError = default;
            socketError = SocketError.Success;
            return true;
        }
    }

    private sealed class WakingTransport : IDatagramTransport
    {
        public Action? Signal { get; private set; }

        public SystemAddress LocalAddress { get; } = new(IPAddress.Loopback, 40000);

        public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote)
        {
            length = 0;
            remote = default;
            return false;
        }

        public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0) => true;

        public void SetReadySignal(Action signal) => Signal = signal;

        public void Dispose()
        {
        }
    }
}
