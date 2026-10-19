using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Transport;

/// <summary>
/// One bound UDP socket. Receives without blocking with <c>Socket.ReceiveFrom(Span, SocketFlags, SocketAddress)</c>,
/// and in Background mode keeps one outstanding <c>ReceiveFromAsync</c> into a slot as the wake signal, armed only when
/// the socket has been drained.
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

    /// <summary>Binds a UDP socket as RakNet's RNS2_Berkley::Bind does, without SO_REUSEADDR so a taken port fails.</summary>
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

    public void Dispose()
    {
        _disposed = true;
        _socket.Dispose();
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

    private void Arm()
    {
        if (_signal is null || _disposed || Interlocked.CompareExchange(ref _armed, 1, 0) != 0)
            return;
        try
        {
#pragma warning disable CA2012 // The awaiter is consumed exactly once, by OnSlotReceived.
            _pending = _socket.ReceiveFromAsync(_slot, SocketFlags.None, _slotAddress).ConfigureAwait(false).GetAwaiter();
#pragma warning restore CA2012
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (_pending.IsCompleted)
            OnSlotReceived();
        else
            _pending.UnsafeOnCompleted(_onSlotReceived);
    }

    private void OnSlotReceived()
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
            _signal?.Invoke();
        }
    }
}
