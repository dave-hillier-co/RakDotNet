using System.Net;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Peer;

/// <summary>A datagram for the transport to send.</summary>
internal readonly record struct OutgoingDatagram(IPEndPoint Remote, byte[] Data)
{
    /// <summary>
    /// Set on MTU probes: the transport sends this datagram with the IP don't-fragment flag set (SetDoNotFragment around
    /// the probe's send, RakPeer.cpp:5784-5787, 5818-5821), and reports a send that fails because the datagram is
    /// larger than the path allows (WSAEMSGSIZE, EMSGSIZE) through <see cref="PeerCore.ReportDatagramTooLarge"/>.
    /// </summary>
    public bool DoNotFragment { get; init; }
}

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

    /// <summary>SetLimitIPConnectionFrequency. Off by default (RakPeer.cpp:312).</summary>
    public bool LimitIpConnectionFrequency { get; init; }
}

/// <summary>
/// The client half of RakNet's PublicKey argument to Connect, as far as the offline handshake reads it
/// (GenerateConnectionRequestChallenge, RakPeer.cpp:3193-3262). Connecting without one is PKM_INSECURE_CONNECTION.
/// </summary>
internal sealed record ClientSecurity
{
    /// <summary>PKM_ACCEPT_ANY_PUBLIC_KEY.</summary>
    public static ClientSecurity AcceptAnyPublicKey { get; } = new();

    /// <summary>
    /// remote_public_key: the server key the client expects (PKM_USE_KNOWN_PUBLIC_KEY, PKM_USE_TWO_WAY_AUTHENTICATION),
    /// <see cref="SecureOpenConnectionReply1.PublicKeyLength"/> bytes, or empty to accept any key.
    /// </summary>
    public ReadOnlyMemory<byte> ServerPublicKey { get; init; }
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

    /// <summary>MTUSize: the agreed MTU the reliability layer packs datagrams to.</summary>
    public int Mtu { get; init; }

    public long ConnectionTimeMs { get; } = nowMs;

    public long LastReliableSendMs { get; set; } = nowMs;

    /// <summary>0: ping as soon as connected (RakPeer.cpp:3749).</summary>
    public long NextPingTimeMs { get; set; }

    /// <summary>65535 until the first pong (RakPeer.cpp:3748).</summary>
    public ushort LowestPing { get; set; } = ushort.MaxValue;

    /// <summary>
    /// pingAndClockDifferential (RakPeer.h:671): the last <see cref="PingHistoryLength"/> pongs, written round
    /// robin from <see cref="PingWriteIndex"/>. Unused entries have a ping of 65535 (RakPeer.cpp:3740-3744).
    /// </summary>
    public PingSample[] PingHistory { get; } = Enumerable.Repeat(new PingSample(ushort.MaxValue, 0), PingHistoryLength).ToArray();

    /// <summary>pingAndClockDifferentialWriteIndex.</summary>
    public int PingWriteIndex { get; set; }

    /// <summary>PING_TIMES_ARRAY_SIZE, RakNetTypes.h:462.</summary>
    public const int PingHistoryLength = 5;

    public IPEndPoint MyExternalAddress { get; set; } = InternalAddressList.Unassigned;

    public IReadOnlyList<IPEndPoint> TheirInternalAddresses { get; set; } = [];
}

/// <summary>
/// PingAndClockDifferential (RakPeer.h:654-659): one pong's round trip in milliseconds and the remote clock's lead,
/// a RakNet::Time difference that wraps for a remote clock behind the local one.
/// </summary>
internal readonly record struct PingSample(ushort PingTime, ulong ClockDifferential);

/// <summary>RequestedConnectionStruct (RakPeer.h:831): a Connect call waiting for the offline handshake.</summary>
internal sealed class ConnectionAttempt(IPEndPoint address, byte[] password, int attemptCount, int intervalMs, long timeoutMs, ClientSecurity? security, long nowMs)
{
    public IPEndPoint Address { get; } = address;

    public byte[] Password { get; } = password;

    public int AttemptCount { get; } = attemptCount;

    public int IntervalMs { get; } = intervalMs;

    /// <summary>The connection's timeout, or 0 for the peer default.</summary>
    public long TimeoutMs { get; } = timeoutMs;

    /// <summary>publicKeyMode and remote_public_key; null for PKM_INSECURE_CONNECTION.</summary>
    public ClientSecurity? Security { get; } = security;

    public long NextRequestTimeMs { get; set; } = nowMs;

    /// <summary>requestsMade, an unsigned char in the C++ (RakPeer.h:835).</summary>
    public byte RequestsMade { get; set; }

    /// <summary>The index into <see cref="RakNetConstants.MtuSizes"/> of the last probe sent.</summary>
    public int LastMtuIndex { get; set; }
}
