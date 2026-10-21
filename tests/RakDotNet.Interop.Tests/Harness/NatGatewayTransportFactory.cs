using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Puts a RakDotNet peer behind a <see cref="NatRouter"/>, as the natGateway command puts tools/interop-peer behind
/// one: every socket bound through it sends to the router's gateway and receives only from it, with the header
/// docs/interop-protocol.md specifies. A socket's datagrams to its own address pass untouched.
/// </summary>
public sealed class NatGatewayTransportFactory(IPEndPoint gateway, IDatagramTransportFactory? inner = null) : IDatagramTransportFactory
{
    private readonly IDatagramTransportFactory _inner = inner ?? UdpTransportFactory.Instance;

    public IPEndPoint Gateway { get; } = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        transport = null;
        if (!_inner.TryBind(binding, out var bound, out startupError, out socketError))
            return false;
        transport = new GatewayTransport(bound, Gateway);
        return true;
    }

    private sealed class GatewayTransport(IDatagramTransport inner, SystemAddress gateway) : IDatagramTransport
    {
        private const int MaxDatagramSize = 65536 + NatRouter.HeaderSize;

        // Receives run on one thread at a time: the peer's I/O cycle, or the plugin that owns the socket.
        private readonly byte[] _received = new byte[MaxDatagramSize];

        public SystemAddress LocalAddress => inner.LocalAddress;

        public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote)
        {
            while (inner.TryReceive(_received, out var received, out var from))
            {
                if (from == gateway)
                {
                    if (received < NatRouter.HeaderSize)
                        continue;
                    remote = NatRouter.ReadAddress(_received.AsSpan(1));
                    length = Math.Min(buffer.Length, received - NatRouter.HeaderSize);
                    _received.AsSpan(NatRouter.HeaderSize, length).CopyTo(buffer);
                    return true;
                }
                if (IsOwnAddress(from))
                {
                    remote = from;
                    length = Math.Min(buffer.Length, received);
                    _received.AsSpan(0, length).CopyTo(buffer);
                    return true;
                }
                // Nothing outside routes to a host behind a NAT.
            }
            length = 0;
            remote = default;
            return false;
        }

        public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0)
        {
            if (IsOwnAddress(remote))
                return inner.TrySend(datagram, remote, ttl);
            var buffer = ArrayPool<byte>.Shared.Rent(NatRouter.HeaderSize + datagram.Length);
            try
            {
                var length = Encapsulate(datagram, remote, ttl, buffer);
                return inner.TrySend(buffer.AsSpan(0, length), gateway);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public bool TrySendDoNotFragment(ReadOnlySpan<byte> datagram, in SystemAddress remote, out SocketError socketError)
        {
            if (IsOwnAddress(remote))
                return inner.TrySendDoNotFragment(datagram, remote, out socketError);
            var buffer = ArrayPool<byte>.Shared.Rent(NatRouter.HeaderSize + datagram.Length);
            try
            {
                var length = Encapsulate(datagram, remote, 0, buffer);
                return inner.TrySendDoNotFragment(buffer.AsSpan(0, length), gateway, out socketError);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void SetReadySignal(Action signal) => inner.SetReadySignal(signal);

        public void Dispose() => inner.Dispose();

        private static int Encapsulate(ReadOnlySpan<byte> datagram, SystemAddress remote, int ttl, byte[] buffer)
        {
            buffer[0] = (byte)Math.Clamp(ttl, 0, 255);
            NatRouter.WriteAddress(buffer.AsSpan(1), remote.ToIPEndPoint());
            datagram.CopyTo(buffer.AsSpan(NatRouter.HeaderSize));
            return NatRouter.HeaderSize + datagram.Length;
        }

        private bool IsOwnAddress(SystemAddress address)
        {
            var local = inner.LocalAddress;
            return address.Port == local.Port && (address.EqualsIgnoringPort(local) || local.EqualsIgnoringPort(new SystemAddress(IPAddress.Any, 0)));
        }
    }
}
