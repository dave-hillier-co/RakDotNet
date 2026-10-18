using System.Collections;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary><see cref="RakPeer.LowLevel"/>: what NAT, relay and diagnostic plugins need beyond the main surface.</summary>
public sealed class RakPeerLowLevel
{
    private RakPeerLowLevel() { }

    /// <summary>SendOutOfBand; received as an OutOfBand event.</summary>
    public bool SendOutOfBand(SystemAddress remote, ReadOnlySpan<byte> payload, int socketIndex = 0) =>
        throw new NotImplementedException();

    /// <inheritdoc cref="SendOutOfBand"/>
    public ValueTask<bool> SendOutOfBandAsync(string host, int port, ReadOnlyMemory<byte> payload,
                                              int socketIndex = 0, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>WriteOutOfBandHeader: ID_OUT_OF_BAND_INTERNAL, this peer's GUID and the magic (RakPeer.cpp:2979-2985).</summary>
    public void WriteOutOfBandHeader(ref BitStreamWriter writer) => throw new NotImplementedException();

    /// <inheritdoc cref="WriteOutOfBandHeader(ref BitStreamWriter)"/>
    public void WriteOutOfBandHeader(BitStream stream) => throw new NotImplementedException();

    /// <summary>SendTTL</summary>
    public bool SendTtl(SystemAddress remote, int ttl, int socketIndex = 0) => throw new NotImplementedException();

    /// <summary>ChangeSystemAddress, for Router2.</summary>
    public void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress address) => throw new NotImplementedException();

    /// <summary>SetIncomingDatagramEventHandler.</summary>
    public IncomingDatagramFilter? IncomingDatagramFilter
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>ApplyNetworkSimulator; null is off.</summary>
    public NetworkSimulatorOptions? NetworkSimulator
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>IsNetworkSimulatorActive</summary>
    public bool IsNetworkSimulatorActive => throw new NotImplementedException();

    /// <summary>Observers that are not plugins.</summary>
    public NetworkObserverCollection Observers => throw new NotImplementedException();
}

/// <summary>Returns false to drop a datagram before RakNet processes it. Runs in the I/O cycle and must not block.</summary>
public delegate bool IncomingDatagramFilter(ReadOnlySpan<byte> datagram, SystemAddress from, int socketIndex);

/// <summary><see cref="RakPeerLowLevel.Observers"/>. Attaching is a command; the observer's hooks start on the next I/O cycle.</summary>
public sealed class NetworkObserverCollection : IReadOnlyCollection<INetworkObserver>
{
    private NetworkObserverCollection() { }

    public int Count => throw new NotImplementedException();

    public void Attach(INetworkObserver observer) => throw new NotImplementedException();

    public bool Detach(INetworkObserver observer) => throw new NotImplementedException();

    public IEnumerator<INetworkObserver> GetEnumerator() => throw new NotImplementedException();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
