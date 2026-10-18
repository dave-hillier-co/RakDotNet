using System.Net;

namespace RakDotNet.Transport;

/// <summary>What RakPeer::Startup and SetMaximumIncomingConnections establish for a <see cref="PeerTransport"/>.</summary>
internal sealed record PeerTransportOptions
{
    /// <summary>RakPeer's update interval: the update thread waits at most 10 ms for data (UpdateNetworkLoop, RakPeer.cpp:6352-6358).</summary>
    public static readonly TimeSpan DefaultUpdateInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>The address and port to bind (SocketDescriptor). Port 0 binds an ephemeral port.</summary>
    public required IPEndPoint LocalEndPoint { get; init; }

    /// <summary>Startup's maxConnections.</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>SetMaximumIncomingConnections. RakPeer starts at 0, accepting nobody.</summary>
    public int MaxIncomingConnections { get; init; }

    /// <summary>The peer's GUID, or null to generate a random one as RakPeer::GenerateGUID does.</summary>
    public ulong? Guid { get; init; }

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>How often the update cycle runs when no datagram arrives.</summary>
    public TimeSpan UpdateInterval { get; init; } = DefaultUpdateInterval;
}
