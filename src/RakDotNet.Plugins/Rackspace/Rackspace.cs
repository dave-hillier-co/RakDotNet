using System.Globalization;
using System.Net;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// Rackspace: the Rackspace Cloud Servers API v1.0 over a started <see cref="TcpInterface"/> the application owns and
/// polls (Rackspace.cpp). Each operation is an HTTP/1.1 request on a connection of its own, and its result is the
/// status of what the server sent by the time it closed that connection.
/// </summary>
/// <remarks>
/// <para>
/// Pass every packet <see cref="TcpInterface.Receive"/> returns to <see cref="OnReceive"/> and every address
/// <see cref="TcpInterface.HasLostConnection"/> returns to <see cref="OnClosedConnection"/>, as
/// Samples/RackspaceConsole does. Operations asked for while authentication is in progress wait for it, and run when it
/// succeeds.
/// </para>
/// <para>
/// Received data is matched to operations by the server's address, so every operation open to one server gets all of
/// its data, and one that connects to the address of a connection just closed is completed with it. Requests are
/// written as UTF-8; replies are read up to each packet's first zero byte and given to the callbacks as UTF-8.
/// </para>
/// </remarks>
public sealed class Rackspace
{
    private const ushort ApiPort = 443;

    private readonly List<IRackspaceEventCallback> _eventCallbacks = [];
    private readonly List<Operation> _operations = [];
    private TcpInterface? _tcpInterface;
    private string _serverManagementUrl = "";
    private string _serverManagementDomain = "";
    private string _serverManagementPath = "";
    private string _authToken = "";

    /// <summary>RakNet's OPEN_SSL_CLIENT_SUPPORT build: each connection continues over TLS, as the Rackspace API requires.</summary>
    public bool UseSsl { get; init; }

    /// <summary>
    /// Where a connection to a host goes. RakNet connects to the host on port 443; this gives that endpoint unless
    /// replaced. The host still names the server in the request and is the TLS target name.
    /// </summary>
    public Func<string, DnsEndPoint> ServerEndPoint { get; init; } = host => new DnsEndPoint(host, ApiPort);

    /// <summary>X-Server-Management-Url from the last successful authentication.</summary>
    public string ServerManagementUrl => _serverManagementUrl;

    /// <summary>X-Storage-Url from the last successful authentication.</summary>
    public string StorageUrl { get; private set; } = "";

    /// <summary>X-CDN-Management-Url from the last successful authentication.</summary>
    public string CdnManagementUrl { get; private set; } = "";

    /// <summary>X-Auth-Token from the last successful authentication, sent with every operation.</summary>
    public string AuthToken => _authToken;

    /// <summary>X-Storage-Token from the last successful authentication.</summary>
    public string StorageToken { get; private set; } = "";

    /// <summary>
    /// Authenticate: connects to <paramref name="authenticationUrl"/> and asks for a token; the result goes to
    /// <see cref="IRackspaceEventCallback.OnAuthenticationResult"/>. Returns the server's address, the one in progress
    /// when authentication already is, or Unassigned when the connection failed (Rackspace.cpp:46-87).
    /// </summary>
    public SystemAddress Authenticate(TcpInterface tcpInterface, string authenticationUrl, string rackspaceCloudUsername, string apiAccessKey)
    {
        ArgumentNullException.ThrowIfNull(tcpInterface);
        ArgumentNullException.ThrowIfNull(authenticationUrl);
        ArgumentNullException.ThrowIfNull(rackspaceCloudUsername);
        ArgumentNullException.ThrowIfNull(apiAccessKey);
        var inProgress = _operations.Find(o => o.Type == RackspaceOperationType.ConnectAndAuthenticate);
        if (inProgress is not null)
            return inProgress.ConnectionAddress;

        _tcpInterface = tcpInterface;
        var operation = new Operation(RackspaceOperationType.ConnectAndAuthenticate, "", "", "")
        {
            ConnectionAddress = Connect(authenticationUrl),
        };
        if (operation.ConnectionAddress == SystemAddress.Unassigned)
        {
            foreach (var callback in _eventCallbacks.ToArray())
                callback.OnConnectionAttemptFailure(RackspaceOperationType.ConnectAndAuthenticate, authenticationUrl);
            return SystemAddress.Unassigned;
        }

        var command = $"GET /v1.0 HTTP/1.1\nHost: {authenticationUrl}\nX-Auth-User: {rackspaceCloudUsername}\nX-Auth-Key: {apiAccessKey}\n\n";
        tcpInterface.Send(Encoding.UTF8.GetBytes(command), operation.ConnectionAddress, broadcast: false);
        _operations.Add(operation);
        return operation.ConnectionAddress;
    }

    /// <summary>ListServers: GET servers.</summary>
    public void ListServers() => AddOperation(RackspaceOperationType.ListServers, "GET", "servers", "");

    /// <summary>ListServersWithDetails: GET servers/detail.</summary>
    public void ListServersWithDetails() => AddOperation(RackspaceOperationType.ListServersWithDetails, "GET", "servers/detail", "");

    /// <summary>CreateServer: POST servers with the server's name, image and flavor.</summary>
    public void CreateServer(string name, string imageId, string flavorId) =>
        AddOperation(RackspaceOperationType.CreateServer, "POST", "servers",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"{name}\" imageId=\"{imageId}\" flavorId=\"{flavorId}\"></server>");

    /// <summary>GetServerDetails: GET servers/id.</summary>
    public void GetServerDetails(string serverId) => AddOperation(RackspaceOperationType.GetServerDetails, "GET", $"servers/{serverId}", "");

    /// <summary>UpdateServerNameOrPassword: PUT servers/id with whichever is not empty; nothing when both are.</summary>
    public void UpdateServerNameOrPassword(string serverId, string newName, string newPassword)
    {
        ArgumentNullException.ThrowIfNull(newName);
        ArgumentNullException.ThrowIfNull(newPassword);
        if (newName.Length == 0 && newPassword.Length == 0)
            return;
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\"");
        if (newName.Length != 0)
            xml.Append(CultureInfo.InvariantCulture, $" name=\"{newName}\"");
        if (newPassword.Length != 0)
            xml.Append(CultureInfo.InvariantCulture, $" adminPass=\"{newPassword}\"");
        xml.Append(" />");
        AddOperation(RackspaceOperationType.UpdateServerNameOrPassword, "PUT", $"servers/{serverId}", xml.ToString());
    }

    /// <summary>DeleteServer: DELETE servers/id.</summary>
    public void DeleteServer(string serverId) => AddOperation(RackspaceOperationType.DeleteServer, "DELETE", $"servers/{serverId}", "");

    /// <summary>ListServerAddresses: GET servers/id/ips.</summary>
    public void ListServerAddresses(string serverId) => AddOperation(RackspaceOperationType.ListServerAddresses, "GET", $"servers/{serverId}/ips", "");

    /// <summary>ShareServerAddress: PUT servers/id/ips/public/address.</summary>
    public void ShareServerAddress(string serverId, string ipAddress) =>
        AddOperation(RackspaceOperationType.ShareServerAddress, "PUT", $"servers/{serverId}/ips/public/{ipAddress}", "");

    /// <summary>DeleteServerAddress: DELETE servers/id/ips/public/address.</summary>
    public void DeleteServerAddress(string serverId, string ipAddress) =>
        AddOperation(RackspaceOperationType.DeleteServerAddress, "DELETE", $"servers/{serverId}/ips/public/{ipAddress}", "");

    /// <summary>RebootServer: POST servers/id/action with a reboot of type HARD or SOFT.</summary>
    public void RebootServer(string serverId, string rebootType) =>
        AddOperation(RackspaceOperationType.RebootServer, "POST", $"servers/{serverId}/action",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><reboot xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" type=\"{rebootType}\"/>");

    /// <summary>RebuildServer: POST servers/id/action with a rebuild from the image.</summary>
    public void RebuildServer(string serverId, string imageId) =>
        AddOperation(RackspaceOperationType.RebuildServer, "POST", $"servers/{serverId}/action",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><rebuild xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" imageId=\"{imageId}\"/>");

    /// <summary>ResizeServer: POST servers/id/action with a resize to the flavor.</summary>
    public void ResizeServer(string serverId, string flavorId) =>
        AddOperation(RackspaceOperationType.ResizeServer, "POST", $"servers/{serverId}/action",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><resize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" flavorId=\"{flavorId}\"/>");

    /// <summary>ConfirmResizedServer: POST servers/id/action with confirmResize.</summary>
    public void ConfirmResizedServer(string serverId) =>
        AddOperation(RackspaceOperationType.ConfirmResizedServer, "POST", $"servers/{serverId}/action",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><confirmResize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" />");

    /// <summary>RevertResizedServer: POST servers/id/action with revertResize.</summary>
    public void RevertResizedServer(string serverId) =>
        AddOperation(RackspaceOperationType.RevertResizedServer, "POST", $"servers/{serverId}/action",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><revertResize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" />");

    /// <summary>ListFlavors: GET flavors.</summary>
    public void ListFlavors() => AddOperation(RackspaceOperationType.ListFlavors, "GET", "flavors", "");

    /// <summary>GetFlavorDetails: GET flavors/id.</summary>
    public void GetFlavorDetails(string flavorId) => AddOperation(RackspaceOperationType.GetFlavorDetails, "GET", $"flavors/{flavorId}", "");

    /// <summary>ListImages: GET images.</summary>
    public void ListImages() => AddOperation(RackspaceOperationType.ListImages, "GET", "images", "");

    /// <summary>CreateImage: POST images with an image of the server.</summary>
    public void CreateImage(string serverId, string imageName) =>
        AddOperation(RackspaceOperationType.CreateImage, "POST", "images",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><image xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"{imageName}\" serverId=\"{serverId}\"/>");

    /// <summary>GetImageDetails: GET images/id.</summary>
    public void GetImageDetails(string imageId) => AddOperation(RackspaceOperationType.GetImageDetails, "GET", $"images/{imageId}", "");

    /// <summary>DeleteImage: DELETE images/id.</summary>
    public void DeleteImage(string imageId) => AddOperation(RackspaceOperationType.DeleteImage, "DELETE", $"images/{imageId}", "");

    /// <summary>ListSharedIPGroups: GET shared_ip_groups.</summary>
    public void ListSharedIPGroups() => AddOperation(RackspaceOperationType.ListSharedIPGroups, "GET", "shared_ip_groups", "");

    /// <summary>ListSharedIPGroupsWithDetails: GET shared_ip_groups/detail.</summary>
    public void ListSharedIPGroupsWithDetails() =>
        AddOperation(RackspaceOperationType.ListSharedIPGroupsWithDetails, "GET", "shared_ip_groups/detail", "");

    /// <summary>CreateSharedIPGroup: POST shared_ip_groups, with the server when <paramref name="optionalServerId"/> is not empty.</summary>
    public void CreateSharedIPGroup(string name, string optionalServerId)
    {
        ArgumentNullException.ThrowIfNull(optionalServerId);
        var xml = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><sharedIpGroup xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"{name}\">";
        if (optionalServerId.Length != 0)
            xml += $"<server id=\"{optionalServerId}\"/>";
        xml += "</sharedIpGroup>";
        AddOperation(RackspaceOperationType.CreateSharedIPGroup, "POST", "shared_ip_groups", xml);
    }

    /// <summary>GetSharedIPGroupDetails: GET shared_ip_groups/id.</summary>
    public void GetSharedIPGroupDetails(string groupId) =>
        AddOperation(RackspaceOperationType.GetSharedIPGroupDetails, "GET", $"shared_ip_groups/{groupId}", "");

    /// <summary>DeleteSharedIPGroup: DELETE shared_ip_groups/id.</summary>
    public void DeleteSharedIPGroup(string groupId) =>
        AddOperation(RackspaceOperationType.DeleteSharedIPGroup, "DELETE", $"shared_ip_groups/{groupId}", "");

    /// <summary>AddEventCallback: once each; callbacks run in the order added.</summary>
    public void AddEventCallback(IRackspaceEventCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!_eventCallbacks.Contains(callback))
            _eventCallbacks.Add(callback);
    }

    /// <summary>RemoveEventCallback</summary>
    public void RemoveEventCallback(IRackspaceEventCallback callback) => _eventCallbacks.Remove(callback);

    /// <summary>ClearEventCallbacks</summary>
    public void ClearEventCallbacks() => _eventCallbacks.Clear();

    /// <summary>
    /// AddOperation: an operation with a request of the caller's own. It waits while authentication is in progress;
    /// otherwise it connects and sends at once, and is dropped if it cannot connect (Rackspace.cpp:143-157).
    /// </summary>
    public void AddOperation(RackspaceOperationType type, string httpCommand, string operation, string xml)
    {
        ArgumentNullException.ThrowIfNull(httpCommand);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(xml);
        var pending = new Operation(type, httpCommand, operation, xml)
        {
            IsPendingAuthentication = _operations.Exists(o => o.Type == RackspaceOperationType.ConnectAndAuthenticate),
        };
        if (pending.IsPendingAuthentication || ExecuteOperation(pending))
            _operations.Add(pending);
    }

    /// <summary>OnReceive: adds the packet's data to every running operation connected to its address (Rackspace.cpp:571-581).</summary>
    public void OnReceive(TcpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var data = packet.Data.Span;
        var end = data.IndexOf((byte)0);
        var text = end >= 0 ? data[..end] : data;
        foreach (var operation in _operations)
        {
            if (!operation.IsPendingAuthentication && operation.ConnectionAddress == packet.Address)
                operation.IncomingStream.Write(text);
        }
    }

    /// <summary>
    /// OnClosedConnection: completes every running operation connected to the address with the status of what it
    /// received, and tells the callbacks. A successful authentication (204) reads the URLs and tokens and runs the
    /// operations that were waiting for it (Rackspace.cpp:296-570).
    /// </summary>
    public void OnClosedConnection(SystemAddress systemAddress)
    {
        if (systemAddress == SystemAddress.Unassigned)
            return;
        var index = 0;
        while (index < _operations.Count)
        {
            var operation = _operations[index];
            if (operation.IsPendingAuthentication || operation.ConnectionAddress != systemAddress)
            {
                index++;
                continue;
            }
            _operations.RemoveAt(index);

            var packetData = Encoding.UTF8.GetString(operation.IncomingStream.ToArray());
            var eventType = ReadStatus(packetData);
            var callbacks = _eventCallbacks.ToArray();
            if (operation.Type == RackspaceOperationType.ConnectAndAuthenticate)
            {
                if (eventType == RackspaceEventType.Success204)
                {
                    ReadAuthentication(packetData);
                    RunWaitingOperations();
                    // RakNet starts the scan again from the first operation (Rackspace.cpp:383-384).
                    index = 0;
                }
                foreach (var callback in callbacks)
                    callback.OnAuthenticationResult(eventType, packetData);
                continue;
            }
            foreach (var callback in callbacks)
                Report(callback, operation.Type, eventType, packetData);
        }
    }

    /// <summary>EventTypeToString: RakNet's name for the result, spelling included.</summary>
    public static string EventTypeToString(RackspaceEventType eventType) => eventType switch
    {
        RackspaceEventType.Success200 => "Success_200",
        RackspaceEventType.Success201 => "Success_201",
        RackspaceEventType.Success202 => "Success_202",
        RackspaceEventType.Success203 => "Success_203",
        RackspaceEventType.Success204 => "Success_204",
        RackspaceEventType.CloudServersFault500 => "Cloud_Servers_Fault_500",
        RackspaceEventType.ServiceUnavailable503 => "Service_Unavailable_503",
        RackspaceEventType.Unauthorized401 => "Unauthorized_401",
        RackspaceEventType.BadRequest400 => "Bad_Request_400",
        RackspaceEventType.OverLimit413 => "Over_Limit_413",
        RackspaceEventType.BadMediaType415 => "Bad_Media_Type_415",
        RackspaceEventType.ItemNotFound404 => "Item_Not_Found_404",
        RackspaceEventType.BuildInProgress409 => "Build_In_Progress_409",
        RackspaceEventType.ResizeNotAllowed403 => "Resize_Not_Allowed_403",
        RackspaceEventType.ConnectionClosedWithoutResponse => "Connection_Closed_Without_Reponse",
        RackspaceEventType.UnknownFailure => "Unknown_Failure",
        _ => "Unknown event type (bug)",
    };

    // Rackspace.cpp:319-357: the digits after the first "HTTP/1.1 ", at most 31 of them, read by atoi.
    private static RackspaceEventType ReadStatus(string packetData)
    {
        const string statusLine = "HTTP/1.1 ";
        var index = packetData.IndexOf(statusLine, StringComparison.Ordinal);
        if (index < 0)
            return RackspaceEventType.ConnectionClosedWithoutResponse;
        var start = index + statusLine.Length;
        var end = start;
        while (end < packetData.Length && end - start < 31 && packetData[end] is >= '0' and <= '9')
            end++;
        return ConsoleText.Atoi(packetData[start..end]) switch
        {
            200 => RackspaceEventType.Success200,
            201 => RackspaceEventType.Success201,
            202 => RackspaceEventType.Success202,
            203 => RackspaceEventType.Success203,
            204 => RackspaceEventType.Success204,
            500 => RackspaceEventType.CloudServersFault500,
            503 => RackspaceEventType.ServiceUnavailable503,
            401 => RackspaceEventType.Unauthorized401,
            400 => RackspaceEventType.BadRequest400,
            413 => RackspaceEventType.OverLimit413,
            415 => RackspaceEventType.BadMediaType415,
            404 => RackspaceEventType.ItemNotFound404,
            409 => RackspaceEventType.BuildInProgress409,
            403 => RackspaceEventType.ResizeNotAllowed403,
            _ => RackspaceEventType.UnknownFailure,
        };
    }

    // Rackspace.cpp:361-371.
    private void ReadAuthentication(string packetData)
    {
        _serverManagementUrl = ReadLine(packetData, "X-Server-Management-Url: ");
        (_, _serverManagementDomain, _serverManagementPath) = HttpFormat.SplitUri(_serverManagementUrl);
        StorageUrl = ReadLine(packetData, "X-Storage-Url: ");
        CdnManagementUrl = ReadLine(packetData, "X-CDN-Management-Url: ");
        _authToken = ReadLine(packetData, "X-Auth-Token: ");
        StorageToken = ReadLine(packetData, "X-Storage-Token: ");
    }

    // Rackspace.cpp:373-382: each waiting operation runs, and is dropped if it cannot connect.
    private void RunWaitingOperations()
    {
        var index = 0;
        while (index < _operations.Count)
        {
            var operation = _operations[index];
            if (!operation.IsPendingAuthentication)
            {
                index++;
                continue;
            }
            operation.IsPendingAuthentication = false;
            if (ExecuteOperation(operation))
                index++;
            else
                _operations.RemoveAt(index);
        }
    }

    // ReadLine (Rackspace.cpp:619-646): the text after the first occurrence of the name, up to the end of its line;
    // empty when the name is missing.
    private static string ReadLine(string data, string name)
    {
        var index = data.IndexOf(name, StringComparison.Ordinal);
        if (index < 0)
            return "";
        var start = index + name.Length;
        var end = data.AsSpan(start).IndexOfAny('\r', '\n');
        return end < 0 ? data[start..] : data.Substring(start, end);
    }

    // ExecuteOperation (Rackspace.cpp:582-618).
    private bool ExecuteOperation(Operation operation)
    {
        var tcp = _tcpInterface ?? throw new InvalidOperationException("Authenticate first: Rackspace has no TcpInterface.");
        operation.ConnectionAddress = Connect(_serverManagementDomain);
        if (operation.ConnectionAddress == SystemAddress.Unassigned)
        {
            foreach (var callback in _eventCallbacks.ToArray())
                callback.OnConnectionAttemptFailure(operation.Type, _serverManagementUrl);
            return false;
        }

        var xmlLength = Encoding.UTF8.GetByteCount(operation.Xml);
        var command = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"{operation.HttpCommand} {_serverManagementPath}/{operation.OperationPath} HTTP/1.1\n")
            .Append(CultureInfo.InvariantCulture, $"Host: {_serverManagementDomain}\n")
            .Append("Content-Type: application/xml\n")
            .Append(CultureInfo.InvariantCulture, $"Content-Length: {xmlLength}\n")
            .Append("Accept: application/xml\n")
            .Append(CultureInfo.InvariantCulture, $"X-Auth-Token: {_authToken}\n");
        if (operation.Xml.Length != 0)
            command.Append('\n').Append(operation.Xml).Append('\n');
        command.Append('\n');
        tcp.Send(Encoding.UTF8.GetBytes(command.ToString()), operation.ConnectionAddress, broadcast: false);
        return true;
    }

    // A blocking connect to where the host goes, then TLS when built with it (Rackspace.cpp:63-73, 647-665).
    private SystemAddress Connect(string host)
    {
        var tcp = _tcpInterface!;
        var endPoint = ServerEndPoint(host);
        var address = tcp.Connect(endPoint.Host, (ushort)endPoint.Port, block: true);
        if (address != SystemAddress.Unassigned && UseSsl)
            tcp.StartSslClient(address, host);
        return address;
    }

    private static void Report(IRackspaceEventCallback callback, RackspaceOperationType type, RackspaceEventType eventType, string html)
    {
        switch (type)
        {
            case RackspaceOperationType.ListServers: callback.OnListServersResult(eventType, html); break;
            case RackspaceOperationType.ListServersWithDetails: callback.OnListServersWithDetailsResult(eventType, html); break;
            case RackspaceOperationType.CreateServer: callback.OnCreateServerResult(eventType, html); break;
            case RackspaceOperationType.GetServerDetails: callback.OnGetServerDetails(eventType, html); break;
            case RackspaceOperationType.UpdateServerNameOrPassword: callback.OnUpdateServerNameOrPassword(eventType, html); break;
            case RackspaceOperationType.DeleteServer: callback.OnDeleteServer(eventType, html); break;
            case RackspaceOperationType.ListServerAddresses: callback.OnListServerAddresses(eventType, html); break;
            case RackspaceOperationType.ShareServerAddress: callback.OnShareServerAddress(eventType, html); break;
            case RackspaceOperationType.DeleteServerAddress: callback.OnDeleteServerAddress(eventType, html); break;
            case RackspaceOperationType.RebootServer: callback.OnRebootServer(eventType, html); break;
            case RackspaceOperationType.RebuildServer: callback.OnRebuildServer(eventType, html); break;
            case RackspaceOperationType.ResizeServer: callback.OnResizeServer(eventType, html); break;
            case RackspaceOperationType.ConfirmResizedServer: callback.OnConfirmResizedServer(eventType, html); break;
            case RackspaceOperationType.RevertResizedServer: callback.OnRevertResizedServer(eventType, html); break;
            case RackspaceOperationType.ListFlavors: callback.OnListFlavorsResult(eventType, html); break;
            case RackspaceOperationType.GetFlavorDetails: callback.OnGetFlavorDetailsResult(eventType, html); break;
            case RackspaceOperationType.ListImages: callback.OnListImagesResult(eventType, html); break;
            case RackspaceOperationType.CreateImage: callback.OnCreateImageResult(eventType, html); break;
            case RackspaceOperationType.GetImageDetails: callback.OnGetImageDetailsResult(eventType, html); break;
            case RackspaceOperationType.DeleteImage: callback.OnDeleteImageResult(eventType, html); break;
            case RackspaceOperationType.ListSharedIPGroups: callback.OnListSharedIPGroups(eventType, html); break;
            case RackspaceOperationType.ListSharedIPGroupsWithDetails: callback.OnListSharedIPGroupsWithDetails(eventType, html); break;
            case RackspaceOperationType.CreateSharedIPGroup: callback.OnCreateSharedIPGroup(eventType, html); break;
            case RackspaceOperationType.GetSharedIPGroupDetails: callback.OnGetSharedIPGroupDetails(eventType, html); break;
            case RackspaceOperationType.DeleteSharedIPGroup: callback.OnDeleteSharedIPGroup(eventType, html); break;
            default: break;
        }
    }

    // RackspaceOperation (Rackspace.h:367-377).
    private sealed class Operation(RackspaceOperationType type, string httpCommand, string operationPath, string xml)
    {
        public RackspaceOperationType Type { get; } = type;

        public SystemAddress ConnectionAddress { get; set; } = SystemAddress.Unassigned;

        public bool IsPendingAuthentication { get; set; }

        public MemoryStream IncomingStream { get; } = new();

        public string HttpCommand { get; } = httpCommand;

        public string OperationPath { get; } = operationPath;

        public string Xml { get; } = xml;
    }
}
