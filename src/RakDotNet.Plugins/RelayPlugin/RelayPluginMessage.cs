namespace RakDotNet.Plugins;

/// <summary>
/// RelayPluginEnums (RelayPlugin.h:35-56): the byte after ID_RELAY_PLUGIN, and the results of
/// <see cref="RelayPlugin.AddParticipantOnServer"/>. Values are positional in the C++ enum.
/// </summary>
public enum RelayPluginMessage : byte
{
    /// <summary>RPE_MESSAGE_TO_SERVER_FROM_CLIENT</summary>
    MessageToServerFromClient,

    /// <summary>RPE_ADD_CLIENT_REQUEST_FROM_CLIENT</summary>
    AddClientRequestFromClient,

    /// <summary>RPE_REMOVE_CLIENT_REQUEST_FROM_CLIENT</summary>
    RemoveClientRequestFromClient,

    /// <summary>RPE_GROUP_MESSAGE_FROM_CLIENT</summary>
    GroupMessageFromClient,

    /// <summary>RPE_JOIN_GROUP_REQUEST_FROM_CLIENT</summary>
    JoinGroupRequestFromClient,

    /// <summary>RPE_LEAVE_GROUP_REQUEST_FROM_CLIENT</summary>
    LeaveGroupRequestFromClient,

    /// <summary>RPE_GET_GROUP_LIST_REQUEST_FROM_CLIENT</summary>
    GetGroupListRequestFromClient,

    /// <summary>RPE_MESSAGE_TO_CLIENT_FROM_SERVER</summary>
    MessageToClientFromServer,

    /// <summary>RPE_ADD_CLIENT_NOT_ALLOWED</summary>
    AddClientNotAllowed,

    /// <summary>RPE_ADD_CLIENT_TARGET_NOT_CONNECTED</summary>
    AddClientTargetNotConnected,

    /// <summary>RPE_ADD_CLIENT_NAME_ALREADY_IN_USE</summary>
    AddClientNameAlreadyInUse,

    /// <summary>RPE_ADD_CLIENT_SUCCESS</summary>
    AddClientSuccess,

    /// <summary>RPE_USER_ENTERED_ROOM</summary>
    UserEnteredRoom,

    /// <summary>RPE_USER_LEFT_ROOM</summary>
    UserLeftRoom,

    /// <summary>RPE_GROUP_MSG_FROM_SERVER</summary>
    GroupMsgFromServer,

    /// <summary>RPE_GET_GROUP_LIST_REPLY_FROM_SERVER</summary>
    GetGroupListReplyFromServer,

    /// <summary>RPE_JOIN_GROUP_SUCCESS</summary>
    JoinGroupSuccess,

    /// <summary>RPE_JOIN_GROUP_FAILURE</summary>
    JoinGroupFailure,
}
