using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// DynDNS on the client peer, its update driven by calling Update as an application's loop does, against the server
/// peer's TCPInterface playing both checkip.dyndns.org and members.dyndns.org, which checks every request byte for
/// byte. RakNet has no DynDNS sample; the scenarios follow the flow of DynDNS.cpp. The hosts updated are numeric or
/// localhost, so resolving them needs no network.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class DynDnsScenarios
{
    // DynDNS.cpp:94-106.
    private const string UpdateRequest =
        "GET /nic/update?hostname=203.0.113.7&myip=198.51.100.20&wildcard=NOCHG&mx=NOCHG&backmx=NOCHG HTTP/1.0\n" +
        "Host: members.dyndns.org\n" +
        "Authorization: Basic dXNlcjpwYXNz\r\n" +
        "User-Agent: Jenkins Software LLC - PC - 1.0\n\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_public_address_is_checked_then_the_update_is_sent_and_its_result_read(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "DynDNS servers", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        var services = (IServicesDriver)clientDriver;

        await services.StartDynDnsAsync(new DynDnsRequest("203.0.113.7", "198.51.100.20", "user:pass", Redirects(serverPeer)), Token);
        var updating = RunToCompletionAsync(services);

        var checkIp = await ServiceScript.AcceptAsync(serverPeer, Token);
        // "Needs 2 newlines!" (DynDNS.cpp:126).
        await ServiceScript.ExchangeAsync(serverPeer, checkIp, "GET\n\n",
            "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nConnection: close\r\n\r\nCurrent IP Address: 198.51.100.20", Token);
        var update = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.ExchangeAsync(serverPeer, update, UpdateRequest,
            "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\ngood 198.51.100.20", Token);

        var status = await updating;
        Assert.Equal(0, status.ResultCode);
        Assert.Equal("DNS update success.\nPlease wait up to 60 seconds for the change to take effect.\n", status.Description);
        Assert.True(status.Successful);
        Assert.Equal("198.51.100.20", status.MyPublicIp);
        // The check-IP connection was closed by DynDNS once it had the address (DynDNS.cpp:249-252).
        await serverPeer.WaitForAsync(e => e.Kind == "lostConnection" && Equals(e.Address, checkIp), "the check-IP connection to close", InteropSession.DefaultTimeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_host_that_already_has_the_public_address_is_left_alone(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "DynDNS servers", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        var services = (IServicesDriver)clientDriver;

        await services.StartDynDnsAsync(new DynDnsRequest("localhost", null, "user:pass", Redirects(serverPeer)), Token);
        var updating = RunToCompletionAsync(services);
        var checkIp = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.ExchangeAsync(serverPeer, checkIp, "GET\n\n", "HTTP/1.1 200 OK\r\n\r\nCurrent IP Address: 127.0.0.1", Token);

        var status = await updating;
        // RC_DNS_ALREADY_SET (DynDNS.cpp:236-246).
        Assert.Equal(1, status.ResultCode);
        Assert.Equal("No action needed", status.Description);
        Assert.True(status.Successful);
        Assert.Equal("127.0.0.1", status.MyPublicIp);
        var others = await serverPeer.SettleAsync(TimeSpan.FromMilliseconds(300), Token);
        Assert.DoesNotContain(others, e => e.Kind == "newIncomingConnection");
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_silent_check_ip_server_is_skipped_and_an_update_server_that_closes_loses_the_connection(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "DynDNS servers", Token);
        await using var clientDriver = await PeerMatrix.LaunchAsync(client, Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        var services = (IServicesDriver)clientDriver;

        await services.StartDynDnsAsync(new DynDnsRequest("203.0.113.7", "198.51.100.20", "user:pass", Redirects(serverPeer)), Token);
        var updating = RunToCompletionAsync(services);
        var checkIp = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.ExpectAsync(serverPeer, checkIp, "GET\n\n", Token);
        // No reply: a second later DynDNS closes the connection and goes on (DynDNS.cpp:133-138).
        var update = await ServiceScript.AcceptAsync(serverPeer, Token);
        await ServiceScript.ExpectAsync(serverPeer, update, UpdateRequest, Token);
        await serverPeer.Tcp.TcpCloseAsync(update, Token);

        var status = await updating;
        // RC_CONNECTION_LOST_WITHOUT_RESPONSE (DynDNS.cpp:257-263).
        Assert.Equal(12, status.ResultCode);
        Assert.Equal("Connection lost to DynDNS during GET operation", status.Description);
        Assert.False(status.Successful);
        Assert.Equal("", status.MyPublicIp);
    }

    private static ServiceRedirect[] Redirects(TcpPeer server) =>
        [new("checkip.dyndns.org", server.EndPoint), new("members.dyndns.org", server.EndPoint)];

    private static async Task<DynDnsStatus> RunToCompletionAsync(IServicesDriver services)
    {
        var deadline = DateTime.UtcNow + InteropSession.DefaultTimeout;
        while (true)
        {
            var status = await services.UpdateDynDnsAsync(Token).ConfigureAwait(false);
            if (status.Completed)
                return status;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"DynDNS did not complete; last status {status}.");
            await Task.Delay(5, Token).ConfigureAwait(false);
        }
    }
}
