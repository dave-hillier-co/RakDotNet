using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>RakNet's ConnectionAttemptResult: the synchronous answer to starting a connection attempt.</summary>
/// <remarks>
/// INVALID_PARAMETER has no value: a bad argument throws <see cref="ArgumentException"/>, and a stopped peer throws
/// <see cref="ObjectDisposedException"/>.
/// </remarks>
public enum ConnectAttemptResult : byte
{
    /// <summary>CONNECTION_ATTEMPT_STARTED. Does not rule out an immediate ConnectFailed.</summary>
    Started,

    /// <summary>CANNOT_RESOLVE_DOMAIN_NAME; host overloads only.</summary>
    CannotResolveDomainName,

    /// <summary>ALREADY_CONNECTED_TO_ENDPOINT</summary>
    AlreadyConnectedToEndpoint,

    /// <summary>CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS</summary>
    ConnectionAttemptAlreadyInProgress,

    /// <summary>SECURITY_INITIALIZATION_FAILED: an invalid client security configuration (RakPeer.cpp:3296-3298).</summary>
    SecurityInitializationFailed,
}

/// <summary>RakNet's PI2_FailedConnectionAttemptReason: why a started attempt failed (RakPeer.cpp:6429-6458).</summary>
public enum ConnectFailure : byte
{
    /// <summary>ID_CONNECTION_ATTEMPT_FAILED</summary>
    ConnectionAttemptFailed,

    /// <summary>ID_ALREADY_CONNECTED</summary>
    AlreadyConnected,

    /// <summary>ID_NO_FREE_INCOMING_CONNECTIONS</summary>
    NoFreeIncomingConnections,

    /// <summary>ID_CONNECTION_BANNED</summary>
    ConnectionBanned,

    /// <summary>ID_INVALID_PASSWORD</summary>
    InvalidPassword,

    /// <summary>ID_INCOMPATIBLE_PROTOCOL_VERSION</summary>
    IncompatibleProtocolVersion,

    /// <summary>ID_IP_RECENTLY_CONNECTED</summary>
    IpRecentlyConnected,

    /// <summary>ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY</summary>
    RemoteSystemRequiresPublicKey,

    /// <summary>ID_OUR_SYSTEM_REQUIRES_SECURITY</summary>
    OurSystemRequiresSecurity,

    /// <summary>ID_PUBLIC_KEY_MISMATCH</summary>
    PublicKeyMismatch,
}

/// <summary>
/// The outcome of <see cref="RakPeer.ConnectAsync(SystemAddress, ConnectOptions?, CancellationToken)"/>:
/// the synchronous start, the asynchronous failure and success are three separate facts, never one enum.
/// </summary>
public readonly struct ConnectResult
{
    public ConnectResult(ConnectAttemptResult attemptResult, ConnectFailure? failure, RakConnection? connection, byte remoteProtocolVersion)
    {
        AttemptResult = attemptResult;
        Failure = failure;
        Connection = connection;
        RemoteProtocolVersion = remoteProtocolVersion;
    }

    /// <summary><see cref="ConnectAttemptResult.Started"/> unless the attempt was rejected synchronously.</summary>
    public ConnectAttemptResult AttemptResult { get; }

    /// <summary>Set when a started attempt failed.</summary>
    public ConnectFailure? Failure { get; }

    /// <summary>Set when connected.</summary>
    public RakConnection? Connection { get; }

    /// <summary>Set only when <see cref="Failure"/> is <see cref="ConnectFailure.IncompatibleProtocolVersion"/>.</summary>
    public byte RemoteProtocolVersion { get; }

    public bool IsConnected => Connection is not null;

    /// <summary>The connection, or a <see cref="RakConnectException"/> carrying this result.</summary>
    public RakConnection EnsureConnected() => Connection ?? throw new RakConnectException(this);
}
