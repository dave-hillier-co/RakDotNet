using System.Net;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Peer;

/// <summary>A datagram for the transport to send.</summary>
internal readonly record struct OutgoingDatagram(IPEndPoint Remote, byte[] Data);

/// <summary>ConnectionAttemptResult, RakNetTypes.h:58-66. Values are positional, as in the C++.</summary>
internal enum ConnectionAttemptResult
{
    ConnectionAttemptStarted,
    InvalidParameter,
    CannotResolveDomainName,
    AlreadyConnectedToEndpoint,
    ConnectionAttemptAlreadyInProgress,
    SecurityInitializationFailed,
}

/// <summary>ConnectionState, RakNetTypes.h:69-85. Values are positional, as in the C++.</summary>
internal enum ConnectionState
{
    IsPending,
    IsConnecting,
    IsConnected,
    IsDisconnecting,
    IsSilentlyDisconnecting,
    IsDisconnected,
    IsNotConnected,
}

/// <summary>Configuration of a <see cref="PeerCore"/>: what RakPeer::Startup and its setters establish.</summary>
internal sealed record PeerOptions
{
    /// <summary>This peer's GUID (RakPeer::GenerateGUID). The caller chooses it; the core has no randomness.</summary>
    public required ulong Guid { get; init; }

    /// <summary>The number of remote systems, incoming and outgoing (Startup's maxConnections).</summary>
    public int MaxConnections { get; init; } = 8;

    /// <summary>SetMaximumIncomingConnections. RakPeer starts at 0, accepting nobody.</summary>
    public int MaxIncomingConnections { get; init; }

    /// <summary>ipList, as built by <see cref="LocalAddressList.Build"/>.</summary>
    public IReadOnlyList<IPEndPoint> LocalAddresses { get; init; } = InternalAddressList.CreateUnassigned();

    /// <summary>SetIncomingPassword: what ID_CONNECTION_REQUEST must carry.</summary>
    public ReadOnlyMemory<byte> IncomingPassword { get; init; }

    /// <summary>defaultTimeoutTime (RakPeer.cpp:247-250), given to each new connection.</summary>
    public long TimeoutMs { get; init; } = ReliabilityLayer.DefaultTimeoutMs;
}

/// <summary>RemoteSystemStruct::ConnectMode, RakPeer.h:693.</summary>
internal enum ConnectMode
{
    NoAction,
    DisconnectAsap,
    DisconnectAsapSilently,
    DisconnectOnNoAck,
    RequestedConnection,
    HandlingConnectionRequest,
    UnverifiedSender,
    Connected,
}

/// <summary>One slot of remoteSystemList (RemoteSystemStruct, RakPeer.h:663).</summary>
internal sealed class RemoteSystem(int index, IPEndPoint address, ulong guid, ReliabilityLayer session, ConnectMode mode, long nowMs)
{
    /// <summary>The slot index, sent as the SystemIndex of ID_CONNECTION_REQUEST_ACCEPTED.</summary>
    public int Index { get; } = index;

    public IPEndPoint Address { get; } = address;

    public ulong Guid { get; set; } = guid;

    public bool IsActive { get; set; } = true;

    public ReliabilityLayer Session { get; } = session;

    public ConnectMode Mode { get; set; } = mode;

    public bool WeInitiatedTheConnection { get; set; }

    public long ConnectionTimeMs { get; } = nowMs;

    public long LastReliableSendMs { get; set; } = nowMs;

    /// <summary>0: ping as soon as connected (RakPeer.cpp:3749).</summary>
    public long NextPingTimeMs { get; set; }

    /// <summary>65535 until the first pong (RakPeer.cpp:3748).</summary>
    public ushort LowestPing { get; set; } = ushort.MaxValue;

    public ushort LastPing { get; set; } = ushort.MaxValue;

    public long ClockDifferential { get; set; }

    public IPEndPoint MyExternalAddress { get; set; } = InternalAddressList.Unassigned;

    public IReadOnlyList<IPEndPoint> TheirInternalAddresses { get; set; } = [];
}

/// <summary>RequestedConnectionStruct (RakPeer.h:831): a Connect call waiting for the offline handshake.</summary>
internal sealed class ConnectionAttempt(IPEndPoint address, byte[] password, int attemptCount, int intervalMs, long timeoutMs, long nowMs)
{
    public IPEndPoint Address { get; } = address;

    public byte[] Password { get; } = password;

    public int AttemptCount { get; } = attemptCount;

    public int IntervalMs { get; } = intervalMs;

    /// <summary>The connection's timeout, or 0 for the peer default.</summary>
    public long TimeoutMs { get; } = timeoutMs;

    public long NextRequestTimeMs { get; set; } = nowMs;

    public int RequestsMade { get; set; }
}
