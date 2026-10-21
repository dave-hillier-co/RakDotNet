namespace RakDotNet.Plugins;

/// <summary>RackspaceOperationType (Rackspace.h:56-87): what a <see cref="Rackspace"/> operation does, in RakNet's order.</summary>
public enum RackspaceOperationType
{
    /// <summary>RO_CONNECT_AND_AUTHENTICATE</summary>
    ConnectAndAuthenticate,

    /// <summary>RO_LIST_SERVERS</summary>
    ListServers,

    /// <summary>RO_LIST_SERVERS_WITH_DETAILS</summary>
    ListServersWithDetails,

    /// <summary>RO_CREATE_SERVER</summary>
    CreateServer,

    /// <summary>RO_GET_SERVER_DETAILS</summary>
    GetServerDetails,

    /// <summary>RO_UPDATE_SERVER_NAME_OR_PASSWORD</summary>
    UpdateServerNameOrPassword,

    /// <summary>RO_DELETE_SERVER</summary>
    DeleteServer,

    /// <summary>RO_LIST_SERVER_ADDRESSES</summary>
    ListServerAddresses,

    /// <summary>RO_SHARE_SERVER_ADDRESS</summary>
    ShareServerAddress,

    /// <summary>RO_DELETE_SERVER_ADDRESS</summary>
    DeleteServerAddress,

    /// <summary>RO_REBOOT_SERVER</summary>
    RebootServer,

    /// <summary>RO_REBUILD_SERVER</summary>
    RebuildServer,

    /// <summary>RO_RESIZE_SERVER</summary>
    ResizeServer,

    /// <summary>RO_CONFIRM_RESIZED_SERVER</summary>
    ConfirmResizedServer,

    /// <summary>RO_REVERT_RESIZED_SERVER</summary>
    RevertResizedServer,

    /// <summary>RO_LIST_FLAVORS</summary>
    ListFlavors,

    /// <summary>RO_GET_FLAVOR_DETAILS</summary>
    GetFlavorDetails,

    /// <summary>RO_LIST_IMAGES</summary>
    ListImages,

    /// <summary>RO_CREATE_IMAGE</summary>
    CreateImage,

    /// <summary>RO_GET_IMAGE_DETAILS</summary>
    GetImageDetails,

    /// <summary>RO_DELETE_IMAGE</summary>
    DeleteImage,

    /// <summary>RO_LIST_SHARED_IP_GROUPS</summary>
    ListSharedIPGroups,

    /// <summary>RO_LIST_SHARED_IP_GROUPS_WITH_DETAILS</summary>
    ListSharedIPGroupsWithDetails,

    /// <summary>RO_CREATE_SHARED_IP_GROUP</summary>
    CreateSharedIPGroup,

    /// <summary>RO_GET_SHARED_IP_GROUP_DETAILS</summary>
    GetSharedIPGroupDetails,

    /// <summary>RO_DELETE_SHARED_IP_GROUP</summary>
    DeleteSharedIPGroup,

    /// <summary>RO_NONE</summary>
    None,
}
