using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// UDPForwarder: forwards UDP datagrams between two systems through a socket of its own, independent of RakNet's
/// protocol. UDPProxyServer and Router2 use it; it can also be used alone, as Samples/UDPForwarder does.
/// </summary>
/// <remarks>
/// <para>
/// Ported from UDPForwarder.cpp. <see cref="StartForwarding"/> binds a socket for a source and a destination and
/// returns its port. A datagram arriving on that socket from the source is sent on to the destination and one from the
/// destination to the source. Each side is first matched by its address alone, since a router may change the port, and
/// the first datagram from a side fixes its address and port for the rest of the entry's life: datagrams from the
/// other side go to that address and port from then on, and datagrams from anywhere else are dropped. An entry that
/// forwards nothing for its timeout is removed and its socket closed.
/// </para>
/// <para>
/// RakNet runs the forwarding on a thread of its own; <see cref="PeerThreading.Background"/> does the same, woken by
/// the sockets and every 30 ms for timeouts. <see cref="PeerThreading.Manual"/> starts no thread and forwards when
/// <see cref="Update"/> is called. RakNet's limits are kept: an entry is refused only once more than
/// <see cref="MaxForwardEntries"/> are in use, and <see cref="StopForwarding"/> takes effect on the next update. Times
/// are RakNet::TimeMS, 32-bit milliseconds, on the <see cref="TimeProvider"/>.
/// </para>
/// <para>
/// Sockets are bound through an <see cref="IDatagramTransportFactory"/>, as for a <see cref="RakPeer"/>. A socket
/// bound by <see cref="UdpTransportFactory"/> sends itself a test datagram, which RakNet's raw sockets do not; datagrams
/// from the socket's own address are therefore not forwarded.
/// </para>
/// <para>
/// An entry whose socket fails with a <see cref="SocketException"/> is removed and its socket closed; the other entries
/// keep forwarding.
/// </para>
/// <para>Members may be called from any thread.</para>
/// </remarks>
public sealed class UdpForwarder : IDisposable
{
    /// <summary>DEFAULT_MAX_FORWARD_ENTRIES (UDPForwarder.cpp:29).</summary>
    public const int DefaultMaxForwardEntries = 64;

    /// <summary>MAXIMUM_MTU_SIZE: the largest datagram forwarded whole.</summary>
    private const int MaximumMtuSize = 1492;

    /// <summary>UDP_FORWARDER_MAXIMUM_TIMEOUT (UDPProxyCommon.h:64): ten minutes.</summary>
    public static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(60000 * 10);

    // RakSleep(30) between updates when idle (UDPForwarder.cpp:617-622).
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(30);

    private readonly Lock _gate = new();
    private readonly IDatagramTransportFactory _transportFactory;
    private readonly TimeProvider _timeProvider;
    private readonly PeerThreading _threading;
    private readonly long _origin;
    private readonly List<ForwardEntry> _forwardList = [];
    private readonly Queue<(SystemAddress Source, SystemAddress Destination)> _stopForwardingCommands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly byte[] _buffer = new byte[MaximumMtuSize];
    private int _maxForwardEntries = DefaultMaxForwardEntries;
    private bool _isRunning;
    private Thread? _thread;
    private volatile bool _stopThread;

    /// <param name="transportFactory">Binds the forwarding sockets; <see cref="UdpTransportFactory.Instance"/> when null.</param>
    /// <param name="timeProvider">The clock for timeouts; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="threading">Whether a thread of the forwarder's own runs it, or the caller through <see cref="Update"/>.</param>
    public UdpForwarder(IDatagramTransportFactory? transportFactory = null, TimeProvider? timeProvider = null,
                        PeerThreading threading = PeerThreading.Background)
    {
        _transportFactory = transportFactory ?? UdpTransportFactory.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _threading = threading;
        _origin = _timeProvider.GetTimestamp();
    }

    /// <summary>Whether <see cref="Startup"/> was called and <see cref="Shutdown"/> was not since.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _isRunning;
        }
    }

    /// <summary>
    /// SetMaxForwardEntries / GetMaxForwardEntries: how many entries may be in use before more are refused; 64 by
    /// default. An entry is refused when more than this many are in use, so one more than this can exist.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 1 and 32766, as RakNet asserts.</exception>
    public int MaxForwardEntries
    {
        get
        {
            lock (_gate)
                return _maxForwardEntries;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, 65535 / 2);
            lock (_gate)
                _maxForwardEntries = value;
        }
    }

    /// <summary>GetUsedForwardEntries: the entries forwarding now.</summary>
    public int UsedForwardEntries
    {
        get
        {
            lock (_gate)
                return _forwardList.Count;
        }
    }

    /// <summary>Startup: starts forwarding, with a thread in <see cref="PeerThreading.Background"/>. Again is a no-op.</summary>
    public void Startup()
    {
        lock (_gate)
        {
            if (_isRunning)
                return;
            _isRunning = true;
            if (_threading != PeerThreading.Background)
                return;
            _stopThread = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "UdpForwarder" };
            _thread.Start();
        }
    }

    /// <summary>Shutdown: stops forwarding and closes every forwarding socket. Without <see cref="Startup"/>, a no-op.</summary>
    public void Shutdown()
    {
        Thread? thread;
        lock (_gate)
        {
            if (!_isRunning)
                return;
            _isRunning = false;
            thread = _thread;
            _thread = null;
            _stopThread = true;
        }
        _wake.Set();
        if (thread is not null && thread != Thread.CurrentThread)
            thread.Join();
        lock (_gate)
        {
            foreach (var entry in _forwardList)
                entry.Socket.Dispose();
            _forwardList.Clear();
            _stopForwardingCommands.Clear();
        }
    }

    /// <inheritdoc cref="Shutdown"/>
    public void Dispose()
    {
        Shutdown();
        _wake.Dispose();
    }

    /// <summary>
    /// StartForwarding: forwards datagrams from <paramref name="source"/> to <paramref name="destination"/> and back,
    /// through a new socket bound on <paramref name="forceHostAddress"/> (every IPv4 address when null or empty) and an
    /// ephemeral port, until nothing is forwarded for <paramref name="timeoutOnNoData"/>. A pair already forwarded in
    /// either direction is not bound again.
    /// </summary>
    /// <remarks>
    /// RakNet queues the request for its thread and waits for the answer; here it is answered on the caller, with the
    /// same checks in the same order (UDPForwarder.cpp:117-178, 381-516).
    /// </remarks>
    public UdpForwarding StartForwarding(SystemAddress source, SystemAddress destination, TimeSpan timeoutOnNoData, string? forceHostAddress = null)
    {
        var timeoutMs = timeoutOnNoData <= TimeSpan.Zero ? 0 : Math.Min(timeoutOnNoData.TotalMilliseconds, uint.MaxValue);
        if (timeoutMs < 1 || timeoutOnNoData > MaximumTimeout || !source.IsAssigned || !destination.IsAssigned)
            return new UdpForwarding(UdpForwarderResult.InvalidParameters, 0, null);

        lock (_gate)
        {
            if (!_isRunning)
                return new UdpForwarding(UdpForwarderResult.NotRunning, 0, null);
            if (_forwardList.Count > _maxForwardEntries)
                return new UdpForwarding(UdpForwarderResult.NoSockets, 0, null);
            if (IndexOf(source, destination) >= 0)
                return new UdpForwarding(UdpForwarderResult.ForwardingAlreadyExists, 0, null);

            // inet_addr of the forced address; one that is not an address cannot be bound.
            var bindAddress = IPAddress.Any;
            if (!string.IsNullOrEmpty(forceHostAddress) && !IPAddress.TryParse(forceHostAddress, out bindAddress!))
                return new UdpForwarding(UdpForwarderResult.BindFailed, 0, null);
            if (!_transportFactory.TryBind(new SocketBinding(new IPEndPoint(bindAddress, 0)), out var socket, out _, out _))
                return new UdpForwarding(UdpForwarderResult.BindFailed, 0, null);

            var entry = new ForwardEntry(source, destination, (uint)timeoutMs, socket, NowMs());
            _forwardList.Add(entry);
            if (_threading == PeerThreading.Background)
            {
                socket.SetReadySignal(Wake);
                Wake();
            }
            return new UdpForwarding(UdpForwarderResult.Success, socket.LocalAddress.Port, socket);
        }
    }

    /// <summary>StopForwarding: removes the entry for the pair, in either direction, at the next update.</summary>
    public void StopForwarding(SystemAddress source, SystemAddress destination)
    {
        lock (_gate)
            _stopForwardingCommands.Enqueue((source, destination));
        Wake();
    }

    /// <summary>
    /// UpdateUDPForwarder (UDPForwarder.cpp:381-597): applies stops, removes entries that timed out and forwards what
    /// has arrived. Only in <see cref="PeerThreading.Manual"/>; the forwarder's thread calls it otherwise.
    /// </summary>
    /// <exception cref="InvalidOperationException">The forwarder runs on its own thread.</exception>
    public void Update()
    {
        if (_threading != PeerThreading.Manual)
            throw new InvalidOperationException("Update runs the forwarder only in PeerThreading.Manual; its thread runs it otherwise.");
        UpdateCore();
    }

    private void Run()
    {
        while (!_stopThread)
        {
            UpdateCore();
            _wake.WaitOne(IdleWait);
        }
    }

    private void Wake()
    {
        try
        {
            _wake.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void UpdateCore()
    {
        lock (_gate)
        {
            if (!_isRunning)
                return;
            var curTime = NowMs();

            while (_stopForwardingCommands.TryDequeue(out var stop))
            {
                var i = IndexOf(stop.Source, stop.Destination);
                if (i < 0)
                    continue;
                var entry = _forwardList[i];
                // RemoveAtIndexFast: the last entry takes the removed one's place.
                _forwardList[i] = _forwardList[^1];
                _forwardList.RemoveAt(_forwardList.Count - 1);
                entry.Socket.Dispose();
            }

            // The comparison with the last forward accounts for the timestamp wrapping.
            for (var i = 0; i < _forwardList.Count;)
            {
                var entry = _forwardList[i];
                if (curTime > entry.TimeLastDatagramForwarded && curTime > unchecked(entry.TimeLastDatagramForwarded + entry.TimeoutOnNoDataMs))
                {
                    entry.Socket.Dispose();
                    _forwardList.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            for (var i = 0; i < _forwardList.Count;)
            {
                var entry = _forwardList[i];
                try
                {
                    ReceiveFrom(curTime, entry);
                    i++;
                }
                catch (SocketException)
                {
                    // RakNet prints the error and carries on (UDPForwarder.cpp:233-246), and the entry, forwarding
                    // nothing, times out. A transport that has failed fails every receive, so its entry is removed now
                    // and the rest keep forwarding, on the forwarder's thread too.
                    entry.Socket.Dispose();
                    _forwardList.RemoveAt(i);
                }
            }
        }
    }

    // RecvFrom (UDPForwarder.cpp:180-380), for every datagram waiting.
    private void ReceiveFrom(uint curTime, ForwardEntry entry)
    {
        while (entry.Socket.TryReceive(_buffer, out var length, out var receivedAddress))
        {
            if (length <= 0 || IsOwnAddress(entry.Socket.LocalAddress, receivedAddress))
                continue;

            var confirmed1 = entry.Addr1Confirmed.IsAssigned;
            var confirmed2 = entry.Addr2Confirmed.IsAssigned;
            var matchConfirmed1 = confirmed1 && entry.Addr1Confirmed == receivedAddress;
            var matchConfirmed2 = confirmed2 && entry.Addr2Confirmed == receivedAddress;
            var matchUnconfirmed1 = entry.Addr1Unconfirmed.EqualsIgnoringPort(receivedAddress);
            var matchUnconfirmed2 = entry.Addr2Unconfirmed.EqualsIgnoringPort(receivedAddress);

            SystemAddress forwardTarget;
            if (matchConfirmed1 || (!matchConfirmed2 && !confirmed1 && matchUnconfirmed1))
            {
                // Forward to addr2.
                if (!entry.Addr1Confirmed.IsAssigned)
                    entry.Addr1Confirmed = receivedAddress;
                forwardTarget = entry.Addr2Confirmed.IsAssigned ? entry.Addr2Confirmed : entry.Addr2Unconfirmed;
            }
            else if (matchConfirmed2 || (!confirmed2 && matchUnconfirmed2))
            {
                // Forward to addr1.
                if (!entry.Addr2Confirmed.IsAssigned)
                    entry.Addr2Confirmed = receivedAddress;
                forwardTarget = entry.Addr1Confirmed.IsAssigned ? entry.Addr1Confirmed : entry.Addr1Unconfirmed;
            }
            else
            {
                continue;
            }

            entry.Socket.TrySend(_buffer.AsSpan(0, length), forwardTarget);
            entry.TimeLastDatagramForwarded = curTime;
        }
    }

    // The socket's own test datagram: from its address, or from 127.0.0.1 when it is bound to every address.
    private static bool IsOwnAddress(SystemAddress local, SystemAddress received) =>
        received.Port == local.Port
        && (received.EqualsIgnoringPort(local)
            || (local.EqualsIgnoringPort(new SystemAddress(IPAddress.Any, 0)) && received.EqualsIgnoringPort(new SystemAddress(IPAddress.Loopback, 0))));

    private int IndexOf(SystemAddress source, SystemAddress destination) =>
        _forwardList.FindIndex(e => (e.Addr1Unconfirmed == source && e.Addr2Unconfirmed == destination)
                                    || (e.Addr1Unconfirmed == destination && e.Addr2Unconfirmed == source));

    // RakNet::GetTimeMS: 32-bit milliseconds.
    private uint NowMs() => unchecked((uint)(long)_timeProvider.GetElapsedTime(_origin).TotalMilliseconds);

    /// <summary>UDPForwarder::ForwardEntry (UDPForwarder.h:94-103).</summary>
    private sealed class ForwardEntry(SystemAddress source, SystemAddress destination, uint timeoutOnNoDataMs, IDatagramTransport socket, uint created)
    {
        public SystemAddress Addr1Unconfirmed { get; } = source;

        public SystemAddress Addr2Unconfirmed { get; } = destination;

        public SystemAddress Addr1Confirmed { get; set; }

        public SystemAddress Addr2Confirmed { get; set; }

        public uint TimeLastDatagramForwarded { get; set; } = created;

        public uint TimeoutOnNoDataMs { get; } = timeoutOnNoDataMs;

        public IDatagramTransport Socket { get; } = socket;
    }
}
