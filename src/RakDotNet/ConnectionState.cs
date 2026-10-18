namespace RakDotNet;

/// <summary>RakNet's ConnectionState (RakPeerInterface.h), in the same order.</summary>
public enum ConnectionState : byte
{
    /// <summary>IS_PENDING: an outgoing attempt still in the request queue.</summary>
    Pending,

    /// <summary>IS_CONNECTING</summary>
    Connecting,

    /// <summary>IS_CONNECTED</summary>
    Connected,

    /// <summary>IS_DISCONNECTING: a notifying close waiting for outgoing data to drain.</summary>
    Disconnecting,

    /// <summary>IS_SILENTLY_DISCONNECTING</summary>
    SilentlyDisconnecting,

    /// <summary>IS_DISCONNECTED</summary>
    Disconnected,

    /// <summary>IS_NOT_CONNECTED</summary>
    NotConnected,
}
