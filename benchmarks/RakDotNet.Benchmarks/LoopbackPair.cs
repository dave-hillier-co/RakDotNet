using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// A client and a server <see cref="RakPeer"/> connected over UDP on 127.0.0.1, as RakNet's
/// Samples/LoopbackPerformanceTest runs its source and destination: the client sends, the server reads every message.
/// </summary>
internal sealed class LoopbackPair : IDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly PeerThreading _threading;
    private readonly RakConnection _toServer;

    public LoopbackPair(PeerThreading threading)
    {
        _threading = threading;
        var loopback = new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0));
        Server = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            MaxIncomingConnections = 1,
            Bindings = [loopback],
            Threading = threading,
        });
        Client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1, Bindings = [loopback], Threading = threading });

        var result = Client.Connect(Server.Sockets[0].LocalAddress);
        if (result != ConnectAttemptResult.Started)
            throw new InvalidOperationException($"Connect returned {result}.");
        RakConnection? toServer = null;
        var serverConnected = false;
        var deadline = DateTime.UtcNow + ConnectTimeout;
        while (toServer is null || !serverConnected)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The loopback pair did not connect.");
            Pump();
            while (Client.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Connected)
                    toServer = e.Connection;
            }
            while (Server.Events.TryRead(out var e))
                serverConnected |= e.Kind == PeerEventKind.Connected;
            Thread.Yield();
        }
        _toServer = toServer;
    }

    public RakPeer Server { get; }

    public RakPeer Client { get; }

    /// <summary>
    /// Sends <paramref name="count"/> copies of <paramref name="message"/> from the client, then returns once the
    /// server has read every one.
    /// </summary>
    public void Transfer(ReadOnlySpan<byte> message, int count, Reliability reliability)
    {
        for (var i = 0; i < count; i++)
        {
            var sent = _toServer.Send(message, reliability);
            if (!sent.IsQueued)
                throw new InvalidOperationException($"Send returned {sent.Status}.");
        }

        var received = 0;
        var spinner = default(SpinWait);
        while (received < count)
        {
            Pump();
            var any = false;
            while (Server.Events.TryRead(out var e))
            {
                any = true;
                switch (e.Kind)
                {
                    case PeerEventKind.Message when e.Id == MessageId.UserPacketEnum:
                        received++;
                        break;
                    case PeerEventKind.Disconnected:
                        throw new InvalidOperationException($"The client disconnected: {e.DisconnectReason}.");
                }
            }
            while (Client.Events.TryRead(out _))
            {
            }
            if (any)
                spinner.Reset();
            else
                spinner.SpinOnce(sleep1Threshold: -1);
        }
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }

    // In Manual mode the caller runs both I/O cycles; in Background mode each peer's loop thread does.
    private void Pump()
    {
        if (_threading != PeerThreading.Manual)
            return;
        Client.Update();
        Server.Update();
    }
}
