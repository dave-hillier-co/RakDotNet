using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

namespace RakDotNet.Testing;

/// <summary>
/// A hand-written datagram fabric standing in for UDP, so several real <see cref="RakPeer"/>s run sociable tests in one
/// process with no sockets. Pass it as <see cref="RakPeerOptions.Transport"/> to every peer.
/// </summary>
/// <remarks>
/// Loss, delay, duplication and reordering are drawn from <see cref="InMemoryNetworkOptions.Seed"/> and scheduled on
/// <see cref="TimeProvider"/>, so with <see cref="PeerThreading.Manual"/> and a fake time provider a run is fully
/// deterministic. A binding on port 0 is given the next free port; binding a port twice fails with
/// <see cref="StartupError.SocketPortAlreadyInUse"/>.
/// </remarks>
public sealed class InMemoryNetwork : IDatagramTransportFactory
{
    public InMemoryNetwork(TimeProvider timeProvider, InMemoryNetworkOptions? options = null) => throw new NotImplementedException();

    /// <summary>The clock delayed datagrams are scheduled on; give the peers the same one.</summary>
    public TimeProvider TimeProvider => throw new NotImplementedException();

    public InMemoryNetworkOptions Options => throw new NotImplementedException();

    /// <summary>Datagrams handed to the network by any transport.</summary>
    public long DatagramsSent => throw new NotImplementedException();

    /// <summary>Datagrams the network dropped by <see cref="InMemoryNetworkOptions.PacketLoss"/> or to an unbound address.</summary>
    public long DatagramsDropped => throw new NotImplementedException();

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError) =>
        throw new NotImplementedException();
}

/// <summary>The impairments of an <see cref="InMemoryNetwork"/>. The default is a perfect network.</summary>
public sealed record InMemoryNetworkOptions
{
    /// <summary>Seeds every random draw.</summary>
    public int Seed { get; init; }

    /// <summary>Probability, 0 to 1, that a datagram is dropped.</summary>
    public double PacketLoss { get; init; }

    /// <summary>Delay added to every datagram.</summary>
    public TimeSpan MinDelay { get; init; }

    /// <summary>Maximum extra delay drawn per datagram.</summary>
    public TimeSpan DelayVariance { get; init; }

    /// <summary>Probability, 0 to 1, that a datagram is delivered twice.</summary>
    public double Duplication { get; init; }

    /// <summary>Probability, 0 to 1, that a datagram is held back behind the next one.</summary>
    public double Reordering { get; init; }
}
