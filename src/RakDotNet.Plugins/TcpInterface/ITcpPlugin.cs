using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// A plugin attached to a <see cref="TcpInterface"/>: the PluginInterface2 hooks TCPInterface calls. Every hook is
/// optional.
/// </summary>
/// <remarks>
/// <para>
/// TCPInterface calls its plugins from the polling members, on the caller's thread: <see cref="OnUpdate"/> and
/// <see cref="OnReceive"/> from Receive, <see cref="OnConnected"/> from HasNewIncomingConnection and
/// HasCompletedConnectionAttempt, <see cref="OnConnectFailed"/> from HasFailedConnectionAttempt, and
/// <see cref="OnDisconnected"/> from HasLostConnection and CloseConnection. A TCP connection has no RakNetGUID and no
/// <see cref="RakConnection"/>, so these hooks take the address where <see cref="IRakPlugin"/>'s take a connection.
/// </para>
/// <para>
/// A plugin that serves both kinds of host implements both interfaces; <see cref="TcpInterface"/> is an
/// <see cref="IPluginHost"/>, so code that sends through the host works on either.
/// </para>
/// </remarks>
public interface ITcpPlugin
{
    /// <summary>SetTCPInterface + OnAttach, from <see cref="TcpInterface.AttachPlugin"/> (TCPInterface.cpp:551-559).</summary>
    void OnAttached(TcpInterface tcp) { }

    /// <summary>OnDetach, from <see cref="TcpInterface.DetachPlugin"/> (TCPInterface.cpp:560-575).</summary>
    void OnDetached() { }

    /// <summary>OnRakPeerStartup, at the end of a successful <see cref="TcpInterface.Start"/> (TCPInterface.cpp:242-244).</summary>
    void OnStartup() { }

    /// <summary>OnRakPeerShutdown, at the start of every <see cref="TcpInterface.Stop"/> (TCPInterface.cpp:251-253).</summary>
    void OnShutdown() { }

    /// <summary>Update, once per Receive call, before anything is taken (TCPInterface.cpp:507-509).</summary>
    void OnUpdate() { }

    /// <summary>OnReceive. The first <see cref="PluginResult.Consume"/> stops the chain and Receive returns null.</summary>
    PluginResult OnReceive(TcpPacket packet) => PluginResult.Continue;

    /// <summary>
    /// OnNewConnection, for an incoming connection and for a completed outgoing attempt alike; RakNet passes
    /// isIncoming true for both (TCPInterface.cpp:666, 706).
    /// </summary>
    void OnConnected(SystemAddress address) { }

    /// <summary>OnClosedConnection (TCPInterface.cpp:585, 726).</summary>
    void OnDisconnected(SystemAddress address, TcpDisconnectReason reason) { }

    /// <summary>OnFailedConnectionAttempt with FCAR_CONNECTION_ATTEMPT_FAILED (TCPInterface.cpp:679-691).</summary>
    void OnConnectFailed(SystemAddress address) { }
}

/// <summary>The PI2_LostConnectionReason values TCPInterface raises.</summary>
public enum TcpDisconnectReason : byte
{
    /// <summary>LCR_CLOSED_BY_USER: <see cref="TcpInterface.CloseConnection"/> (TCPInterface.cpp:585).</summary>
    ClosedByUser,

    /// <summary>LCR_DISCONNECTION_NOTIFICATION: the connection was lost, reported by HasLostConnection (TCPInterface.cpp:726).</summary>
    DisconnectionNotification,
}
