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
public sealed class UdpTransportFactory : IDatagramTransportFactory
{
    private UdpTransportFactory() { }

    public static UdpTransportFactory Instance { get; } = new();

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError) =>
        throw new NotImplementedException();
}
