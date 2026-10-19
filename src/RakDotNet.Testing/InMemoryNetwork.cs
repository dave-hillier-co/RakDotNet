using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Testing;

/// <summary>
/// A hand-written datagram fabric standing in for UDP, so several real <see cref="RakPeer"/>s run sociable tests in one
/// process with no sockets. Pass it as <see cref="RakPeerOptions.Transport"/> to every peer.
/// </summary>
/// <remarks>
/// <para>
/// Loss, delay, duplication and reordering are drawn from <see cref="InMemoryNetworkOptions.Seed"/> and scheduled on
/// <see cref="TimeProvider"/>, so with <see cref="PeerThreading.Manual"/> and a fake time provider a run is fully
/// deterministic. A binding on port 0 is given the next free port; binding a port twice fails with
/// <see cref="StartupError.SocketPortAlreadyInUse"/>.
/// </para>
/// <para>
/// Addressing follows IPv4 UDP on one host: a port is bound once whatever its address, a binding on 0.0.0.0 receives
/// datagrams sent to any address on its port and sends from 127.0.0.1, and a datagram to a port nobody bound is dropped.
/// Each binding queues at most <see cref="SocketBinding.ReceiveBufferSize"/> bytes (1 MiB by default) and drops what
/// would overflow it, as a socket's receive buffer does, and a receive into a smaller buffer truncates the datagram.
/// IPv6 is refused with <see cref="StartupError.SocketFamilyNotSupported"/>, as the UDP transport refuses it.
/// </para>
/// </remarks>
public sealed class InMemoryNetwork : IDatagramTransportFactory
{
    private const int FirstEphemeralPort = 49152;
    private const int DefaultReceiveBufferBytes = 1 << 20;

    private readonly Lock _gate = new();
    private readonly Dictionary<int, Endpoint> _bound = [];
    private readonly Random _random;
    private readonly PriorityQueue<Scheduled, (long Due, long Sequence)> _scheduled = new();
    private readonly Dictionary<SystemAddress, Scheduled> _heldBack = [];
    private readonly long _start;
    private ITimer? _timer;
    private long _sequence;
    private int _nextPort = FirstEphemeralPort;
    private long _sent;
    private long _dropped;

    public InMemoryNetwork(TimeProvider timeProvider, InMemoryNetworkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= new InMemoryNetworkOptions();
        ValidateProbability(options.PacketLoss, nameof(options));
        ValidateProbability(options.Duplication, nameof(options));
        ValidateProbability(options.Reordering, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinDelay, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.DelayVariance, TimeSpan.Zero, nameof(options));
        TimeProvider = timeProvider;
        Options = options;
        _random = new Random(options.Seed);
        _start = timeProvider.GetTimestamp();
    }

    /// <summary>The clock delayed datagrams are scheduled on; give the peers the same one.</summary>
    public TimeProvider TimeProvider { get; }

    public InMemoryNetworkOptions Options { get; }

    /// <summary>Datagrams handed to the network by any transport.</summary>
    public long DatagramsSent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Datagrams the network dropped: by <see cref="InMemoryNetworkOptions.PacketLoss"/>, to an unbound address, or
    /// because the receiver's queue was full.
    /// </summary>
    public long DatagramsDropped => Interlocked.Read(ref _dropped);

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        ArgumentNullException.ThrowIfNull(binding);
        transport = null;
        startupError = default;
        socketError = SocketError.Success;
        var endPoint = binding.LocalEndPoint;
        if (endPoint.AddressFamily != AddressFamily.InterNetwork)
        {
            startupError = StartupError.SocketFamilyNotSupported;
            socketError = SocketError.AddressFamilyNotSupported;
            return false;
        }

        lock (_gate)
        {
            var port = endPoint.Port;
            if (port == 0 && !TryTakeFreePort(out port))
            {
                startupError = StartupError.SocketFailedToBind;
                socketError = SocketError.AddressNotAvailable;
                return false;
            }
            if (_bound.ContainsKey(port))
            {
                startupError = StartupError.SocketPortAlreadyInUse;
                socketError = SocketError.AddressAlreadyInUse;
                return false;
            }
            var endpoint = new Endpoint(this, new SystemAddress(endPoint.Address, port), binding.ReceiveBufferSize ?? DefaultReceiveBufferBytes);
            _bound.Add(port, endpoint);
            transport = endpoint;
            return true;
        }
    }

    private long Now => TimeProvider.GetElapsedTime(_start).Ticks;

    private static void ValidateProbability(double value, string parameter)
    {
        if (!(value >= 0 && value <= 1))
            throw new ArgumentOutOfRangeException(parameter, value, "A probability is between 0 and 1.");
    }

    private bool TryTakeFreePort(out int port)
    {
        for (var tried = 0; tried <= ushort.MaxValue - FirstEphemeralPort; tried++)
        {
            port = _nextPort;
            _nextPort = _nextPort == ushort.MaxValue ? FirstEphemeralPort : _nextPort + 1;
            if (!_bound.ContainsKey(port))
                return true;
        }
        port = 0;
        return false;
    }

    private void Send(Endpoint from, ReadOnlySpan<byte> datagram, SystemAddress to)
    {
        var source = from.LocalAddress.IsLoopback || IsAny(from.LocalAddress)
            ? new SystemAddress(IPAddress.Loopback, from.LocalAddress.Port)
            : from.LocalAddress;
        List<Endpoint>? signalled = null;
        lock (_gate)
        {
            _sent++;
            if (Draw(Options.PacketLoss))
            {
                _dropped++;
                return;
            }
            var copies = Draw(Options.Duplication) ? 2 : 1;
            var delay = Options.MinDelay + (Options.DelayVariance * (Options.DelayVariance > TimeSpan.Zero ? _random.NextDouble() : 0));
            var datagramCopy = datagram.ToArray();
            for (var i = 0; i < copies; i++)
            {
                var scheduled = new Scheduled(datagramCopy, source, to, Now + delay.Ticks);
                if (_heldBack.Remove(to, out var held))
                {
                    // The held datagram goes out right behind this one.
                    Dispatch(scheduled, ref signalled);
                    Dispatch(held with { Due = Math.Max(held.Due, scheduled.Due) }, ref signalled);
                }
                else if (Draw(Options.Reordering))
                {
                    _heldBack[to] = scheduled;
                }
                else
                {
                    Dispatch(scheduled, ref signalled);
                }
            }
        }
        Signal(signalled);
    }

    private bool Draw(double probability) => probability > 0 && _random.NextDouble() < probability;

    private void Dispatch(Scheduled datagram, ref List<Endpoint>? signalled)
    {
        if (datagram.Due <= Now)
        {
            Deliver(datagram, ref signalled);
            return;
        }
        _scheduled.Enqueue(datagram, (datagram.Due, _sequence++));
        if (_scheduled.Peek() == datagram)
            Arm();
    }

    private void Deliver(Scheduled datagram, ref List<Endpoint>? signalled)
    {
        var to = datagram.To;
        if (to.AddressFamily != AddressFamily.InterNetwork || !_bound.TryGetValue(to.Port, out var endpoint)
            || !(IsAny(endpoint.LocalAddress) || IsAny(to) || endpoint.LocalAddress.EqualsIgnoringPort(to)))
        {
            _dropped++;
            return;
        }
        if (!endpoint.Enqueue(datagram.Data, datagram.From))
        {
            _dropped++;
            return;
        }
        (signalled ??= []).Add(endpoint);
    }

    private void Arm()
    {
        var delay = TimeSpan.FromTicks(Math.Max(0, _scheduled.Peek().Due - Now));
        _timer ??= TimeProvider.CreateTimer(static state => ((InMemoryNetwork)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        List<Endpoint>? signalled = null;
        lock (_gate)
        {
            var now = Now;
            while (_scheduled.TryPeek(out var next, out var key) && key.Due <= now)
            {
                _scheduled.Dequeue();
                Deliver(next, ref signalled);
            }
            if (_scheduled.Count > 0)
                Arm();
        }
        Signal(signalled);
    }

    private static void Signal(List<Endpoint>? endpoints)
    {
        if (endpoints is null)
            return;
        foreach (var endpoint in endpoints)
            endpoint.RaiseReady();
    }

    private void Unbind(Endpoint endpoint)
    {
        lock (_gate)
        {
            if (_bound.TryGetValue(endpoint.LocalAddress.Port, out var bound) && bound == endpoint)
                _bound.Remove(endpoint.LocalAddress.Port);
        }
    }

    private static bool IsAny(SystemAddress address) => address.EqualsIgnoringPort(new SystemAddress(IPAddress.Any, 0));

    private sealed record Scheduled(byte[] Data, SystemAddress From, SystemAddress To, long Due);

    /// <summary>One bound port.</summary>
    private sealed class Endpoint(InMemoryNetwork network, SystemAddress localAddress, int receiveBufferBytes) : IDatagramTransport
    {
        private readonly Lock _gate = new();
        private readonly Queue<(byte[] Data, SystemAddress From)> _inbound = new();
        private int _queuedBytes;
        private Action? _signal;
        private bool _disposed;

        public SystemAddress LocalAddress { get; } = localAddress;

        public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote)
        {
            lock (_gate)
            {
                if (!_inbound.TryDequeue(out var datagram))
                {
                    length = 0;
                    remote = default;
                    return false;
                }
                _queuedBytes -= datagram.Data.Length;
                length = Math.Min(buffer.Length, datagram.Data.Length);
                datagram.Data.AsSpan(0, length).CopyTo(buffer);
                remote = datagram.From;
                return true;
            }
        }

        public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0)
        {
            if (Volatile.Read(ref _disposed))
                return false;
            network.Send(this, datagram, remote);
            return true;
        }

        public void SetReadySignal(Action signal) => _signal = signal;

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _inbound.Clear();
                _queuedBytes = 0;
            }
            network.Unbind(this);
        }

        /// <summary>False when the receive buffer cannot take it.</summary>
        public bool Enqueue(byte[] data, SystemAddress from)
        {
            lock (_gate)
            {
                if (_disposed || _queuedBytes + data.Length > receiveBufferBytes)
                    return false;
                _inbound.Enqueue((data, from));
                _queuedBytes += data.Length;
                return true;
            }
        }

        public void RaiseReady() => _signal?.Invoke();
    }
}

/// <summary>The impairments of an <see cref="InMemoryNetwork"/>. The default is a perfect network.</summary>
public sealed record InMemoryNetworkOptions
{
    /// <summary>Seeds every random draw.</summary>
    public int Seed { get; init; }

    /// <summary>Probability, 0 to 1, that a datagram is dropped.</summary>
    public double PacketLoss { get; init; }

    /// <summary>Delay added to every datagram.</summary>
    public TimeSpan MinDelay { get; init; }

    /// <summary>Maximum extra delay drawn per datagram.</summary>
    public TimeSpan DelayVariance { get; init; }

    /// <summary>Probability, 0 to 1, that a datagram is delivered twice.</summary>
    public double Duplication { get; init; }

    /// <summary>
    /// Probability, 0 to 1, that a datagram is held back behind the next one to the same address. A held datagram waits
    /// for that next datagram however long it takes.
    /// </summary>
    public double Reordering { get; init; }
}
