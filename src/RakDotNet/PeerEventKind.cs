namespace RakDotNet;

/// <summary>
/// The kind of a <see cref="PeerEvent"/>. A pure function of <see cref="PeerEvent.Data"/>[0], as RakNet applications
/// switch on it, except <see cref="ReceiptAbandoned"/>.
/// </summary>
public enum PeerEventKind : byte
{
    /// <summary>
    /// Any packet not listed below: user IDs, plugin IDs, ID_REMOTE_*, ID_TIMESTAMP-prefixed, loopback, and the
    /// one-byte ID_UNCONNECTED_PING or ID_UNCONNECTED_PING_OPEN_CONNECTIONS of a ping this peer answered.
    /// </summary>
    Message,

    /// <summary>ID_CONNECTION_REQUEST_ACCEPTED (outgoing) or ID_NEW_INCOMING_CONNECTION (incoming).</summary>
    Connected,

    /// <summary>ID_CONNECTION_ATTEMPT_FAILED and the nine rejection IDs; see <see cref="ConnectFailure"/>.</summary>
    ConnectFailed,

    /// <summary>ID_DISCONNECTION_NOTIFICATION or ID_CONNECTION_LOST; see <see cref="RakDotNet.DisconnectReason"/>.</summary>
    Disconnected,

    /// <summary>ID_SND_RECEIPT_ACKED</summary>
    ReceiptAcked,

    /// <summary>ID_SND_RECEIPT_LOSS</summary>
    ReceiptLost,

    /// <summary>
    /// Not a RakNet packet: the receipt's connection ended, or its target was not found, while the receipt was
    /// outstanding. <see cref="PeerEvent.IsRakNetPacket"/> is false, the data is empty, and plugins never see it.
    /// </summary>
    ReceiptAbandoned,

    /// <summary>ID_UNCONNECTED_PONG</summary>
    UnconnectedPong,

    /// <summary>ID_ADVERTISE_SYSTEM</summary>
    AdvertiseSystem,

    /// <summary>ID_OUT_OF_BAND_INTERNAL</summary>
    OutOfBand,

    /// <summary>ID_DOWNLOAD_PROGRESS</summary>
    DownloadProgress,
}

/// <summary>The PI2_LostConnectionReason values RakNet raises (RakPeer.cpp:6417-6422).</summary>
/// <remarks>
/// There is no ClosedByUser: RakNet's LCR_CLOSED_BY_USER callback is commented out (RakPeer.cpp:1652-1663). A local
/// close is reported by the packet RakNet enqueues for it, with <see cref="PeerEvent.ClosedLocally"/> set.
/// </remarks>
public enum DisconnectReason : byte
{
    /// <summary>
    /// ID_DISCONNECTION_NOTIFICATION: the remote's notification, enqueued once this peer's ACK of it is sent
    /// (RakPeer.cpp:6095-6099, 5903), or a local notifying close once its outgoing data drains (RakPeer.cpp:5873-5903).
    /// </summary>
    DisconnectionNotification,

    /// <summary>
    /// ID_CONNECTION_LOST: a timeout or dead link (RakPeer.cpp:5900-5901), or a local silent close of a connected system
    /// (RakPeer.cpp:1667-1678).
    /// </summary>
    ConnectionLost,
}
