using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Interop.Tests.Harness;

/// <param name="Index">Position in the recording, across both directions.</param>
/// <param name="Elapsed">Time since the first recorded datagram.</param>
/// <param name="Client">The client-side endpoint the datagram came from or went to.</param>
/// <param name="Fault">What the proxy did to it; dropped datagrams are recorded too.</param>
public sealed record ProxyDatagram(long Index, TimeSpan Elapsed, ProxyDirection Direction, IPEndPoint Client, byte[] Payload, FaultDecision Fault);

/// <summary>
/// A UDP proxy between client peers and one server. Clients send to <see cref="ListenEndPoint"/>; each distinct
/// client endpoint gets its own upstream socket, so the server sees one address per client. Every datagram is
/// recorded, and <see cref="Faults"/> can drop, duplicate and delay datagrams deterministically.
/// </summary>
public sealed class UdpProxy : IAsyncDisposable
{
    private const int MaxDatagramSize = 65536;

    private readonly Socket _listen;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<IPEndPoint, Upstream> _upstreams = new();
    private readonly List<ProxyDatagram> _datagrams = [];
    private readonly Lock _recordLock = new();
    private readonly PriorityQueue<Delayed, (long Due, long Order)> _delayed = new();
    private readonly Lock _delayLock = new();
    private readonly ITimer _delayTimer;
    private readonly List<Task> _loops = [];
    private long _firstTimestamp = -1;
    private long _aToBCount;
    private long _bToACount;
    private long _delayedOrder;
    private long _timerDue = long.MaxValue;
    private volatile FaultOptions _faults;

    private UdpProxy(IPEndPoint server, FaultOptions faults, TimeProvider time)
    {
        ServerEndPoint = server;
        _faults = faults;
        _time = time;
        _listen = Bind(server.AddressFamily);
        ListenEndPoint = (IPEndPoint)_listen.LocalEndPoint!;
        _delayTimer = time.CreateTimer(_ => ReleaseDue(), null, System.Threading.Timeout.InfiniteTimeSpan, System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>Where clients send; the server's replies come back from here.</summary>
    public IPEndPoint ListenEndPoint { get; }

    public IPEndPoint ServerEndPoint { get; }

    /// <summary>Applies to datagrams received from now on. Decision indices keep counting from proxy start.</summary>
    public FaultOptions Faults
    {
        get => _faults;
        set => _faults = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>A snapshot of every datagram received so far, in arrival order.</summary>
    public IReadOnlyList<ProxyDatagram> Datagrams
    {
        get
        {
            lock (_recordLock)
            {
                return _datagrams.ToArray();
            }
        }
    }

    /// <summary>The address the server sees for each client endpoint.</summary>
    public IReadOnlyDictionary<IPEndPoint, IPEndPoint> UpstreamEndPoints =>
        _upstreams.ToDictionary(pair => pair.Key, pair => (IPEndPoint)pair.Value.Socket.LocalEndPoint!);

    public static UdpProxy Start(IPEndPoint server, FaultOptions? faults = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var proxy = new UdpProxy(server, faults ?? FaultOptions.None, timeProvider ?? TimeProvider.System);
        lock (proxy._loops)
        {
            proxy._loops.Add(Task.Run(proxy.ListenLoopAsync));
        }
        return proxy;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _delayTimer.DisposeAsync().ConfigureAwait(false);
        _listen.Dispose();
        foreach (var upstream in _upstreams.Values)
        {
            upstream.Socket.Dispose();
        }

        Task[] loops;
        lock (_loops)
        {
            loops = [.. _loops];
        }
        await Task.WhenAll(loops).ConfigureAwait(false);
        _stop.Dispose();
    }

    private static Socket Bind(AddressFamily family)
    {
        var socket = new Socket(family, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(family == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
        return socket;
    }

    private async Task ListenLoopAsync()
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint any = new IPEndPoint(ListenEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (await ReceiveAsync(_listen, buffer, any).ConfigureAwait(false) is { } received)
        {
            var client = (IPEndPoint)received.RemoteEndPoint;
            var upstream = _upstreams.GetOrAdd(client, CreateUpstream);
            Forward(ProxyDirection.AToB, client, buffer.AsSpan(0, received.ReceivedBytes).ToArray(), upstream.Socket, ServerEndPoint);
        }
    }

    private Upstream CreateUpstream(IPEndPoint client)
    {
        var upstream = new Upstream(Bind(ServerEndPoint.AddressFamily));
        lock (_loops)
        {
            _loops.Add(Task.Run(() => UpstreamLoopAsync(client, upstream.Socket)));
        }
        return upstream;
    }

    private async Task UpstreamLoopAsync(IPEndPoint client, Socket socket)
    {
        var buffer = new byte[MaxDatagramSize];
        EndPoint any = new IPEndPoint(ServerEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (await ReceiveAsync(socket, buffer, any).ConfigureAwait(false) is { } received)
        {
            Forward(ProxyDirection.BToA, client, buffer.AsSpan(0, received.ReceivedBytes).ToArray(), _listen, client);
        }
    }

    private async Task<SocketReceiveFromResult?> ReceiveAsync(Socket socket, byte[] buffer, EndPoint any)
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                return await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (SocketException)
            {
                // ICMP port unreachable surfaces as ConnectionReset on some platforms; keep proxying.
            }
        }
        return null;
    }

    private void Forward(ProxyDirection direction, IPEndPoint client, byte[] payload, Socket via, IPEndPoint to)
    {
        var index = direction == ProxyDirection.AToB ? Interlocked.Increment(ref _aToBCount) - 1 : Interlocked.Increment(ref _bToACount) - 1;
        var fault = _faults.Decide(direction, index);
        Record(direction, client, payload, fault);
        if (fault.Drop)
        {
            return;
        }

        var copies = fault.Duplicate ? 2 : 1;
        if (fault.Delay <= TimeSpan.Zero)
        {
            for (var i = 0; i < copies; i++)
            {
                Send(via, payload, to);
            }
            return;
        }

        var due = _time.GetTimestamp() + (long)(fault.Delay.TotalSeconds * _time.TimestampFrequency);
        lock (_delayLock)
        {
            for (var i = 0; i < copies; i++)
            {
                _delayed.Enqueue(new Delayed(via, payload, to), (due, _delayedOrder++));
            }
            ScheduleTimer();
        }
    }

    private void Record(ProxyDirection direction, IPEndPoint client, byte[] payload, FaultDecision fault)
    {
        var now = _time.GetTimestamp();
        lock (_recordLock)
        {
            if (_firstTimestamp < 0)
            {
                _firstTimestamp = now;
            }
            _datagrams.Add(new ProxyDatagram(_datagrams.Count, _time.GetElapsedTime(_firstTimestamp, now), direction, client, payload, fault));
        }
    }

    // Caller holds _delayLock.
    private void ScheduleTimer()
    {
        if (!_delayed.TryPeek(out _, out var next) || next.Due >= _timerDue)
        {
            return;
        }

        _timerDue = next.Due;
        var wait = _time.GetElapsedTime(_time.GetTimestamp(), next.Due);
        if (_stop.IsCancellationRequested)
        {
            return;
        }
        try
        {
            _delayTimer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, System.Threading.Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ReleaseDue()
    {
        lock (_delayLock)
        {
            _timerDue = long.MaxValue;
            var now = _time.GetTimestamp();
            while (_delayed.TryPeek(out var datagram, out var key) && key.Due <= now)
            {
                _delayed.Dequeue();
                Send(datagram.Via, datagram.Payload, datagram.To);
            }
            ScheduleTimer();
        }
    }

    private static void Send(Socket via, byte[] payload, IPEndPoint to)
    {
        try
        {
            via.SendTo(payload, SocketFlags.None, to);
        }
        catch (SocketException)
        {
            // The destination went away; a real network would lose the datagram too.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed record Upstream(Socket Socket);

    private sealed record Delayed(Socket Via, byte[] Payload, IPEndPoint To);
}
