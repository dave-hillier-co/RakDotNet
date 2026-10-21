namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet message identifiers (the first byte of a message), generated from MessageIdentifiers.h
/// DefaultMessageIDTypes at RakNet commit 1a169895. Values are positional in the C++ enum.
/// </summary>
public enum MessageId : byte
{
    /// <summary>ID_CONNECTED_PING</summary>
    ConnectedPing = 0,
    /// <summary>ID_UNCONNECTED_PING</summary>
    UnconnectedPing = 1,
    /// <summary>ID_UNCONNECTED_PING_OPEN_CONNECTIONS</summary>
    UnconnectedPingOpenConnections = 2,
    /// <summary>ID_CONNECTED_PONG</summary>
    ConnectedPong = 3,
    /// <summary>ID_DETECT_LOST_CONNECTIONS</summary>
    DetectLostConnections = 4,
    /// <summary>ID_OPEN_CONNECTION_REQUEST_1</summary>
    OpenConnectionRequest1 = 5,
    /// <summary>ID_OPEN_CONNECTION_REPLY_1</summary>
    OpenConnectionReply1 = 6,
    /// <summary>ID_OPEN_CONNECTION_REQUEST_2</summary>
    OpenConnectionRequest2 = 7,
    /// <summary>ID_OPEN_CONNECTION_REPLY_2</summary>
    OpenConnectionReply2 = 8,
    /// <summary>ID_CONNECTION_REQUEST</summary>
    ConnectionRequest = 9,
    /// <summary>ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY</summary>
    RemoteSystemRequiresPublicKey = 10,
    /// <summary>ID_OUR_SYSTEM_REQUIRES_SECURITY</summary>
    OurSystemRequiresSecurity = 11,
    /// <summary>ID_PUBLIC_KEY_MISMATCH</summary>
    PublicKeyMismatch = 12,
    /// <summary>ID_OUT_OF_BAND_INTERNAL</summary>
    OutOfBandInternal = 13,
    /// <summary>ID_SND_RECEIPT_ACKED</summary>
    SndReceiptAcked = 14,
    /// <summary>ID_SND_RECEIPT_LOSS</summary>
    SndReceiptLoss = 15,
    /// <summary>ID_CONNECTION_REQUEST_ACCEPTED</summary>
    ConnectionRequestAccepted = 16,
    /// <summary>ID_CONNECTION_ATTEMPT_FAILED</summary>
    ConnectionAttemptFailed = 17,
    /// <summary>ID_ALREADY_CONNECTED</summary>
    AlreadyConnected = 18,
    /// <summary>ID_NEW_INCOMING_CONNECTION</summary>
    NewIncomingConnection = 19,
    /// <summary>ID_NO_FREE_INCOMING_CONNECTIONS</summary>
    NoFreeIncomingConnections = 20,
    /// <summary>ID_DISCONNECTION_NOTIFICATION</summary>
    DisconnectionNotification = 21,
    /// <summary>ID_CONNECTION_LOST</summary>
    ConnectionLost = 22,
    /// <summary>ID_CONNECTION_BANNED</summary>
    ConnectionBanned = 23,
    /// <summary>ID_INVALID_PASSWORD</summary>
    InvalidPassword = 24,
    /// <summary>ID_INCOMPATIBLE_PROTOCOL_VERSION</summary>
    IncompatibleProtocolVersion = 25,
    /// <summary>ID_IP_RECENTLY_CONNECTED</summary>
    IpRecentlyConnected = 26,
    /// <summary>ID_TIMESTAMP</summary>
    Timestamp = 27,
    /// <summary>ID_UNCONNECTED_PONG</summary>
    UnconnectedPong = 28,
    /// <summary>ID_ADVERTISE_SYSTEM</summary>
    AdvertiseSystem = 29,
    /// <summary>ID_DOWNLOAD_PROGRESS</summary>
    DownloadProgress = 30,
    /// <summary>ID_REMOTE_DISCONNECTION_NOTIFICATION</summary>
    RemoteDisconnectionNotification = 31,
    /// <summary>ID_REMOTE_CONNECTION_LOST</summary>
    RemoteConnectionLost = 32,
    /// <summary>ID_REMOTE_NEW_INCOMING_CONNECTION</summary>
    RemoteNewIncomingConnection = 33,
    /// <summary>ID_FILE_LIST_TRANSFER_HEADER</summary>
    FileListTransferHeader = 34,
    /// <summary>ID_FILE_LIST_TRANSFER_FILE</summary>
    FileListTransferFile = 35,
    /// <summary>ID_FILE_LIST_REFERENCE_PUSH_ACK</summary>
    FileListReferencePushAck = 36,
    /// <summary>ID_DDT_DOWNLOAD_REQUEST</summary>
    DdtDownloadRequest = 37,
    /// <summary>ID_TRANSPORT_STRING</summary>
    TransportString = 38,
    /// <summary>ID_REPLICA_MANAGER_CONSTRUCTION</summary>
    ReplicaManagerConstruction = 39,
    /// <summary>ID_REPLICA_MANAGER_SCOPE_CHANGE</summary>
    ReplicaManagerScopeChange = 40,
    /// <summary>ID_REPLICA_MANAGER_SERIALIZE</summary>
    ReplicaManagerSerialize = 41,
    /// <summary>ID_REPLICA_MANAGER_DOWNLOAD_STARTED</summary>
    ReplicaManagerDownloadStarted = 42,
    /// <summary>ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE</summary>
    ReplicaManagerDownloadComplete = 43,
    /// <summary>ID_RAKVOICE_OPEN_CHANNEL_REQUEST</summary>
    RakvoiceOpenChannelRequest = 44,
    /// <summary>ID_RAKVOICE_OPEN_CHANNEL_REPLY</summary>
    RakvoiceOpenChannelReply = 45,
    /// <summary>ID_RAKVOICE_CLOSE_CHANNEL</summary>
    RakvoiceCloseChannel = 46,
    /// <summary>ID_RAKVOICE_DATA</summary>
    RakvoiceData = 47,
    /// <summary>ID_AUTOPATCHER_GET_CHANGELIST_SINCE_DATE</summary>
    AutopatcherGetChangelistSinceDate = 48,
    /// <summary>ID_AUTOPATCHER_CREATION_LIST</summary>
    AutopatcherCreationList = 49,
    /// <summary>ID_AUTOPATCHER_DELETION_LIST</summary>
    AutopatcherDeletionList = 50,
    /// <summary>ID_AUTOPATCHER_GET_PATCH</summary>
    AutopatcherGetPatch = 51,
    /// <summary>ID_AUTOPATCHER_PATCH_LIST</summary>
    AutopatcherPatchList = 52,
    /// <summary>ID_AUTOPATCHER_REPOSITORY_FATAL_ERROR</summary>
    AutopatcherRepositoryFatalError = 53,
    /// <summary>ID_AUTOPATCHER_CANNOT_DOWNLOAD_ORIGINAL_UNMODIFIED_FILES</summary>
    AutopatcherCannotDownloadOriginalUnmodifiedFiles = 54,
    /// <summary>ID_AUTOPATCHER_FINISHED_INTERNAL</summary>
    AutopatcherFinishedInternal = 55,
    /// <summary>ID_AUTOPATCHER_FINISHED</summary>
    AutopatcherFinished = 56,
    /// <summary>ID_AUTOPATCHER_RESTART_APPLICATION</summary>
    AutopatcherRestartApplication = 57,
    /// <summary>ID_NAT_PUNCHTHROUGH_REQUEST</summary>
    NatPunchthroughRequest = 58,
    /// <summary>ID_NAT_CONNECT_AT_TIME</summary>
    NatConnectAtTime = 59,
    /// <summary>ID_NAT_GET_MOST_RECENT_PORT</summary>
    NatGetMostRecentPort = 60,
    /// <summary>ID_NAT_CLIENT_READY</summary>
    NatClientReady = 61,
    /// <summary>ID_NAT_TARGET_NOT_CONNECTED</summary>
    NatTargetNotConnected = 62,
    /// <summary>ID_NAT_TARGET_UNRESPONSIVE</summary>
    NatTargetUnresponsive = 63,
    /// <summary>ID_NAT_CONNECTION_TO_TARGET_LOST</summary>
    NatConnectionToTargetLost = 64,
    /// <summary>ID_NAT_ALREADY_IN_PROGRESS</summary>
    NatAlreadyInProgress = 65,
    /// <summary>ID_NAT_PUNCHTHROUGH_FAILED</summary>
    NatPunchthroughFailed = 66,
    /// <summary>ID_NAT_PUNCHTHROUGH_SUCCEEDED</summary>
    NatPunchthroughSucceeded = 67,
    /// <summary>ID_READY_EVENT_SET</summary>
    ReadyEventSet = 68,
    /// <summary>ID_READY_EVENT_UNSET</summary>
    ReadyEventUnset = 69,
    /// <summary>ID_READY_EVENT_ALL_SET</summary>
    ReadyEventAllSet = 70,
    /// <summary>ID_READY_EVENT_QUERY</summary>
    ReadyEventQuery = 71,
    /// <summary>ID_LOBBY_GENERAL</summary>
    LobbyGeneral = 72,
    /// <summary>ID_RPC_REMOTE_ERROR</summary>
    RpcRemoteError = 73,
    /// <summary>ID_RPC_PLUGIN</summary>
    RpcPlugin = 74,
    /// <summary>ID_FILE_LIST_REFERENCE_PUSH</summary>
    FileListReferencePush = 75,
    /// <summary>ID_READY_EVENT_FORCE_ALL_SET</summary>
    ReadyEventForceAllSet = 76,
    /// <summary>ID_ROOMS_EXECUTE_FUNC</summary>
    RoomsExecuteFunc = 77,
    /// <summary>ID_ROOMS_LOGON_STATUS</summary>
    RoomsLogonStatus = 78,
    /// <summary>ID_ROOMS_HANDLE_CHANGE</summary>
    RoomsHandleChange = 79,
    /// <summary>ID_LOBBY2_SEND_MESSAGE</summary>
    Lobby2SendMessage = 80,
    /// <summary>ID_LOBBY2_SERVER_ERROR</summary>
    Lobby2ServerError = 81,
    /// <summary>ID_FCM2_NEW_HOST</summary>
    Fcm2NewHost = 82,
    /// <summary>ID_FCM2_REQUEST_FCMGUID</summary>
    Fcm2RequestFcmguid = 83,
    /// <summary>ID_FCM2_RESPOND_CONNECTION_COUNT</summary>
    Fcm2RespondConnectionCount = 84,
    /// <summary>ID_FCM2_INFORM_FCMGUID</summary>
    Fcm2InformFcmguid = 85,
    /// <summary>ID_FCM2_UPDATE_MIN_TOTAL_CONNECTION_COUNT</summary>
    Fcm2UpdateMinTotalConnectionCount = 86,
    /// <summary>ID_FCM2_VERIFIED_JOIN_START</summary>
    Fcm2VerifiedJoinStart = 87,
    /// <summary>ID_FCM2_VERIFIED_JOIN_CAPABLE</summary>
    Fcm2VerifiedJoinCapable = 88,
    /// <summary>ID_FCM2_VERIFIED_JOIN_FAILED</summary>
    Fcm2VerifiedJoinFailed = 89,
    /// <summary>ID_FCM2_VERIFIED_JOIN_ACCEPTED</summary>
    Fcm2VerifiedJoinAccepted = 90,
    /// <summary>ID_FCM2_VERIFIED_JOIN_REJECTED</summary>
    Fcm2VerifiedJoinRejected = 91,
    /// <summary>ID_UDP_PROXY_GENERAL</summary>
    UdpProxyGeneral = 92,
    /// <summary>ID_SQLite3_EXEC</summary>
    Sqlite3Exec = 93,
    /// <summary>ID_SQLite3_UNKNOWN_DB</summary>
    Sqlite3UnknownDb = 94,
    /// <summary>ID_SQLLITE_LOGGER</summary>
    SqlliteLogger = 95,
    /// <summary>ID_NAT_TYPE_DETECTION_REQUEST</summary>
    NatTypeDetectionRequest = 96,
    /// <summary>ID_NAT_TYPE_DETECTION_RESULT</summary>
    NatTypeDetectionResult = 97,
    /// <summary>ID_ROUTER_2_INTERNAL</summary>
    Router2Internal = 98,
    /// <summary>ID_ROUTER_2_FORWARDING_NO_PATH</summary>
    Router2ForwardingNoPath = 99,
    /// <summary>ID_ROUTER_2_FORWARDING_ESTABLISHED</summary>
    Router2ForwardingEstablished = 100,
    /// <summary>ID_ROUTER_2_REROUTED</summary>
    Router2Rerouted = 101,
    /// <summary>ID_TEAM_BALANCER_INTERNAL</summary>
    TeamBalancerInternal = 102,
    /// <summary>ID_TEAM_BALANCER_REQUESTED_TEAM_FULL</summary>
    TeamBalancerRequestedTeamFull = 103,
    /// <summary>ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED</summary>
    TeamBalancerRequestedTeamLocked = 104,
    /// <summary>ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED</summary>
    TeamBalancerTeamRequestedCancelled = 105,
    /// <summary>ID_TEAM_BALANCER_TEAM_ASSIGNED</summary>
    TeamBalancerTeamAssigned = 106,
    /// <summary>ID_LIGHTSPEED_INTEGRATION</summary>
    LightspeedIntegration = 107,
    /// <summary>ID_XBOX_LOBBY</summary>
    XboxLobby = 108,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_SUCCESS</summary>
    TwoWayAuthenticationIncomingChallengeSuccess = 109,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS</summary>
    TwoWayAuthenticationOutgoingChallengeSuccess = 110,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_FAILURE</summary>
    TwoWayAuthenticationIncomingChallengeFailure = 111,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_FAILURE</summary>
    TwoWayAuthenticationOutgoingChallengeFailure = 112,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_TIMEOUT</summary>
    TwoWayAuthenticationOutgoingChallengeTimeout = 113,
    /// <summary>ID_TWO_WAY_AUTHENTICATION_NEGOTIATION</summary>
    TwoWayAuthenticationNegotiation = 114,
    /// <summary>ID_CLOUD_POST_REQUEST</summary>
    CloudPostRequest = 115,
    /// <summary>ID_CLOUD_RELEASE_REQUEST</summary>
    CloudReleaseRequest = 116,
    /// <summary>ID_CLOUD_GET_REQUEST</summary>
    CloudGetRequest = 117,
    /// <summary>ID_CLOUD_GET_RESPONSE</summary>
    CloudGetResponse = 118,
    /// <summary>ID_CLOUD_UNSUBSCRIBE_REQUEST</summary>
    CloudUnsubscribeRequest = 119,
    /// <summary>ID_CLOUD_SERVER_TO_SERVER_COMMAND</summary>
    CloudServerToServerCommand = 120,
    /// <summary>ID_CLOUD_SUBSCRIPTION_NOTIFICATION</summary>
    CloudSubscriptionNotification = 121,
    /// <summary>ID_LIB_VOICE</summary>
    LibVoice = 122,
    /// <summary>ID_RELAY_PLUGIN</summary>
    RelayPlugin = 123,
    /// <summary>ID_NAT_REQUEST_BOUND_ADDRESSES</summary>
    NatRequestBoundAddresses = 124,
    /// <summary>ID_NAT_RESPOND_BOUND_ADDRESSES</summary>
    NatRespondBoundAddresses = 125,
    /// <summary>ID_FCM2_UPDATE_USER_CONTEXT</summary>
    Fcm2UpdateUserContext = 126,
    /// <summary>ID_RESERVED_3</summary>
    Reserved3 = 127,
    /// <summary>ID_RESERVED_4</summary>
    Reserved4 = 128,
    /// <summary>ID_RESERVED_5</summary>
    Reserved5 = 129,
    /// <summary>ID_RESERVED_6</summary>
    Reserved6 = 130,
    /// <summary>ID_RESERVED_7</summary>
    Reserved7 = 131,
    /// <summary>ID_RESERVED_8</summary>
    Reserved8 = 132,
    /// <summary>ID_RESERVED_9</summary>
    Reserved9 = 133,
    /// <summary>ID_USER_PACKET_ENUM</summary>
    UserPacketEnum = 134,
}

/// <summary>
/// RakNet's OutOfBandIdentifiers (MessageIdentifiers.h:23-37): the byte after ID_OUT_OF_BAND_INTERNAL in the payload of
/// an out-of-band message, used by the NAT and Router2 plugins. Values are positional in the C++ enum.
/// </summary>
public enum OutOfBandId : byte
{
    /// <summary>ID_NAT_ESTABLISH_UNIDIRECTIONAL</summary>
    NatEstablishUnidirectional = 0,
    /// <summary>ID_NAT_ESTABLISH_BIDIRECTIONAL</summary>
    NatEstablishBidirectional = 1,
    /// <summary>ID_NAT_TYPE_DETECT</summary>
    NatTypeDetect = 2,
    /// <summary>ID_ROUTER_2_REPLY_TO_SENDER_PORT</summary>
    Router2ReplyToSenderPort = 3,
    /// <summary>ID_ROUTER_2_REPLY_TO_SPECIFIED_PORT</summary>
    Router2ReplyToSpecifiedPort = 4,
    /// <summary>ID_ROUTER_2_MINI_PUNCH_REPLY</summary>
    Router2MiniPunchReply = 5,
    /// <summary>ID_ROUTER_2_MINI_PUNCH_REPLY_BOUNCE</summary>
    Router2MiniPunchReplyBounce = 6,
    /// <summary>ID_XBOX_360_VOICE</summary>
    Xbox360Voice = 7,
    /// <summary>ID_XBOX_360_GET_NETWORK_ROOM</summary>
    Xbox360GetNetworkRoom = 8,
    /// <summary>ID_XBOX_360_RETURN_NETWORK_ROOM</summary>
    Xbox360ReturnNetworkRoom = 9,
    /// <summary>ID_NAT_PING</summary>
    NatPing = 10,
    /// <summary>ID_NAT_PONG</summary>
    NatPong = 11,
}
