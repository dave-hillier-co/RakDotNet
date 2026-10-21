using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Plugins.Tests.EmailSender;

namespace RakDotNet.Plugins.Tests.DynDns;

/// <summary>
/// DynDNS against in-process check-IP and update servers that record every byte, driven by calling Update as an
/// application's loop does. Expected bytes are derived from DynDNS.cpp.
/// </summary>
public sealed class DynDnsTests : IDisposable
{
    // DynDNS.cpp:94-106 for hostname=game.example.com, myip=1.2.3.4 and "user:pass".
    private const string UpdateRequest =
        "GET /nic/update?hostname=game.example.com&myip=1.2.3.4&wildcard=NOCHG&mx=NOCHG&backmx=NOCHG HTTP/1.0\n" +
        "Host: members.dyndns.org\n" +
        "Authorization: Basic dXNlcjpwYXNz\r\n" +
        "User-Agent: Jenkins Software LLC - PC - 1.0\n\n";

    private const string CheckIpReply =
        "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nServer: DynDNS-CheckIP/1.0\r\nConnection: close\r\n\r\nCurrent IP Address: 98.189.219.22";

    private readonly ScriptedServer _checkIp = new();
    private readonly ScriptedServer _update = new();
    private readonly ConcurrentQueue<string> _resolved = new();

    public void Dispose()
    {
        _checkIp.Dispose();
        _update.Dispose();
    }

    [Fact]
    public void Asks_for_the_public_address_then_sends_the_update()
    {
        _checkIp.Expect(async c =>
        {
            Assert.Equal("GET\n\n", await c.ReadUntilAsync("\n\n"));
            await c.SendAsync(CheckIpReply);
        });
        _update.Expect(async c =>
        {
            Assert.Equal(UpdateRequest, await c.ReadUntilAsync("\n\n"));
            await c.SendAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\ngood 98.189.219.22");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", "1.2.3.4", "user:pass");
        Assert.True(dynDns.IsRunning);
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.Success, dynDns.CompletedResultCode);
        Assert.Equal("DNS update success.\nPlease wait up to 60 seconds for the change to take effect.\n", dynDns.CompletedDescription);
        Assert.True(dynDns.WasResultSuccessful);
        Assert.Equal("98.189.219.22", dynDns.MyPublicIp);
        Assert.Equal(["game.example.com"], _resolved);
    }

    [Fact]
    public void Without_an_address_the_request_leaves_out_myip()
    {
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(CheckIpReply);
        });
        _update.Expect(async c =>
        {
            Assert.StartsWith("GET /nic/update?hostname=game.example.com&wildcard=NOCHG", await c.ReadUntilAsync("\n\n"), StringComparison.Ordinal);
            await c.SendAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nnochg 98.189.219.22");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.NoChange, dynDns.CompletedResultCode);
        Assert.Equal("No change", dynDns.CompletedDescription);
        Assert.True(dynDns.WasResultSuccessful);
    }

    [Fact]
    public void A_host_that_already_has_the_address_is_not_updated()
    {
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nCurrent IP Address: 127.0.0.1");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("localhost", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.DnsAlreadySet, dynDns.CompletedResultCode);
        Assert.Equal("No action needed", dynDns.CompletedDescription);
        Assert.Equal("127.0.0.1", dynDns.MyPublicIp);
        Assert.Empty(_update.Conversations);
    }

    [Fact]
    public void A_reported_address_followed_by_text_is_looked_up_as_a_host_name()
    {
        // SystemAddress::FromString reads the rest of the packet; with letters in it, that is a host name to resolve,
        // which fails, giving UNASSIGNED_SYSTEM_ADDRESS and its text (RakNetTypes.cpp:262-268, 477-521).
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\n\r\n<html><body>Current IP Address: 98.189.219.22</body></html>");
        });
        _update.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n!yours");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.NotYours, dynDns.CompletedResultCode);
        Assert.Equal("UNASSIGNED_SYSTEM_ADDRESS", dynDns.MyPublicIp);
        Assert.Equal(["98.189.219.22</body></html>", "game.example.com"], _resolved);
    }

    [Theory]
    [InlineData("good", DynDnsResultCode.Success, "DNS update success.\nPlease wait up to 60 seconds for the change to take effect.\n")]
    [InlineData("nochg", DynDnsResultCode.NoChange, "No change")]
    [InlineData("abuse", DynDnsResultCode.Abuse, "Host has been blocked. You will need to contact DynDNS to reenable.")]
    [InlineData("badagent", DynDnsResultCode.BadAgent, "Useragent is blocked")]
    [InlineData("badauth", DynDnsResultCode.BadAuth, "Username/password pair bad")]
    [InlineData("badsys", DynDnsResultCode.BadSys, "Bad system parameter")]
    [InlineData("dnserr", DynDnsResultCode.DnsError, "DNS inconsistency")]
    [InlineData("!donator", DynDnsResultCode.NotDonator, "Paid account feature")]
    [InlineData("nohost", DynDnsResultCode.NoHost, "No such host in system")]
    [InlineData("notfqdn", DynDnsResultCode.NotFqdn, "Invalid hostname format")]
    [InlineData("numhost", DynDnsResultCode.NumHost, "Serious error")]
    [InlineData("!yours", DynDnsResultCode.NotYours, "This host exists, but does not belong to you")]
    [InlineData("911", DynDnsResultCode.Error911, "911")]
    [InlineData("whatever", DynDnsResultCode.UnknownResult, "DynDNS returned unknown result")]
    public void The_result_code_follows_the_connection_close_header(string code, DynDnsResultCode expected, string description)
    {
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\n\r\nno address here");
        });
        _update.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync($"HTTP/1.1 200 OK\r\nConnection: close\r\n \r\n{code}");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(expected, dynDns.CompletedResultCode);
        Assert.Equal(description, dynDns.CompletedDescription);
        // "good" with no digits after it leaves the address as it was: not reported.
        Assert.Equal("", dynDns.MyPublicIp);
    }

    [Fact]
    public void A_reply_without_a_connection_close_header_cannot_be_parsed()
    {
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(CheckIpReply);
        });
        _update.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\n\r\ngood 1.2.3.4");
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.ParsingFailure, dynDns.CompletedResultCode);
        Assert.Equal("Parsing failure on returned string from DynDNS", dynDns.CompletedDescription);
        Assert.False(dynDns.WasResultSuccessful);
    }

    [Fact]
    public void A_check_ip_server_that_does_not_answer_within_a_second_is_skipped()
    {
        var time = new FakeTimeProvider();
        _checkIp.Expect(c => c.ReadUntilAsync("\n\n"));
        _update.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\ngood 5.6.7.8");
        });
        using var dynDns = Create(time);

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        Poll.Until(() => { dynDns.Update(); return _checkIp.Conversations.TryPeek(out var c) && c.Received.Length == 5; }, "the check-IP request");
        dynDns.Update();
        Assert.Empty(_update.Conversations);
        time.Advance(TimeSpan.FromMilliseconds(1001));
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.Success, dynDns.CompletedResultCode);
        Assert.Equal("5.6.7.8", dynDns.MyPublicIp);
    }

    [Fact]
    public void An_update_server_that_does_not_answer_within_a_second_times_out()
    {
        var time = new FakeTimeProvider();
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(CheckIpReply);
        });
        _update.Expect(c => c.ReadUntilAsync("\n\n"));
        using var dynDns = Create(time);

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        Poll.Until(() => { dynDns.Update(); return _update.Conversations.TryPeek(out var c) && c.Received.EndsWith("\n\n", StringComparison.Ordinal); }, "the update request");
        time.Advance(TimeSpan.FromMilliseconds(1001));
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.DynDnsTimeout, dynDns.CompletedResultCode);
        Assert.Equal("DynDNS did not respond", dynDns.CompletedDescription);
    }

    [Fact]
    public void An_update_server_that_closes_without_answering_loses_the_connection()
    {
        _checkIp.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            await c.SendAsync(CheckIpReply);
        });
        _update.Expect(async c =>
        {
            await c.ReadUntilAsync("\n\n");
            c.Close();
        });
        using var dynDns = Create();

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.ConnectionLostWithoutResponse, dynDns.CompletedResultCode);
        Assert.Equal("Connection lost to DynDNS during GET operation", dynDns.CompletedDescription);
    }

    [Fact]
    public void An_unreachable_server_did_not_connect()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        using var dynDns = Create();
        dynDns.CheckIpServer = new DnsEndPoint("127.0.0.1", port);

        dynDns.UpdateHostIp("game.example.com", null, "user:pass");
        RunToCompletion(dynDns);

        Assert.Equal(DynDnsResultCode.TcpDidNotConnect, dynDns.CompletedResultCode);
        Assert.Equal("Could not connect to DynDNS", dynDns.CompletedDescription);
    }

    [Fact]
    public void Update_does_nothing_when_idle()
    {
        using var dynDns = Create();

        dynDns.Update();

        Assert.True(dynDns.IsCompleted);
        Assert.False(dynDns.IsRunning);
    }

    private Plugins.DynDns Create(TimeProvider? time = null) =>
        new(time)
        {
            CheckIpServer = new DnsEndPoint("127.0.0.1", _checkIp.Port),
            UpdateServer = new DnsEndPoint("127.0.0.1", _update.Port),
            ResolveHost = host =>
            {
                _resolved.Enqueue(host);
                return host == "localhost" ? IPAddress.Loopback : null;
            },
        };

    private static void RunToCompletion(Plugins.DynDns dynDns) =>
        Poll.Until(() => { dynDns.Update(); return dynDns.IsCompleted; }, "the update to complete");
}

file static class Poll
{
    public static void Until(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(2);
        }
    }
}
