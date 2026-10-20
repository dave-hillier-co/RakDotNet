namespace RakDotNet.Plugins;

/// <summary>
/// RackspaceEventType (Rackspace.h:36-54): the result of a <see cref="Rackspace"/> operation, from the HTTP status of
/// its reply, in RakNet's order. <see cref="Rackspace.EventTypeToString"/> gives RakNet's names.
/// </summary>
public enum RackspaceEventType
{
    /// <summary>RET_Success_200</summary>
    Success200,

    /// <summary>RET_Success_201</summary>
    Success201,

    /// <summary>RET_Success_202</summary>
    Success202,

    /// <summary>RET_Success_203</summary>
    Success203,

    /// <summary>RET_Success_204</summary>
    Success204,

    /// <summary>RET_Cloud_Servers_Fault_500</summary>
    CloudServersFault500,

    /// <summary>RET_Service_Unavailable_503</summary>
    ServiceUnavailable503,

    /// <summary>RET_Unauthorized_401</summary>
    Unauthorized401,

    /// <summary>RET_Bad_Request_400</summary>
    BadRequest400,

    /// <summary>RET_Over_Limit_413</summary>
    OverLimit413,

    /// <summary>RET_Bad_Media_Type_415</summary>
    BadMediaType415,

    /// <summary>RET_Item_Not_Found_404</summary>
    ItemNotFound404,

    /// <summary>RET_Build_In_Progress_409</summary>
    BuildInProgress409,

    /// <summary>RET_Resize_Not_Allowed_403</summary>
    ResizeNotAllowed403,

    /// <summary>RET_Connection_Closed_Without_Reponse: the connection closed before an HTTP/1.1 status line arrived</summary>
    ConnectionClosedWithoutResponse,

    /// <summary>RET_Unknown_Failure: a status RakNet does not list</summary>
    UnknownFailure,
}
