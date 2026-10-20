using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Transport;

/// <summary>
/// One bound UDP socket. In Manual mode it receives without blocking with
/// <c>Socket.ReceiveFrom(Span, SocketFlags, SocketAddress)</c> while <c>Available</c> says a datagram waits. In
/// Background mode every receive is a <c>ReceiveFromAsync</c> into a slot: one that completes at once is a waiting
/// datagram, taken straight away, and the first that does not stays outstanding as the wake signal.
/// </summary>
/// <remarks>
/// While the asynchronous receive is outstanding no synchronous receive is made, because the runtime serialises
/// receives on a socket and a synchronous one would wait behind it. Allocation-free in steady state: the receive and
/// send <see cref="SocketAddress"/>es are reused, and the pending receive is a <see cref="ValueTask{TResult}"/> completed
/// through a cached continuation.
/// </remarks>
internal sealed class UdpDatagramTransport : IDatagramTransport
{
    // Larger than any datagram, so an oversized one is read whole and then ignored rather than truncated.
    private const int SlotSize = 64 * 1024;

    // Linux <netinet/in.h>: IP_MTU_DISCOVER at IPPROTO_IP and IPV6_MTU_DISCOVER at IPPROTO_IPV6, which
    // Socket.DontFragment sets on Linux.
    private const int IpProtocolIp = 0;
    private const int IpMtuDiscover = 10;
    private const int IpProtocolIpv6 = 41;
    private const int Ipv6MtuDiscover = 23;

    private readonly Socket _socket;
    private readonly SocketAddress _receiveAddress;
    private readonly SocketAddress _slotAddress;
    private readonly SocketAddress _sendAddress;
    private readonly byte[] _slot = GC.AllocateUninitializedArray<byte>(SlotSize, pinned: true);
    private readonly Lock _sendGate = new();
    private readonly Action _onSlotReceived;
    private ConfiguredValueTaskAwaitable<int>.ConfiguredValueTaskAwaiter _pending;
    private Action? _signal;
    private int _armed;
    private volatile bool _slotReady;
    private int _slotLength;
    private SystemAddress _slotRemote;
    private volatile SocketException? _fatal;
    private volatile bool _disposed;
    private bool _dontFragmentSupported = true;

    private UdpDatagramTransport(Socket socket)
    {
        _socket = socket;
        LocalAddress = (IPEndPoint)socket.LocalEndPoint!;
        _receiveAddress = new SocketAddress(socket.AddressFamily);
        _slotAddress = new SocketAddress(socket.AddressFamily);
        _sendAddress = new SocketAddress(socket.AddressFamily);
        _onSlotReceived = OnSlotReceived;
    }

    public SystemAddress LocalAddress { get; }

    internal int ReceiveBufferSize => _socket.ReceiveBufferSize;

    internal int SendBufferSize => _socket.SendBufferSize;

    /// <summary>The raw IP_MTU_DISCOVER (or IPV6_MTU_DISCOVER) value. Linux only.</summary>
    internal int PathMtuDiscovery
    {
        get
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            var (level, name) = MtuDiscoverOption;
            _socket.GetRawSocketOption(level, name, value);
            return BitConverter.ToInt32(value);
        }
    }

    private (int Level, int Name) MtuDiscoverOption =>
        _socket.AddressFamily == AddressFamily.InterNetworkV6 ? (IpProtocolIpv6, Ipv6MtuDiscover) : (IpProtocolIp, IpMtuDiscover);

    /// <summary>
    /// Binds a UDP socket as RakNet's RNS2_Berkley::BindShared does (RakNetSocket2_Berkley.cpp:160-263,
    /// RakNetSocket2.cpp:303-332): the binding's buffer sizes, SO_BROADCAST as RakPeer::Startup asks (RakPeer.cpp:505),
    /// no SO_REUSEADDR so a taken port fails, then 4 zero bytes sent to the bound address as a test.
    /// </summary>
    public static bool TryBind(SocketBinding binding, out UdpDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        ArgumentNullException.ThrowIfNull(binding);
        transport = null;
        startupError = default;
        socketError = SocketError.Success;
        if (binding.LocalEndPoint.AddressFamily != AddressFamily.InterNetwork)
        {
            // IPv6 SystemAddresses are serialised as a platform sockaddr_in6 (docs/plan.md).
            startupError = StartupError.SocketFamilyNotSupported;
            socketError = SocketError.AddressFamilyNotSupported;
            return false;
        }

        var socket = new Socket(binding.LocalEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (binding.ReceiveBufferSize is { } receive)
                socket.ReceiveBufferSize = receive;
            if (binding.SendBufferSize is { } send)
                socket.SendBufferSize = send;
            socket.EnableBroadcast = true;
            socket.Bind(binding.LocalEndPoint);
        }
        catch (SocketException exception)
        {
            socket.Dispose();
            socketError = exception.SocketErrorCode;
            startupError = exception.SocketErrorCode switch
            {
                SocketError.AddressAlreadyInUse => StartupError.SocketPortAlreadyInUse,
                SocketError.AddressFamilyNotSupported or SocketError.ProtocolFamilyNotSupported => StartupError.SocketFamilyNotSupported,
                _ => StartupError.SocketFailedToBind,
            };
            return false;
        }

        // RakNetSocket2.cpp:317-326. The bound address has 0.0.0.0 replaced by 127.0.0.1 (RakNetSocket2_Berkley.cpp:97-101).
        var bound = (IPEndPoint)socket.LocalEndPoint!;
        var self = new IPEndPoint(bound.Address.Equals(IPAddress.Any) ? IPAddress.Loopback : bound.Address, bound.Port);
        try
        {
            socket.SendTo(stackalloc byte[4], SocketFlags.None, self);
        }
        catch (SocketException exception)
        {
            socket.Dispose();
            socketError = exception.SocketErrorCode;
            startupError = StartupError.SocketFailedTestSend;
            return false;
        }

        transport = new UdpDatagramTransport(socket);
        return true;
    }

    public void SetReadySignal(Action signal) => _signal = signal;

    public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote)
    {
        if (_fatal is { } fatal)
            throw fatal;
        if (TryTakeSlot(buffer, out length, out remote))
            return true;
        if (_disposed || Volatile.Read(ref _armed) != 0)
            return false;
        // The receive may have completed between the first take and reading _armed; its datagram comes first.
        if (TryTakeSlot(buffer, out length, out remote))
            return true;
        if (_signal is not null)
            return TryReceiveThroughSlot(buffer, out length, out remote);

        while (true)
        {
            try
            {
                if (_socket.Available == 0)
                    break;
                length = _socket.ReceiveFrom(buffer, SocketFlags.None, _receiveAddress);
                remote = SystemAddress.FromSocketAddress(_receiveAddress);
                return true;
            }
            catch (SocketException exception) when (IsPerDatagramError(exception.SocketErrorCode))
            {
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        Arm();
        return TryTakeSlot(buffer, out length, out remote);
    }

    public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0)
    {
        if (remote.AddressFamily != _socket.AddressFamily || _disposed)
            return false;
        lock (_sendGate)
        {
            try
            {
                remote.CopyTo(_sendAddress);
                if (ttl <= 0)
                    return _socket.SendTo(datagram, SocketFlags.None, _sendAddress) == datagram.Length;

                var previous = _socket.Ttl;
                _socket.Ttl = (short)ttl;
                try
                {
                    return _socket.SendTo(datagram, SocketFlags.None, _sendAddress) == datagram.Length;
                }
                finally
                {
                    _socket.Ttl = previous;
                }
            }
            catch (SocketException)
            {
                // RakNet ignores send failures (RNS2_Berkley::Send returns the error and nobody checks it); the
                // reliability layer resends or times out.
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// RakPeer.cpp:5779-5821: IP don't-fragment is set for this send only (RNS2_Berkley::SetDoNotFragment,
    /// RakNetSocket2_Berkley.cpp:71-80). A platform that refuses the option sends the datagram as it is.
    /// </summary>
    public bool TrySendDoNotFragment(ReadOnlySpan<byte> datagram, in SystemAddress remote, out SocketError socketError)
    {
        socketError = SocketError.Success;
        if (remote.AddressFamily != _socket.AddressFamily || _disposed)
        {
            socketError = SocketError.AddressFamilyNotSupported;
            return false;
        }
        lock (_sendGate)
        {
            var restore = SetDontFragment();
            try
            {
                remote.CopyTo(_sendAddress);
                return _socket.SendTo(datagram, SocketFlags.None, _sendAddress) == datagram.Length;
            }
            catch (SocketException exception)
            {
                socketError = exception.SocketErrorCode;
                return false;
            }
            catch (ObjectDisposedException)
            {
                socketError = SocketError.OperationAborted;
                return false;
            }
            finally
            {
                if (restore is { } previous)
                    RestoreDontFragment(previous);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
    }

    /// <summary>
    /// Sets don't-fragment and returns what puts the socket back: on Linux the raw path MTU discovery mode it had (a new
    /// socket's IP_PMTUDISC_WANT, which Socket.DontFragment = false would turn into IP_PMTUDISC_DONT), elsewhere 0. Null
    /// when the option could not be set.
    /// </summary>
    private int? SetDontFragment()
    {
        if (!_dontFragmentSupported)
            return null;
        try
        {
            var previous = OperatingSystem.IsLinux() ? PathMtuDiscovery : 0;
            _socket.DontFragment = true;
            return previous;
        }
        catch (Exception exception) when (exception is SocketException or NotSupportedException)
        {
            _dontFragmentSupported = false;
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private void RestoreDontFragment(int previous)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var (level, name) = MtuDiscoverOption;
                _socket.SetRawSocketOption(level, name, BitConverter.GetBytes(previous));
            }
            else
            {
                _socket.DontFragment = false;
            }
        }
        catch (Exception exception) when (exception is SocketException or NotSupportedException or ObjectDisposedException)
        {
        }
    }

    // Errors that concern one datagram, not the socket: an ICMP port unreachable from an earlier send, or an
    // oversized datagram.
    private static bool IsPerDatagramError(SocketError error) =>
        error is SocketError.ConnectionReset or SocketError.ConnectionRefused or SocketError.MessageSize
            or SocketError.NetworkReset or SocketError.HostUnreachable or SocketError.WouldBlock;

    private bool TryTakeSlot(Span<byte> buffer, out int length, out SystemAddress remote)
    {
        if (!_slotReady)
        {
            length = 0;
            remote = default;
            return false;
        }
        length = Math.Min(_slotLength, buffer.Length);
        _slot.AsSpan(0, length).CopyTo(buffer);
        remote = _slotRemote;
        _slotReady = false;
        return true;
    }

    // Background mode: every receive goes through the slot. One that completes at once is a datagram that was waiting,
    // taken straight away without a signal, since the loop is already reading; the first that cannot complete stays
    // outstanding as the signal. One system call per datagram, where checking Available first would take two.
    private bool TryReceiveThroughSlot(Span<byte> buffer, out int length, out SystemAddress remote)
    {
        while (Arm(signalIfCompletedNow: false))
        {
            if (TryTakeSlot(buffer, out length, out remote))
                return true;
            if (_fatal is { } fatal)
                throw fatal;
            if (_disposed)
                break;
            // The receive failed for that datagram alone, such as an ICMP port unreachable; try the next.
        }
        return TryTakeSlot(buffer, out length, out remote);
    }

    /// <summary>
    /// Issues the outstanding receive. Never while the slot holds an unread datagram: the next receive would write into
    /// the same slot. Returns true when the receive completed at once, its outcome already in the slot.
    /// </summary>
    internal bool Arm(bool signalIfCompletedNow = true)
    {
        if (_signal is null || _disposed || _slotReady || Interlocked.CompareExchange(ref _armed, 1, 0) != 0)
            return false;
        try
        {
#pragma warning disable CA2012 // The awaiter is consumed exactly once, by OnSlotReceived.
            _pending = _socket.ReceiveFromAsync(_slot, SocketFlags.None, _slotAddress).ConfigureAwait(false).GetAwaiter();
#pragma warning restore CA2012
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        if (!_pending.IsCompleted)
        {
            _pending.UnsafeOnCompleted(_onSlotReceived);
            return false;
        }
        Complete(signalIfCompletedNow);
        return true;
    }

    private void OnSlotReceived() => Complete(signal: true);

    private void Complete(bool signal)
    {
        try
        {
            _slotLength = _pending.GetResult();
            _slotRemote = SystemAddress.FromSocketAddress(_slotAddress);
            _slotReady = true;
        }
        catch (SocketException exception) when (IsPerDatagramError(exception.SocketErrorCode))
        {
        }
        catch (SocketException exception) when (!_disposed && exception.SocketErrorCode != SocketError.OperationAborted)
        {
            _fatal = exception;
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _pending = default;
            Volatile.Write(ref _armed, 0);
            if (signal)
                _signal?.Invoke();
        }
    }
}
