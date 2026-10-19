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
    private RakSocket() { }

    /// <summary>The binding's index in <see cref="RakPeerOptions.Bindings"/>.</summary>
    public int Index => throw new NotImplementedException();

    /// <summary>GetMyBoundAddress(index)</summary>
    public SystemAddress LocalAddress => throw new NotImplementedException();

    public AddressFamily AddressFamily => throw new NotImplementedException();

    /// <summary>False after shutdown.</summary>
    public bool IsOpen => throw new NotImplementedException();

    /// <summary>Sends a raw datagram, without RakNet framing.</summary>
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote) => throw new NotImplementedException();

    /// <summary>Sends a raw datagram with a time-to-live, as SendTTL does.</summary>
    public bool SendTo(ReadOnlySpan<byte> datagram, SystemAddress remote, int ttl) => throw new NotImplementedException();
}
