using System.Net;
using System.Security.Cryptography;
using RakDotNet.Protocol.Security;
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

    /// <summary>SetUnreliableTimeout, given to each new connection: RakPeer's 1000 ms (RakPeer.cpp:238, 3647).</summary>
    public long UnreliableTimeoutMs { get; init; } = ReliabilityLayer.RakPeerUnreliableTimeoutMs;

    /// <summary>SetSplitMessageProgressInterval, given to each new connection; 0, never (RakPeer.cpp:236, 3646).</summary>
    public int SplitMessageProgressInterval { get; init; }

    /// <summary>SetPerConnectionOutgoingBandwidthLimit in bits per second; 0, unlimited (RakPeer.cpp:239).</summary>
    public uint OutgoingBandwidthLimit { get; init; }

    /// <summary>AllowConnectionResponseIPMigration. Off by default (RakPeer.cpp:233).</summary>
    public bool AllowConnectionResponseIpMigration { get; init; }

    /// <summary>The receive limits every connection's reliability layer enforces.</summary>
    public SessionLimits SessionLimits { get; init; } = SessionLimits.Default;

    /// <summary>InitializeSecurity (RakPeer.cpp:762-812); null is DisableSecurity.</summary>
    public ServerSecurityOptions? Security { get; init; }

    /// <summary>
    /// The CSPRNG the secure handshake draws from, in place of cat's Fortuna: the server's ephemeral keys and cookie key.
    /// Its output never reaches the wire in the clear.
    /// </summary>
    public ISecureRandom SecureRandom { get; init; } = SystemSecureRandom.Instance;
}

/// <summary>
/// The arguments of InitializeSecurity (RakPeer.cpp:762-812): the server's key pair, which
/// ServerEasyHandshake::Initialize must accept, and bRequireClientKey.
/// </summary>
/// <param name="PublicKey">cat::EasyHandshake::PUBLIC_KEY_BYTES of public key.</param>
/// <param name="PrivateKey">cat::EasyHandshake::PRIVATE_KEY_BYTES of private key; the core copies it.</param>
/// <param name="RequireClientKey">Clients must prove an identity key (two-way authentication).</param>
internal sealed record ServerSecurityOptions(ReadOnlyMemory<byte> PublicKey, ReadOnlyMemory<byte> PrivateKey, bool RequireClientKey);

/// <summary>PublicKeyMode (RakPeerInterface.h) for a secure connection attempt; PKM_INSECURE_CONNECTION has no <see cref="ClientSecurity"/>.</summary>
internal enum ClientKeyMode
{
    /// <summary>PKM_ACCEPT_ANY_PUBLIC_KEY: the server's key is taken from its first reply.</summary>
    AcceptAnyPublicKey,

    /// <summary>PKM_USE_KNOWN_PUBLIC_KEY.</summary>
    UseKnownPublicKey,

    /// <summary>PKM_USE_TWO_WAY_AUTHENTICATION: the known key, and the client proves an identity key of its own.</summary>
    UseTwoWayAuthentication,
}

/// <summary>
/// The security members of RequestedConnectionStruct (RakPeer.h:845-852) as GenerateConnectionRequestChallenge sets
/// them up (RakPeer.cpp:3193-3262): the key mode, the server key the client expects, its ClientEasyHandshake and the
/// challenge generated in advance. The connection attempt owns it; disposing it zeroes the handshake's keys.
/// </summary>
internal sealed class ClientSecurity : IDisposable
{
    private ClientSecurity(ClientKeyMode mode, ClientEasyHandshake handshake)
    {
        Mode = mode;
        Handshake = handshake;
    }

    public ClientKeyMode Mode { get; }

    /// <summary>remote_public_key: zero for <see cref="ClientKeyMode.AcceptAnyPublicKey"/> until the server's reply sets it.</summary>
    public byte[] RemotePublicKey { get; } = new byte[EasyHandshake.PublicKeyBytes];

    /// <summary>handshakeChallenge.</summary>
    public byte[] Challenge { get; } = new byte[EasyHandshake.ChallengeBytes];

    /// <summary>client_handshake.</summary>
    public ClientEasyHandshake Handshake { get; }

    /// <summary>
    /// GenerateConnectionRequestChallenge for a secure mode: null where it returns false, which Connect reports as
    /// SECURITY_INITIALIZATION_FAILED.
    /// </summary>
    /// <param name="mode">The key mode.</param>
    /// <param name="serverPublicKey">The server key for the known-key and two-way modes; ignored when accepting any key.</param>
    /// <param name="identityPublicKey">The client's identity public key for two-way authentication.</param>
    /// <param name="identityPrivateKey">The client's identity private key for two-way authentication.</param>
    /// <param name="random">The CSPRNG the challenge and identity proof draw from.</param>
    public static ClientSecurity? Create(ClientKeyMode mode, ReadOnlySpan<byte> serverPublicKey, ReadOnlySpan<byte> identityPublicKey,
                                         ReadOnlySpan<byte> identityPrivateKey, ISecureRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var security = new ClientSecurity(mode, new ClientEasyHandshake(random));
        if (mode == ClientKeyMode.AcceptAnyPublicKey)
            return security;
        if (serverPublicKey.Length != EasyHandshake.PublicKeyBytes)
        {
            security.Dispose();
            return null;
        }
        serverPublicKey.CopyTo(security.RemotePublicKey);
        var handshake = security.Handshake;
        if (!handshake.Initialize(serverPublicKey)
            || (mode == ClientKeyMode.UseTwoWayAuthentication && !handshake.SetIdentity(identityPublicKey, identityPrivateKey))
            || !handshake.GenerateChallenge(security.Challenge))
        {
            security.Dispose();
            return null;
        }
        return security;
    }

    public void Dispose()
    {
        Handshake.Dispose();
        CryptographicOperations.ZeroMemory(Challenge);
    }
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

    /// <summary>Where the system is reached; changed only by <see cref="PeerCore.ChangeSystemAddress"/>.</summary>
    public IPEndPoint Address { get; set; } = address;

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

    /// <summary>answer (RakPeer.h:686): the server's handshake answer, kept to resend for a duplicate request and to verify the client's identity.</summary>
    public byte[]? Answer { get; set; }

    /// <summary>
    /// client_public_key (RakPeer.h:690): the identity key a client proved under two-way authentication, all zero when
    /// none was (GetClientPublicKeyFromSystemAddress tests for a non-zero byte, RakPeer.cpp:2502-2510).
    /// </summary>
    public byte[] ClientPublicKey { get; } = new byte[EasyHandshake.PublicKeyBytes];

    /// <summary>
    /// The slot was taken out of the address lookup (DereferenceRemoteSystem, RakPeer.cpp:3887-3910) after a bad
    /// challenge: it stays active and is found by GUID, but no longer by address, until it times out.
    /// </summary>
    public bool IsDereferenced { get; set; }
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
