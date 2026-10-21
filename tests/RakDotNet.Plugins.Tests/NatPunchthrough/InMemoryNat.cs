using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Testing;

namespace RakDotNet.Plugins.Tests.NatPunchthrough;

/// <summary>How an <see cref="InMemoryNat"/> maps and filters, the four behaviours NatTypeDetectionServer tells apart.</summary>
public enum NatBehaviour
{
    FullCone,
    AddressRestricted,
    PortRestricted,
    Symmetric,
}

/// <summary>
/// A NAT router on an <see cref="InMemoryNetwork"/>, standing in for the network a peer is behind. Give it to a peer as
/// <see cref="RakPeerOptions.Transport"/>: every socket the peer or its plugins bind through it is inside, and what it
/// sends leaves from a mapping the router binds on <see cref="PublicAddress"/>, ports taken in sequence.
/// </summary>
/// <remarks>
/// Cone behaviours keep one mapping per inside socket; symmetric keeps one per inside socket and destination, each at
/// least three ports past the one before, beyond the two ports NatPunchthroughClient predicts past the reported one
/// (MAX_PREDICTIVE_PORT_RANGE), so whether punchthrough gets through does not depend on how many internal addresses the
/// peers report, as each is given a mapping before the external punch. A
/// mapping passes back what comes from anywhere (full cone), from an address it sent to (address restricted), or from
/// an address and port it sent to (port restricted, symmetric). A datagram sent with a time-to-live of 1 dies at the
/// router, and one of 2 opens its mapping but goes no further, as SendTTL intends. What reaches an inside socket
/// directly, not through the router, is dropped, as it would never be routed to a private address.
/// </remarks>
internal sealed class InMemoryNat(InMemoryNetwork network, IPAddress publicAddress, NatBehaviour behaviour) : IDatagramTransportFactory
{
    private readonly Lock _gate = new();
    // NatPunchthroughClient's MAX_PREDICTIVE_PORT_RANGE (2) plus one.
    private const int SymmetricPortSpacing = 3;
    private readonly Dictionary<(Inside Inside, SystemAddress Destination), Mapping> _mappings = [];
    private int _lastSymmetricPort;

    public IPAddress PublicAddress { get; } = publicAddress;

    public NatBehaviour Behaviour { get; } = behaviour;

    /// <summary>The router's mappings, oldest first.</summary>
    public IReadOnlyList<SystemAddress> MappedAddresses
    {
        get
        {
            lock (_gate)
                return [.. _mappings.Values.Distinct().Select(m => m.Endpoint.LocalAddress).Order()];
        }
    }

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        transport = null;
        if (!network.TryBind(binding, out var raw, out startupError, out socketError))
            return false;
        transport = new Inside(this, raw);
        return true;
    }

    private void Outbound(Inside inside, ReadOnlySpan<byte> datagram, SystemAddress destination, int ttl)
    {
        if (ttl == 1)
            return;
        Mapping mapping;
        lock (_gate)
        {
            var key = (inside, Behaviour == NatBehaviour.Symmetric ? destination : SystemAddress.Unassigned);
            if (!_mappings.TryGetValue(key, out var existing))
            {
                if (!TryBindMapping(out var endpoint))
                    return;
                existing = new Mapping(this, inside, endpoint);
                endpoint.SetReadySignal(existing.Pump);
                _mappings.Add(key, existing);
            }
            mapping = existing;
            mapping.SentTo.Add(destination);
        }
        if (ttl != 2)
            mapping.Endpoint.TrySend(datagram, destination);
    }

    // Caller holds _gate.
    private bool TryBindMapping([NotNullWhen(true)] out IDatagramTransport? endpoint)
    {
        if (Behaviour != NatBehaviour.Symmetric || _lastSymmetricPort == 0)
        {
            if (!network.TryBind(new SocketBinding(new IPEndPoint(PublicAddress, 0)), out endpoint, out _, out _))
                return false;
        }
        else
        {
            endpoint = null;
            for (var port = _lastSymmetricPort + SymmetricPortSpacing; port <= IPEndPoint.MaxPort && endpoint is null; port++)
                network.TryBind(new SocketBinding(new IPEndPoint(PublicAddress, port)), out endpoint, out _, out _);
            if (endpoint is null)
                return false;
        }
        if (Behaviour == NatBehaviour.Symmetric)
            _lastSymmetricPort = endpoint.LocalAddress.Port;
        return true;
    }

    private bool Admits(Mapping mapping, SystemAddress source)
    {
        lock (_gate)
        {
            return Behaviour switch
            {
                NatBehaviour.FullCone => true,
                NatBehaviour.AddressRestricted => mapping.SentTo.Any(d => d.EqualsIgnoringPort(source)),
                _ => mapping.SentTo.Contains(source),
            };
        }
    }

    private sealed class Mapping(InMemoryNat nat, Inside inside, IDatagramTransport endpoint)
    {
        public IDatagramTransport Endpoint { get; } = endpoint;

        public HashSet<SystemAddress> SentTo { get; } = [];

        public void Pump()
        {
            var buffer = new byte[2048];
            while (Endpoint.TryReceive(buffer, out var length, out var source))
            {
                if (nat.Admits(this, source))
                    inside.Deliver(buffer.AsSpan(0, length).ToArray(), source);
            }
        }
    }

    /// <summary>A socket behind the router.</summary>
    private sealed class Inside(InMemoryNat nat, IDatagramTransport raw) : IDatagramTransport
    {
        private readonly ConcurrentQueue<(byte[] Data, SystemAddress From)> _delivered = new();
        private Action? _signal;

        public SystemAddress LocalAddress => raw.LocalAddress;

        public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote)
        {
            // Nothing is routed to a private address from outside.
            while (raw.TryReceive(buffer, out _, out _))
            {
            }
            if (_delivered.TryDequeue(out var datagram))
            {
                length = Math.Min(buffer.Length, datagram.Data.Length);
                datagram.Data.AsSpan(0, length).CopyTo(buffer);
                remote = datagram.From;
                return true;
            }
            length = 0;
            remote = default;
            return false;
        }

        public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0)
        {
            nat.Outbound(this, datagram, remote, ttl);
            return true;
        }

        public void SetReadySignal(Action signal) => _signal = signal;

        public void Deliver(byte[] data, SystemAddress from)
        {
            _delivered.Enqueue((data, from));
            _signal?.Invoke();
        }

        public void Dispose() => raw.Dispose();
    }
}
