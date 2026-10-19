using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// The only I/O seam: one bound datagram endpoint. Carries RakNet over UDP, over other datagram media, through the
/// network simulator, and through RakDotNet.Testing's in-memory network.
/// </summary>
public interface IDatagramTransport : IDisposable
{
    SystemAddress LocalAddress { get; }

    /// <summary>Non-blocking; false when no datagram is waiting.</summary>
    bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote);

    /// <summary>Non-blocking; a <paramref name="ttl"/> of 0 keeps the default.</summary>
    bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0);

    /// <summary>
    /// Non-blocking; sends with the IP don't-fragment flag set where the platform supports it, as RakPeer does for each
    /// MTU probe (RakPeer.cpp:5779-5821). <paramref name="socketError"/> is <see cref="SocketError.MessageSize"/> when the
    /// datagram is larger than the path allows. Transports without the flag send as <see cref="TrySend"/> does.
    /// </summary>
    bool TrySendDoNotFragment(ReadOnlySpan<byte> datagram, in SystemAddress remote, out SocketError socketError)
    {
        var sent = TrySend(datagram, remote);
        socketError = sent ? SocketError.Success : SocketError.SocketError;
        return sent;
    }

    /// <summary>Called by the peer once; the transport invokes <paramref name="signal"/> when a datagram becomes available.</summary>
    void SetReadySignal(Action signal);
}

/// <summary>Creates the transport for each <see cref="SocketBinding"/> at start.</summary>
public interface IDatagramTransportFactory
{
    bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError);
}

/// <summary>
/// The default factory: UDP sockets, receiving with <c>Socket.ReceiveFrom(Span, SocketFlags, SocketAddress)</c> into
/// pooled slots and one outstanding <c>ReceiveFromAsync</c> per binding as the Background-mode wake signal.
/// </summary>
/// <remarks>
/// Allocation-free in steady state: one <see cref="System.Net.SocketAddress"/> is reused for receives and copied into a
/// <see cref="SystemAddress"/> value; sends use <c>Socket.SendTo(ReadOnlySpan, SocketFlags, SocketAddress)</c> with a
/// <see cref="System.Net.SocketAddress"/> cached per remote. Transient errors such as ICMP-induced ConnectionReset are ignored, as
/// in RakNet.
/// </remarks>
public sealed class UdpTransportFactory : IDatagramTransportFactory
{
    private UdpTransportFactory() { }

    public static UdpTransportFactory Instance { get; } = new();

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        var bound = Transport.UdpDatagramTransport.TryBind(binding, out var udp, out startupError, out socketError);
        transport = udp;
        return bound;
    }
}
