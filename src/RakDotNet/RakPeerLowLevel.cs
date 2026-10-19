using System.Collections;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet;

/// <summary><see cref="RakPeer.LowLevel"/>: what NAT, relay and diagnostic plugins need beyond the main surface.</summary>
public sealed class RakPeerLowLevel
{
    private readonly RakPeer _peer;
    private volatile IncomingDatagramFilter? _filter;
    private volatile NetworkSimulatorOptions? _simulator;

    internal RakPeerLowLevel(RakPeer peer, NetworkSimulatorOptions? simulator)
    {
        _peer = peer;
        _simulator = simulator;
        
        Observers = new NetworkObserverCollection(peer);
    }

    /// <summary>
    /// SendOutOfBand (RakPeer.cpp:2997-3044): ID_OUT_OF_BAND_INTERNAL, this peer's GUID, the magic and
    /// <paramref name="payload"/> in one datagram, sent on the next I/O cycle; received as an OutOfBand event. False
    /// when the address cannot be reached from the socket.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="payload"/> is 400 bytes or more, which the receiver drops (RakPeer.cpp:4708-4710).</exception>
    public bool SendOutOfBand(SystemAddress remote, ReadOnlySpan<byte> payload, int socketIndex = 0) =>
        _peer.SendOutOfBand(remote, payload, socketIndex);

    /// <inheritdoc cref="SendOutOfBand"/>
    public async ValueTask<bool> SendOutOfBandAsync(string host, int port, ReadOnlyMemory<byte> payload,
                                                    int socketIndex = 0, CancellationToken cancellationToken = default)
    {
        var address = await RakPeer.ResolveAsync(host, port, cancellationToken).ConfigureAwait(false);
        return address is { } remote && SendOutOfBand(remote, payload.Span, socketIndex);
    }

    /// <summary>WriteOutOfBandHeader: ID_OUT_OF_BAND_INTERNAL, this peer's GUID and the magic (RakPeer.cpp:2979-2985).</summary>
    public void WriteOutOfBandHeader(ref BitStreamWriter writer) => new OutOfBandHeader(_peer.RakNetGuid).Write(ref writer);

    /// <inheritdoc cref="WriteOutOfBandHeader(ref BitStreamWriter)"/>
    public void WriteOutOfBandHeader(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(new OutOfBandHeader(_peer.RakNetGuid));
    }

    /// <summary>
    /// SendTTL (RakPeer.cpp:2731-2753): two bytes sent with the IP time-to-live set to <paramref name="ttl"/>, so they
    /// open a NAT mapping without reaching the host. Sent on the next I/O cycle.
    /// </summary>
    public bool SendTtl(SystemAddress remote, int ttl, int socketIndex = 0) => _peer.SendTtl(remote, ttl, socketIndex);

    /// <summary>ChangeSystemAddress, for Router2.</summary>
    public void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress address) => throw new NotImplementedException();

    /// <summary>SetIncomingDatagramEventHandler.</summary>
    public IncomingDatagramFilter? IncomingDatagramFilter
    {
        get => _filter;
        set => _filter = value;
    }

    /// <summary>
    /// ApplyNetworkSimulator; null is off. Drops and delays this peer's connected datagrams as they are sent, as RakNet's
    /// debug build does in ReliabilityLayer::SendBitStream (ReliabilityLayer.cpp:2257-2296); offline datagrams pass.
    /// Takes effect on the next I/O cycle.
    /// </summary>
    public NetworkSimulatorOptions? NetworkSimulator
    {
        get => _simulator;
        set
        {
            Transport.NetworkSimulator.Validate(value, nameof(value));
            _peer.SetNetworkSimulator(value);
            _simulator = value;
        }
    }

    /// <summary>IsNetworkSimulatorActive: loss or extra ping is set (RakPeer.cpp:2969-2976).</summary>
    public bool IsNetworkSimulatorActive => Transport.NetworkSimulator.IsActive(_simulator);

    /// <summary>Observers that are not plugins.</summary>
    public NetworkObserverCollection Observers { get; }
}

/// <summary>Returns false to drop a datagram before RakNet processes it. Runs in the I/O cycle and must not block.</summary>
public delegate bool IncomingDatagramFilter(ReadOnlySpan<byte> datagram, SystemAddress from, int socketIndex);

/// <summary><see cref="RakPeerLowLevel.Observers"/>. Attaching is a command; the observer's hooks start on the next I/O cycle.</summary>
public sealed class NetworkObserverCollection : IReadOnlyCollection<INetworkObserver>
{
    private readonly RakPeer _peer;
    private readonly Lock _gate = new();
    private volatile INetworkObserver[] _observers = [];

    internal NetworkObserverCollection(RakPeer peer) => _peer = peer;

    public int Count => _observers.Length;

    internal INetworkObserver[] Current => _observers;

    public void Attach(INetworkObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _peer.ThrowIfStopped();
        lock (_gate)
        {
            if (Array.IndexOf(_observers, observer) < 0)
                _observers = [.. _observers, observer];
        }
    }

    public bool Detach(INetworkObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            var index = Array.IndexOf(_observers, observer);
            if (index < 0)
                return false;
            _observers = [.. _observers[..index], .. _observers[(index + 1)..]];
            return true;
        }
    }

    public IEnumerator<INetworkObserver> GetEnumerator() => ((IEnumerable<INetworkObserver>)_observers).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
