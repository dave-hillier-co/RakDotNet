using System.Net.Sockets;

namespace RakDotNet;

/// <summary>The StartupResult values that describe the environment rather than a programming error.</summary>
/// <remarks>
/// RAKNET_ALREADY_STARTED cannot happen because a peer starts once; INVALID_SOCKET_DESCRIPTORS and
/// INVALID_MAX_CONNECTIONS are argument validation; COULD_NOT_GENERATE_GUID (RakPeer.cpp:382) cannot happen because the
/// GUID is redrawn; PORT_CANNOT_BE_ZERO applies only to console platforms RakDotNet does not target. RAKNET_STARTED is
/// <see cref="RakPeer.Start"/> returning.
/// </remarks>
public enum StartupError : byte
{
    /// <summary>SOCKET_FAMILY_NOT_SUPPORTED; also IPv6 until per-platform captures exist (docs/plan.md).</summary>
    SocketFamilyNotSupported,

    /// <summary>SOCKET_PORT_ALREADY_IN_USE</summary>
    SocketPortAlreadyInUse,

    /// <summary>SOCKET_FAILED_TO_BIND</summary>
    SocketFailedToBind,

    /// <summary>SOCKET_FAILED_TEST_SEND</summary>
    SocketFailedTestSend,

    /// <summary>FAILED_TO_CREATE_NETWORK_THREAD</summary>
    FailedToCreateNetworkThread,

    /// <summary>STARTUP_OTHER_FAILURE</summary>
    OtherFailure,
}

/// <summary><see cref="RakPeer.Start"/> could not bind or start.</summary>
public sealed class RakStartupException : Exception
{
    public RakStartupException() { }

    public RakStartupException(string message) : base(message) { }

    public RakStartupException(string message, Exception innerException) : base(message, innerException) { }

    public RakStartupException(StartupError error, int bindingIndex, SocketError socketError)
        : base($"Binding {bindingIndex} failed to start: {error} ({socketError}).")
    {
        Error = error;
        BindingIndex = bindingIndex;
        SocketError = socketError;
    }

    public StartupError Error { get; }

    /// <summary>The failing entry of <see cref="RakPeerOptions.Bindings"/>.</summary>
    public int BindingIndex { get; }

    public SocketError SocketError { get; }
}

/// <summary>Thrown by <see cref="ConnectResult.EnsureConnected"/> for an attempt that did not connect.</summary>
public sealed class RakConnectException : Exception
{
    public RakConnectException() { }

    public RakConnectException(string message) : base(message) { }

    public RakConnectException(string message, Exception innerException) : base(message, innerException) { }

    public RakConnectException(ConnectResult result)
        : base($"The connection attempt did not connect: {result.AttemptResult}, {result.Failure?.ToString() ?? "no failure"}.")
    {
        Result = result;
    }

    public ConnectResult Result { get; }
}

/// <summary>
/// A fatal transport failure. <see cref="PeerEventReader.Completion"/> faults with it and pending async operations
/// fault with it; no events are synthesised. Transient errors, such as ICMP-induced ConnectionReset on Windows, are
/// ignored as in RakNet.
/// </summary>
public sealed class RakTransportException : Exception
{
    public RakTransportException() { }

    public RakTransportException(string message) : base(message) { }

    public RakTransportException(string message, Exception innerException) : base(message, innerException) { }

    public RakTransportException(int socketIndex, SocketError socketError)
        : base($"Socket {socketIndex} failed: {socketError}.")
    {
        SocketIndex = socketIndex;
        SocketError = socketError;
    }

    public int SocketIndex { get; }

    public SocketError SocketError { get; }
}
