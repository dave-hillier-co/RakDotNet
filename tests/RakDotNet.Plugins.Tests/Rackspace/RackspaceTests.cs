using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Plugins.Tests.EmailSender;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Rackspace;

/// <summary>
/// Rackspace over a TCPInterface polled as Samples/RackspaceConsole polls it, against in-process authentication and
/// server-management servers that record every byte. Expected bytes are derived from Rackspace.cpp.
/// </summary>
public sealed class RackspaceTests : IDisposable
{
    private const string AuthHost = "auth.api.rackspacecloud.com";
    private const string ManagementHost = "servers.api.rackspacecloud.com";

    // Rackspace.cpp:75-81.
    private const string AuthRequest = "GET /v1.0 HTTP/1.1\nHost: auth.api.rackspacecloud.com\nX-Auth-User: user\nX-Auth-Key: key\n\n";

    private const string AuthReply =
        "HTTP/1.1 204 No Content\r\n" +
        "X-Server-Management-Url: https://servers.api.rackspacecloud.com/v1.0/12345\r\n" +
        "X-Storage-Url: https://storage.clouddrive.com/v1/CloudFS_9c83b\r\n" +
        "X-CDN-Management-Url: https://cdn.clouddrive.com/v1/CloudFS_9c83b\r\n" +
        "X-Auth-Token: eaaafd18-0fed-4b3a-81b4-663c99ec1cbb\r\n" +
        "X-Storage-Token: 1f3e2d\r\n\r\n";

    private readonly ScriptedServer _auth = new();
    private readonly ScriptedServer _management = new();
    private readonly Plugins.TcpInterface _tcp = new();
    private readonly Recorder _recorder = new();
    private readonly Plugins.Rackspace _rackspace;

    public RackspaceTests()
    {
        // Samples/RackspaceConsole/main.cpp:221: one connection at a time.
        _tcp.Start(0, 0, 1);
        _rackspace = new Plugins.Rackspace
        {
            ServerEndPoint = host => host switch
            {
                AuthHost => new DnsEndPoint("127.0.0.1", _auth.Port),
                ManagementHost => new DnsEndPoint("127.0.0.1", _management.Port),
                _ => new DnsEndPoint(host, 443),
            },
        };
        _rackspace.AddEventCallback(_recorder);
    }

    public void Dispose()
    {
        _tcp.Dispose();
        _auth.Dispose();
        _management.Dispose();
    }

    [Fact]
    public void Authentication_reads_the_urls_and_tokens_then_runs_the_operation_that_waited()
    {
        _auth.Expect(async c =>
        {
            Assert.Equal(AuthRequest, await c.ReadUntilAsync("\n\n"));
            await c.SendAsync(AuthReply);
            c.Close();
        });
        _management.Expect(async c =>
        {
            Assert.Equal(
                "GET /v1.0/12345/servers HTTP/1.1\nHost: servers.api.rackspacecloud.com\nContent-Type: application/xml\nContent-Length: 0\n" +
                "Accept: application/xml\nX-Auth-Token: eaaafd18-0fed-4b3a-81b4-663c99ec1cbb\n\n",
                await c.ReadUntilAsync("\n\n"));
            await c.SendAsync("HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\n\r\n<servers/>");
            c.Close();
        });

        var address = _rackspace.Authenticate(_tcp, AuthHost, "user", "key");
        _rackspace.ListServers();
        Pump(() => _recorder.Results.Count == 2);

        Assert.Equal(new SystemAddress(IPAddress.Loopback, _auth.Port), address);
        Assert.Equal(
            [("OnAuthenticationResult", RackspaceEventType.Success204, AuthReply),
             ("OnListServersResult", RackspaceEventType.Success200, "HTTP/1.1 200 OK\r\nContent-Type: application/xml\r\n\r\n<servers/>")],
            _recorder.Results);
        Assert.Equal("https://servers.api.rackspacecloud.com/v1.0/12345", _rackspace.ServerManagementUrl);
        Assert.Equal("https://storage.clouddrive.com/v1/CloudFS_9c83b", _rackspace.StorageUrl);
        Assert.Equal("https://cdn.clouddrive.com/v1/CloudFS_9c83b", _rackspace.CdnManagementUrl);
        Assert.Equal("eaaafd18-0fed-4b3a-81b4-663c99ec1cbb", _rackspace.AuthToken);
        Assert.Equal("1f3e2d", _rackspace.StorageToken);
    }

    public static TheoryData<string, string> Operations => new()
    {
        { "ListServersWithDetails", "GET /v1.0/12345/servers/detail" },
        { "CreateServer", "POST /v1.0/12345/servers|<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"game\" imageId=\"8\" flavorId=\"1\"></server>" },
        { "GetServerDetails", "GET /v1.0/12345/servers/42" },
        { "UpdateServerNameOrPassword", "PUT /v1.0/12345/servers/42|<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"renamed\" adminPass=\"pw\" />" },
        { "UpdateServerName", "PUT /v1.0/12345/servers/42|<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"renamed\" />" },
        { "DeleteServer", "DELETE /v1.0/12345/servers/42" },
        { "ListServerAddresses", "GET /v1.0/12345/servers/42/ips" },
        { "ShareServerAddress", "PUT /v1.0/12345/servers/42/ips/public/10.0.0.1" },
        { "DeleteServerAddress", "DELETE /v1.0/12345/servers/42/ips/public/10.0.0.1" },
        { "RebootServer", "POST /v1.0/12345/servers/42/action|<?xml version=\"1.0\" encoding=\"UTF-8\"?><reboot xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" type=\"HARD\"/>" },
        { "RebuildServer", "POST /v1.0/12345/servers/42/action|<?xml version=\"1.0\" encoding=\"UTF-8\"?><rebuild xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" imageId=\"8\"/>" },
        { "ResizeServer", "POST /v1.0/12345/servers/42/action|<?xml version=\"1.0\" encoding=\"UTF-8\"?><resize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" flavorId=\"3\"/>" },
        { "ConfirmResizedServer", "POST /v1.0/12345/servers/42/action|<?xml version=\"1.0\" encoding=\"UTF-8\"?><confirmResize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" />" },
        { "RevertResizedServer", "POST /v1.0/12345/servers/42/action|<?xml version=\"1.0\" encoding=\"UTF-8\"?><revertResize xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" />" },
        { "ListFlavors", "GET /v1.0/12345/flavors" },
        { "GetFlavorDetails", "GET /v1.0/12345/flavors/3" },
        { "ListImages", "GET /v1.0/12345/images" },
        { "CreateImage", "POST /v1.0/12345/images|<?xml version=\"1.0\" encoding=\"UTF-8\"?><image xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"snapshot\" serverId=\"42\"/>" },
        { "GetImageDetails", "GET /v1.0/12345/images/8" },
        { "DeleteImage", "DELETE /v1.0/12345/images/8" },
        { "ListSharedIPGroups", "GET /v1.0/12345/shared_ip_groups" },
        { "ListSharedIPGroupsWithDetails", "GET /v1.0/12345/shared_ip_groups/detail" },
        { "CreateSharedIPGroup", "POST /v1.0/12345/shared_ip_groups|<?xml version=\"1.0\" encoding=\"UTF-8\"?><sharedIpGroup xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"group\"><server id=\"42\"/></sharedIpGroup>" },
        { "CreateSharedIPGroupWithoutServer", "POST /v1.0/12345/shared_ip_groups|<?xml version=\"1.0\" encoding=\"UTF-8\"?><sharedIpGroup xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"group\"></sharedIpGroup>" },
        { "GetSharedIPGroupDetails", "GET /v1.0/12345/shared_ip_groups/7" },
        { "DeleteSharedIPGroup", "DELETE /v1.0/12345/shared_ip_groups/7" },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void Each_operation_sends_its_request_and_reports_to_its_callback(string operation, string expected)
    {
        Authenticate();
        var parts = expected.Split('|');
        var xml = parts.Length > 1 ? parts[1] : "";
        // Rackspace.cpp:584-611: the body, when there is one, sits between blank lines after the headers.
        var request = $"{parts[0]} HTTP/1.1\nHost: servers.api.rackspacecloud.com\nContent-Type: application/xml\nContent-Length: {xml.Length}\n" +
            "Accept: application/xml\nX-Auth-Token: eaaafd18-0fed-4b3a-81b4-663c99ec1cbb\n" + (xml.Length > 0 ? $"\n{xml}\n" : "") + "\n";
        _management.Expect(async c =>
        {
            Assert.Equal(request, await c.ReadUntilAsync(xml + "\n\n"));
            await c.SendAsync("HTTP/1.1 202 Accepted\r\n\r\n");
            c.Close();
        });

        Run(operation);
        Pump(() => _recorder.Results.Count == 1);

        var (name, eventType, html) = Assert.Single(_recorder.Results);
        Assert.Equal(CallbackFor(operation), name);
        Assert.Equal(RackspaceEventType.Success202, eventType);
        Assert.Equal("HTTP/1.1 202 Accepted\r\n\r\n", html);
    }

    [Theory]
    [InlineData("200", RackspaceEventType.Success200, "Success_200")]
    [InlineData("201", RackspaceEventType.Success201, "Success_201")]
    [InlineData("203", RackspaceEventType.Success203, "Success_203")]
    [InlineData("204", RackspaceEventType.Success204, "Success_204")]
    [InlineData("400", RackspaceEventType.BadRequest400, "Bad_Request_400")]
    [InlineData("401", RackspaceEventType.Unauthorized401, "Unauthorized_401")]
    [InlineData("403", RackspaceEventType.ResizeNotAllowed403, "Resize_Not_Allowed_403")]
    [InlineData("404", RackspaceEventType.ItemNotFound404, "Item_Not_Found_404")]
    [InlineData("409", RackspaceEventType.BuildInProgress409, "Build_In_Progress_409")]
    [InlineData("413", RackspaceEventType.OverLimit413, "Over_Limit_413")]
    [InlineData("415", RackspaceEventType.BadMediaType415, "Bad_Media_Type_415")]
    [InlineData("500", RackspaceEventType.CloudServersFault500, "Cloud_Servers_Fault_500")]
    [InlineData("503", RackspaceEventType.ServiceUnavailable503, "Service_Unavailable_503")]
    [InlineData("418", RackspaceEventType.UnknownFailure, "Unknown_Failure")]
    [InlineData("x", RackspaceEventType.UnknownFailure, "Unknown_Failure")]
    public void The_result_is_the_status_after_the_first_HTTP_1_1(string status, RackspaceEventType expected, string name)
    {
        Authenticate();
        _management.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync($"HTTP/1.0 999 ignored\r\nHTTP/1.1 {status} Whatever\r\n\r\n");
            c.Close();
        });

        _rackspace.GetServerDetails("42");
        Pump(() => _recorder.Results.Count == 1);

        Assert.Equal(expected, _recorder.Results.Single().EventType);
        Assert.Equal(name, Plugins.Rackspace.EventTypeToString(expected));
    }

    [Fact]
    public void A_connection_closed_with_no_status_line_reports_that()
    {
        Authenticate();
        _management.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            c.Close();
        });

        _rackspace.ListFlavors();
        Pump(() => _recorder.Results.Count == 1);

        Assert.Equal([("OnListFlavorsResult", RackspaceEventType.ConnectionClosedWithoutResponse, "")], _recorder.Results);
        Assert.Equal("Connection_Closed_Without_Reponse", Plugins.Rackspace.EventTypeToString(RackspaceEventType.ConnectionClosedWithoutResponse));
    }

    [Fact]
    public void A_failed_authentication_leaves_waiting_operations_waiting()
    {
        _auth.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 401 Unauthorized\r\n\r\n");
            c.Close();
        });

        _rackspace.Authenticate(_tcp, AuthHost, "user", "key");
        _rackspace.ListImages();
        Pump(() => _recorder.Results.Count == 1);

        Assert.Equal([("OnAuthenticationResult", RackspaceEventType.Unauthorized401, "HTTP/1.1 401 Unauthorized\r\n\r\n")], _recorder.Results);
        Assert.Empty(_management.Conversations);

        // Authenticating again runs it (Rackspace.cpp:373-382).
        _management.Expect(async c =>
        {
            Assert.StartsWith("GET /v1.0/12345/images HTTP/1.1\n", await c.ReadUntilAsync("\n\n"), StringComparison.Ordinal);
            await c.SendAsync("HTTP/1.1 200 OK\r\n\r\n");
            c.Close();
        });
        Authenticate();
        Pump(() => _recorder.Results.Count == 1);
        Assert.Equal("OnListImagesResult", _recorder.Results[0].Name);
    }

    [Fact]
    public void Authenticate_while_authenticating_returns_the_same_server_and_sends_nothing()
    {
        _auth.Expect(c => c.ReadUntilAsync("\n\n"));

        var first = _rackspace.Authenticate(_tcp, AuthHost, "user", "key");
        var second = _rackspace.Authenticate(_tcp, AuthHost, "other", "other");

        Assert.Equal(first, second);
        Assert.Single(_tcp.GetConnectionList());
    }

    [Fact]
    public void An_unreachable_authentication_server_is_a_connection_attempt_failure()
    {
        var rackspace = new Plugins.Rackspace { ServerEndPoint = _ => new DnsEndPoint("127.0.0.1", ClosedPort()) };
        rackspace.AddEventCallback(_recorder);

        Assert.Equal(SystemAddress.Unassigned, rackspace.Authenticate(_tcp, AuthHost, "user", "key"));
        Assert.Equal([(RackspaceOperationType.ConnectAndAuthenticate, AuthHost)], _recorder.Failures);
    }

    [Fact]
    public void An_operation_with_no_free_connection_is_dropped_as_a_connection_attempt_failure()
    {
        Authenticate();
        _management.Expect(c => c.ReadUntilAsync("\n\n"));

        // The one connection is taken by the first operation (Rackspace.cpp:647-656).
        _rackspace.ListServers();
        _rackspace.ListFlavors();

        Assert.Equal([(RackspaceOperationType.ListFlavors, "https://servers.api.rackspacecloud.com/v1.0/12345")], _recorder.Failures);
    }

    [Fact]
    public void Update_with_neither_a_name_nor_a_password_does_nothing()
    {
        Authenticate();

        _rackspace.UpdateServerNameOrPassword("42", "", "");

        Assert.Empty(_tcp.GetConnectionList());
    }

    [Fact]
    public void Callbacks_run_once_each_in_the_order_added_until_removed()
    {
        var second = new Recorder();
        _rackspace.AddEventCallback(second);
        _rackspace.AddEventCallback(_recorder);
        _rackspace.RemoveEventCallback(second);
        _rackspace.AddEventCallback(second);
        _auth.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(AuthReply);
            c.Close();
        });

        _rackspace.Authenticate(_tcp, AuthHost, "user", "key");
        Pump(() => second.Results.Count == 1);

        Assert.Single(_recorder.Results);
        Assert.Single(second.Results);
    }

    private void Authenticate()
    {
        _auth.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(AuthReply);
            c.Close();
        });
        var before = _recorder.Results.Count;
        _rackspace.Authenticate(_tcp, AuthHost, "user", "key");
        Pump(() => _recorder.Results.Count > before);
        Assert.Equal(RackspaceEventType.Success204, _recorder.Results[^1].EventType);
        _recorder.Results.Clear();
    }

    // Samples/RackspaceConsole/main.cpp:233-240.
    private void Pump(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out; results so far: {string.Join(", ", _recorder.Results)}");
            while (_tcp.Receive() is { } packet)
                _rackspace.OnReceive(packet);
            _rackspace.OnClosedConnection(_tcp.HasLostConnection());
            Thread.Sleep(2);
        }
    }

    private void Run(string operation)
    {
        switch (operation)
        {
            case "ListServersWithDetails": _rackspace.ListServersWithDetails(); break;
            case "CreateServer": _rackspace.CreateServer("game", "8", "1"); break;
            case "GetServerDetails": _rackspace.GetServerDetails("42"); break;
            case "UpdateServerNameOrPassword": _rackspace.UpdateServerNameOrPassword("42", "renamed", "pw"); break;
            case "UpdateServerName": _rackspace.UpdateServerNameOrPassword("42", "renamed", ""); break;
            case "DeleteServer": _rackspace.DeleteServer("42"); break;
            case "ListServerAddresses": _rackspace.ListServerAddresses("42"); break;
            case "ShareServerAddress": _rackspace.ShareServerAddress("42", "10.0.0.1"); break;
            case "DeleteServerAddress": _rackspace.DeleteServerAddress("42", "10.0.0.1"); break;
            case "RebootServer": _rackspace.RebootServer("42", "HARD"); break;
            case "RebuildServer": _rackspace.RebuildServer("42", "8"); break;
            case "ResizeServer": _rackspace.ResizeServer("42", "3"); break;
            case "ConfirmResizedServer": _rackspace.ConfirmResizedServer("42"); break;
            case "RevertResizedServer": _rackspace.RevertResizedServer("42"); break;
            case "ListFlavors": _rackspace.ListFlavors(); break;
            case "GetFlavorDetails": _rackspace.GetFlavorDetails("3"); break;
            case "ListImages": _rackspace.ListImages(); break;
            case "CreateImage": _rackspace.CreateImage("42", "snapshot"); break;
            case "GetImageDetails": _rackspace.GetImageDetails("8"); break;
            case "DeleteImage": _rackspace.DeleteImage("8"); break;
            case "ListSharedIPGroups": _rackspace.ListSharedIPGroups(); break;
            case "ListSharedIPGroupsWithDetails": _rackspace.ListSharedIPGroupsWithDetails(); break;
            case "CreateSharedIPGroup": _rackspace.CreateSharedIPGroup("group", "42"); break;
            case "CreateSharedIPGroupWithoutServer": _rackspace.CreateSharedIPGroup("group", ""); break;
            case "GetSharedIPGroupDetails": _rackspace.GetSharedIPGroupDetails("7"); break;
            case "DeleteSharedIPGroup": _rackspace.DeleteSharedIPGroup("7"); break;
            default: throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static string CallbackFor(string operation) => operation switch
    {
        "ListServersWithDetails" => "OnListServersWithDetailsResult",
        "CreateServer" => "OnCreateServerResult",
        "GetServerDetails" => "OnGetServerDetails",
        "UpdateServerNameOrPassword" or "UpdateServerName" => "OnUpdateServerNameOrPassword",
        "DeleteServer" => "OnDeleteServer",
        "ListServerAddresses" => "OnListServerAddresses",
        "ShareServerAddress" => "OnShareServerAddress",
        "DeleteServerAddress" => "OnDeleteServerAddress",
        "RebootServer" => "OnRebootServer",
        "RebuildServer" => "OnRebuildServer",
        "ResizeServer" => "OnResizeServer",
        "ConfirmResizedServer" => "OnConfirmResizedServer",
        "RevertResizedServer" => "OnRevertResizedServer",
        "ListFlavors" => "OnListFlavorsResult",
        "GetFlavorDetails" => "OnGetFlavorDetailsResult",
        "ListImages" => "OnListImagesResult",
        "CreateImage" => "OnCreateImageResult",
        "GetImageDetails" => "OnGetImageDetailsResult",
        "DeleteImage" => "OnDeleteImageResult",
        "ListSharedIPGroups" => "OnListSharedIPGroups",
        "ListSharedIPGroupsWithDetails" => "OnListSharedIPGroupsWithDetails",
        "CreateSharedIPGroup" or "CreateSharedIPGroupWithoutServer" => "OnCreateSharedIPGroup",
        "GetSharedIPGroupDetails" => "OnGetSharedIPGroupDetails",
        "DeleteSharedIPGroup" => "OnDeleteSharedIPGroup",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
    };

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Samples/RackspaceConsole's DisplayHTMLPage: every result through ExecuteDefault.
    private sealed class Recorder : IRackspaceEventCallback
    {
        public List<(string Name, RackspaceEventType EventType, string Html)> Results { get; } = [];

        public ConcurrentQueue<(RackspaceOperationType Type, string Url)> Failures { get; } = new();

        public void ExecuteDefault(string callbackName, RackspaceEventType eventType, string htmlAdditionalInfo) =>
            Results.Add((callbackName, eventType, htmlAdditionalInfo));

        public void OnConnectionAttemptFailure(RackspaceOperationType operationType, string url) => Failures.Enqueue((operationType, url));
    }
}
