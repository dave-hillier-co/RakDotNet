using System.Collections;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet;

/// <summary><see cref="RakPeer.LowLevel"/>: what NAT, relay and diagnostic plugins need beyond the main surface.</summary>
public sealed class RakPeerLowLevel
{
    private readonly RakPeer _peer;
    private volatile IncomingDatagramFilter? _filter;
    private readonly NetworkSimulatorOptions? _simulator;

    internal RakPeerLowLevel(RakPeer peer, NetworkSimulatorOptions? simulator)
    {
        _peer = peer;
        _simulator = simulator;
        Observers = new NetworkObserverCollection(peer);
    }

    /// <summary>SendOutOfBand; received as an OutOfBand event.</summary>
    public bool SendOutOfBand(SystemAddress remote, ReadOnlySpan<byte> payload, int socketIndex = 0) =>
        throw new NotImplementedException();

    /// <inheritdoc cref="SendOutOfBand"/>
    public ValueTask<bool> SendOutOfBandAsync(string host, int port, ReadOnlyMemory<byte> payload,
                                              int socketIndex = 0, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>WriteOutOfBandHeader: ID_OUT_OF_BAND_INTERNAL, this peer's GUID and the magic (RakPeer.cpp:2979-2985).</summary>
    public void WriteOutOfBandHeader(ref BitStreamWriter writer) => new OutOfBandHeader(_peer.RakNetGuid).Write(ref writer);

    /// <inheritdoc cref="WriteOutOfBandHeader(ref BitStreamWriter)"/>
    public void WriteOutOfBandHeader(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(new OutOfBandHeader(_peer.RakNetGuid));
    }

    /// <summary>SendTTL</summary>
    public bool SendTtl(SystemAddress remote, int ttl, int socketIndex = 0) => throw new NotImplementedException();

    /// <summary>ChangeSystemAddress, for Router2.</summary>
    public void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress address) => throw new NotImplementedException();

    /// <summary>SetIncomingDatagramEventHandler.</summary>
    public IncomingDatagramFilter? IncomingDatagramFilter
    {
        get => _filter;
        set => _filter = value;
    }

    /// <summary>ApplyNetworkSimulator; null is off.</summary>
    public NetworkSimulatorOptions? NetworkSimulator
    {
        get => _simulator;
        set => throw new NotImplementedException();
    }

    /// <summary>IsNetworkSimulatorActive</summary>
    public bool IsNetworkSimulatorActive => _simulator is not null;

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
