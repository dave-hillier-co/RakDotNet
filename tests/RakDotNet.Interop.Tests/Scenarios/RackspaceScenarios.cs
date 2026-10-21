using System.Text;
using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Rackspace on the client peer over a TCPInterface with one connection, polled as Samples/RackspaceConsole polls it,
/// against two server peers playing the authentication and server-management hosts, which check every request byte for
/// byte. Each host's connections are redirected from port 443 to its peer. The console's commands become a fixed
/// sequence: Authenticate, operations that wait for it, then operations after it.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class RackspaceScenarios
{
    private const string AuthHost = "auth.api.rackspacecloud.com";
    private const string ManagementHost = "servers.api.rackspacecloud.com";
    private const string Token = "eaaafd18-0fed-4b3a-81b4-663c99ec1cbb";

    private const string AuthReply =
        "HTTP/1.1 204 No Content\r\n" +
        "X-Server-Management-Url: https://servers.api.rackspacecloud.com/v1.0/12345\r\n" +
        "X-Storage-Url: https://storage.clouddrive.com/v1/CloudFS_9c83b\r\n" +
        "X-CDN-Management-Url: https://cdn.clouddrive.com/v1/CloudFS_9c83b\r\n" +
        "X-Auth-Token: " + Token + "\r\n" +
        "X-Storage-Token: 1f3e2d\r\n\r\n";

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Authentication_runs_the_operation_that_waited_then_later_operations_send_their_xml(PeerKind client, PeerKind server)
    {
        await using var auth = await TcpPeer.LaunchAsync(server, "authentication server", Cancel);
        await using var management = await TcpPeer.LaunchAsync(server, "management server", Cancel);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Cancel);
        await auth.ListenAsync(cancellationToken: Cancel);
        await management.ListenAsync(cancellationToken: Cancel);
        var services = (IServicesDriver)clientDriver;

        Assert.Equal(auth.EndPoint, await services.AuthenticateRackspaceAsync(Authentication(auth, management), Cancel));
        await services.RackspaceOperationAsync("ListServers", [], Cancel);

        // Rackspace.cpp:75-81.
        var authConnection = await ServiceScript.AcceptAsync(auth, Cancel);
        await ServiceScript.ExchangeAsync(auth, authConnection,
            "GET /v1.0 HTTP/1.1\nHost: auth.api.rackspacecloud.com\nX-Auth-User: user\nX-Auth-Key: key\n\n", AuthReply, Cancel);
        await auth.CloseWhenSentAsync(authConnection, Timeout, Cancel);

        // The result arrives when the connection closes, and the waiting operation runs (Rackspace.cpp:358-391).
        var authenticated = await PollUntilAsync(services, 1);
        Assert.Equal([("OnAuthenticationResult", 4, "Success_204", AuthReply)], authenticated.Select(Describe));
        var list = await ServiceScript.AcceptAsync(management, Cancel);
        await ServiceScript.ExchangeAsync(management, list, Request("GET", "servers", ""), "HTTP/1.1 200 OK\r\n\r\n<servers/>", Cancel);
        await management.CloseWhenSentAsync(list, Timeout, Cancel);
        Assert.Equal([("OnListServersResult", 0, "Success_200", "HTTP/1.1 200 OK\r\n\r\n<servers/>")], (await PollUntilAsync(services, 1)).Select(Describe));

        // Rackspace.cpp:162-170, 582-611.
        await services.RackspaceOperationAsync("CreateServer", ["game", "8", "1"], Cancel);
        var create = await ServiceScript.AcceptAsync(management, Cancel);
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><server xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" name=\"game\" imageId=\"8\" flavorId=\"1\"></server>";
        await ServiceScript.ExchangeAsync(management, create, Request("POST", "servers", xml), "HTTP/1.1 202 Accepted\r\n\r\n", Cancel);
        await management.CloseWhenSentAsync(create, Timeout, Cancel);
        Assert.Equal([("OnCreateServerResult", 2, "Success_202", "HTTP/1.1 202 Accepted\r\n\r\n")], (await PollUntilAsync(services, 1)).Select(Describe));

        // A closed connection with no status line, and an unknown status.
        await services.RackspaceOperationAsync("RebootServer", ["42", "HARD"], Cancel);
        var reboot = await ServiceScript.AcceptAsync(management, Cancel);
        await ServiceScript.ExpectAsync(management, reboot, Request("POST", "servers/42/action",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><reboot xmlns=\"http://docs.rackspacecloud.com/servers/api/v1.0\" type=\"HARD\"/>"), Cancel);
        await management.Tcp.TcpCloseAsync(reboot, Cancel);
        Assert.Equal([("OnRebootServer", 14, "Connection_Closed_Without_Reponse", "")], (await PollUntilAsync(services, 1)).Select(Describe));

        await services.RackspaceOperationAsync("DeleteImage", ["8"], Cancel);
        var delete = await ServiceScript.AcceptAsync(management, Cancel);
        await ServiceScript.ExchangeAsync(management, delete, Request("DELETE", "images/8", ""), "HTTP/1.1 418 Teapot\r\n\r\n", Cancel);
        await management.CloseWhenSentAsync(delete, Timeout, Cancel);
        Assert.Equal([("OnDeleteImageResult", 15, "Unknown_Failure", "HTTP/1.1 418 Teapot\r\n\r\n")], (await PollUntilAsync(services, 1)).Select(Describe));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_refused_authentication_keeps_the_operation_waiting_until_one_succeeds(PeerKind client, PeerKind server)
    {
        await using var auth = await TcpPeer.LaunchAsync(server, "authentication server", Cancel);
        await using var management = await TcpPeer.LaunchAsync(server, "management server", Cancel);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Cancel);
        await auth.ListenAsync(cancellationToken: Cancel);
        await management.ListenAsync(cancellationToken: Cancel);
        var services = (IServicesDriver)clientDriver;

        await services.AuthenticateRackspaceAsync(Authentication(auth, management), Cancel);
        await services.RackspaceOperationAsync("ListImages", [], Cancel);
        var first = await ServiceScript.AcceptAsync(auth, Cancel);
        await ServiceScript.ReceiveUntilAsync(auth, first, "\n\n", Cancel);
        await ServiceScript.SendAsync(auth, first, "HTTP/1.1 401 Unauthorized\r\n\r\n", Cancel);
        await auth.CloseWhenSentAsync(first, Timeout, Cancel);
        Assert.Equal([("OnAuthenticationResult", 7, "Unauthorized_401", "HTTP/1.1 401 Unauthorized\r\n\r\n")], (await PollUntilAsync(services, 1)).Select(Describe));

        // Authenticating again runs the waiting operation (Rackspace.cpp:373-382).
        await services.AuthenticateRackspaceAsync(Authentication(auth, management), Cancel);
        var second = await ServiceScript.AcceptAsync(auth, Cancel);
        await ServiceScript.ReceiveUntilAsync(auth, second, "\n\n", Cancel);
        await ServiceScript.SendAsync(auth, second, AuthReply, Cancel);
        await auth.CloseWhenSentAsync(second, Timeout, Cancel);
        var poll = await PollAsync(services, p => p.Results.Count > 0);
        Assert.Equal([("OnAuthenticationResult", 4, "Success_204", AuthReply)], poll.Results.Select(Describe));
        Assert.Empty(poll.Failures);
        var images = await ServiceScript.AcceptAsync(management, Cancel);
        await ServiceScript.ExchangeAsync(management, images, Request("GET", "images", ""), "HTTP/1.1 200 OK\r\n\r\n<images/>", Cancel);
        await management.CloseWhenSentAsync(images, Timeout, Cancel);
        Assert.Equal([("OnListImagesResult", 0, "Success_200", "HTTP/1.1 200 OK\r\n\r\n<images/>")], (await PollUntilAsync(services, 1)).Select(Describe));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task An_unreachable_authentication_server_is_a_connection_attempt_failure(PeerKind client, PeerKind server)
    {
        await using var auth = await TcpPeer.LaunchAsync(server, "authentication server", Cancel);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Cancel);
        await auth.ListenAsync(cancellationToken: Cancel);
        var endPoint = auth.EndPoint;
        await auth.Tcp.StopTcpAsync(Cancel);
        var services = (IServicesDriver)clientDriver;

        Assert.Null(await services.AuthenticateRackspaceAsync(
            new RackspaceAuthentication(AuthHost, "user", "key", [new ServiceRedirect(AuthHost, endPoint)]), Cancel));
        var poll = await services.PollRackspaceAsync(Cancel);
        Assert.Empty(poll.Results);
        Assert.Equal([new RackspaceFailure(0, AuthHost)], poll.Failures);
    }

    private static RackspaceAuthentication Authentication(TcpPeer auth, TcpPeer management) =>
        new(AuthHost, "user", "key", [new ServiceRedirect(AuthHost, auth.EndPoint), new ServiceRedirect(ManagementHost, management.EndPoint)]);

    // Rackspace.cpp:584-611.
    private static string Request(string method, string operation, string xml) =>
        $"{method} /v1.0/12345/{operation} HTTP/1.1\nHost: servers.api.rackspacecloud.com\nContent-Type: application/xml\n" +
        $"Content-Length: {xml.Length}\nAccept: application/xml\nX-Auth-Token: {Token}\n" + (xml.Length > 0 ? $"\n{xml}\n" : "") + "\n";

    private static (string, int, string, string) Describe(RackspaceResult result) =>
        (result.Callback, result.EventType, result.EventName, Encoding.UTF8.GetString(result.Html));

    private static async Task<IReadOnlyList<RackspaceResult>> PollUntilAsync(IServicesDriver services, int count) =>
        (await PollAsync(services, p => p.Results.Count >= count)).Results;

    private static async Task<RackspacePoll> PollAsync(IServicesDriver services, Func<RackspacePoll, bool> done)
    {
        var results = new List<RackspaceResult>();
        var failures = new List<RackspaceFailure>();
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var poll = await services.PollRackspaceAsync(Cancel);
            results.AddRange(poll.Results);
            failures.AddRange(poll.Failures);
            var all = new RackspacePoll(results, failures);
            if (done(all))
                return all;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Rackspace reported {results.Count} results and {failures.Count} failures.");
            await Task.Delay(5, Cancel);
        }
    }
}
