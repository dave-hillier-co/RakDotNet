using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>One bound socket, as GetSockets / GetSocket return.</summary>
/// <remarks>
/// The underlying <see cref="System.Net.Sockets.Socket"/> is never handed out: its receive side belongs to the peer, and
/// a second reader would steal datagrams.
/// </remarks>
public sealed class RakSocket
{
    private static readonly SystemAddress AnyIPv4 = new(IPAddress.Any, 0);

    private readonly IDatagramTransport _transport;
    private volatile bool _isOpen = true;

    internal RakSocket(int index, IDatagramTransport transport)
    {
        Index = index;
        _transport = transport;
        // RNS2_Berkley::GetSystemAddressIPV4 (RakNetSocket2_Berkley.cpp:89-102): a socket bound to every interface
        // reports 127.0.0.1.
        var bound = transport.LocalAddress;
        LocalAddress = bound.EqualsIgnoringPort(AnyIPv4) ? new SystemAddress(IPAddress.Loopback, bound.Port) : bound;
    }

    /// <summary>The binding's index in <see cref="RakPeerOptions.Bindings"/>.</summary>
    public int Index { get; }

    /// <summary>GetMyBoundAddress(index): the bound address, with 0.0.0.0 reported as 127.0.0.1.</summary>
    public SystemAddress LocalAddress { get; }

    public AddressFamily AddressFamily => LocalAddress.AddressFamily;

    /// <summary>False after shutdown.</summary>
    public bool IsOpen => _isOpen;

    /// <summary>Sends a raw datagram, without RakNet framing.</summary>
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote) => _isOpen && _transport.TrySend(datagram, remote);

    /// <summary>Sends a raw datagram with a time-to-live, as SendTTL does.</summary>
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote, int ttl)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        return _isOpen && _transport.TrySend(datagram, remote, ttl);
    }

    internal void Close() => _isOpen = false;
}
