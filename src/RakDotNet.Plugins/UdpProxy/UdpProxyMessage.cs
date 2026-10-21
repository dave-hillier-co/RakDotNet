namespace RakDotNet.Plugins;

/// <summary>
/// UDPProxyMessages (UDPProxyCommon.h:43-61): the byte after ID_UDP_PROXY_GENERAL in every message between
/// <see cref="UdpProxyClient"/>, <see cref="UdpProxyServer"/> and <see cref="UdpProxyCoordinator"/>. Values are
/// positional in the C++ enum.
/// </summary>
public enum UdpProxyMessage : byte
{
    /// <summary>ID_UDP_PROXY_FORWARDING_SUCCEEDED</summary>
    ForwardingSucceeded,

    /// <summary>ID_UDP_PROXY_FORWARDING_NOTIFICATION</summary>
    ForwardingNotification,

    /// <summary>ID_UDP_PROXY_NO_SERVERS_ONLINE</summary>
    NoServersOnline,

    /// <summary>ID_UDP_PROXY_RECIPIENT_GUID_NOT_CONNECTED_TO_COORDINATOR</summary>
    RecipientGuidNotConnectedToCoordinator,

    /// <summary>ID_UDP_PROXY_ALL_SERVERS_BUSY</summary>
    AllServersBusy,

    /// <summary>ID_UDP_PROXY_IN_PROGRESS</summary>
    InProgress,

    /// <summary>ID_UDP_PROXY_FORWARDING_REQUEST_FROM_CLIENT_TO_COORDINATOR</summary>
    ForwardingRequestFromClientToCoordinator,

    /// <summary>ID_UDP_PROXY_PING_SERVERS_FROM_COORDINATOR_TO_CLIENT</summary>
    PingServersFromCoordinatorToClient,

    /// <summary>ID_UDP_PROXY_PING_SERVERS_REPLY_FROM_CLIENT_TO_COORDINATOR</summary>
    PingServersReplyFromClientToCoordinator,

    /// <summary>ID_UDP_PROXY_FORWARDING_REQUEST_FROM_COORDINATOR_TO_SERVER</summary>
    ForwardingRequestFromCoordinatorToServer,

    /// <summary>ID_UDP_PROXY_FORWARDING_REPLY_FROM_SERVER_TO_COORDINATOR</summary>
    ForwardingReplyFromServerToCoordinator,

    /// <summary>ID_UDP_PROXY_LOGIN_REQUEST_FROM_SERVER_TO_COORDINATOR</summary>
    LoginRequestFromServerToCoordinator,

    /// <summary>ID_UDP_PROXY_LOGIN_SUCCESS_FROM_COORDINATOR_TO_SERVER</summary>
    LoginSuccessFromCoordinatorToServer,

    /// <summary>ID_UDP_PROXY_ALREADY_LOGGED_IN_FROM_COORDINATOR_TO_SERVER</summary>
    AlreadyLoggedInFromCoordinatorToServer,

    /// <summary>ID_UDP_PROXY_NO_PASSWORD_SET_FROM_COORDINATOR_TO_SERVER</summary>
    NoPasswordSetFromCoordinatorToServer,

    /// <summary>ID_UDP_PROXY_WRONG_PASSWORD_FROM_COORDINATOR_TO_SERVER</summary>
    WrongPasswordFromCoordinatorToServer,
}
