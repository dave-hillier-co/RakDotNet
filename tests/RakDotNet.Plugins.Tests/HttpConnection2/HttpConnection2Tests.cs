using System.Net;
using RakDotNet.Plugins.Tests.HttpConnection;
using RakDotNet.Plugins.Tests.TcpInterface;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.HttpConnection2;

/// <summary>
/// HTTPConnection2 attached to a TCPInterface, as Samples/ComprehensivePCGame uses it with the master server: requests
/// formatted with RakString::FormatFor*, the TCP events polled each frame, and responses read with GetResponse.
/// </summary>
public sealed class HttpConnection2Tests : IDisposable
{
    private readonly HttpTestServer _server = new();
    private readonly Plugins.TcpInterface _tcp = new();
    private readonly Plugins.HttpConnection2 _http = new();

    public HttpConnection2Tests()
    {
        _tcp.AttachPlugin(_http);
        _tcp.Start(0, 0, 4);
    }

    private SystemAddress ServerAddress => new(IPAddress.Loopback, _server.Port);

    private string Uri(string path) => $"127.0.0.1:{_server.Port}{path}";

    public void Dispose()
    {
        _tcp.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void A_response_with_a_content_length_is_complete_once_the_body_has_arrived()
    {
        _server.Reply(close: false, "HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nhello", " world");
        var request = HttpFormat.FormatForGet(Uri("/game"));

        Assert.True(_http.TransmitRequest(request, "127.0.0.1", _server.Port, userData: "mine"));
        Assert.True(_http.IsBusy);
        var response = PumpUntilResponse();

        Assert.Equal(request, Assert.Single(_server.Requests));
        Assert.Equal(request, response.StringTransmitted);
        Assert.Equal("127.0.0.1", response.HostTransmitted);
        Assert.Equal(ServerAddress, response.HostReceived);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nhello world", response.ResponseText);
        Assert.Equal(response.ResponseText.IndexOf("hello", StringComparison.Ordinal), response.ContentOffset);
        Assert.Equal("mine", response.UserData);
        Assert.False(_http.IsBusy);
        Assert.False(_http.HasResponse);
    }

    [Fact]
    public void A_chunked_response_is_joined_without_its_chunk_sizes()
    {
        _server.Reply(close: false,
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhel",
            "lo\r\n6\r\n world\r\n",
            "0\r\n\r\n");

        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/chunked")), "127.0.0.1", _server.Port);
        var response = PumpUntilResponse();

        // HTTPConnection2.cpp:226-303: the response holds only the body, at offset 0.
        Assert.Equal("hello world", response.ResponseText);
        Assert.Equal(0, response.ContentOffset);
    }

    [Fact]
    public void A_response_without_a_length_is_complete_at_the_end_of_its_headers()
    {
        // HTTPConnection2.cpp:372-395.
        _server.Reply(close: false, "HTTP/1.1 204 No Content\r\nServer: test\r\n\r\n");

        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/empty")), "127.0.0.1", _server.Port);
        var response = PumpUntilResponse();

        Assert.Equal("HTTP/1.1 204 No Content\r\nServer: test\r\n\r\n", response.ResponseText);
        Assert.Equal(-1, response.ContentOffset);
    }

    [Fact]
    public void A_zero_content_length_completes_with_no_body()
    {
        _server.Reply(close: false, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");

        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/zero")), "127.0.0.1", _server.Port);

        Assert.Equal(-1, PumpUntilResponse().ContentOffset);
    }

    [Fact]
    public void A_content_length_read_as_negative_completes_with_no_body()
    {
        // ReadIntFromSubstring reads 3000000000 as a negative int, which HTTPConnection2.cpp:326-374 completes at once with
        // no body, as it does a length of 0.
        _server.Reply(close: false, "HTTP/1.1 200 OK\r\nContent-Length: 3000000000\r\n\r\nbody");

        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/negative")), "127.0.0.1", _server.Port);

        Assert.Equal(-1, PumpUntilResponse().ContentOffset);
    }

    [Fact]
    public void A_connection_closed_before_the_body_is_complete_completes_the_request()
    {
        // HTTPConnection2.cpp:547-601.
        _server.Reply(close: true, "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\npartial");

        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/short")), "127.0.0.1", _server.Port);
        var response = PumpUntilResponse();

        Assert.EndsWith("partial", response.ResponseText, StringComparison.Ordinal);
        Assert.Equal(response.ResponseText.IndexOf("partial", StringComparison.Ordinal), response.ContentOffset);
    }

    [Fact]
    public void A_second_request_to_a_connected_server_waits_for_the_first_and_reuses_the_connection()
    {
        _server.Reply(close: false, "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nfirst");
        _server.Reply(close: false, "HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\nsecond");
        _http.TransmitRequest(HttpFormat.FormatForGet(Uri("/1")), "127.0.0.1", _server.Port);
        Poll.True(() => { PumpTcp(); return _server.Requests.Count == 1; }, "the first request");

        // Connected: the second goes on the same connection, held until the first completes if that is still outstanding
        // (HTTPConnection2.cpp:59-79, 251-253).
        Assert.True(_http.TransmitRequest(HttpFormat.FormatForPost(Uri("/2"), "text/plain", "body"), "127.0.0.1", _server.Port));

        Assert.EndsWith("first", PumpUntilResponse().ResponseText, StringComparison.Ordinal);
        Assert.EndsWith("second", PumpUntilResponse().ResponseText, StringComparison.Ordinal);
        Assert.Equal(1, _server.ConnectionsAccepted);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public void A_server_that_cannot_be_reached_drops_its_request()
    {
        var port = TcpInterfaceTests.ClosedPort();

        Assert.True(_http.TransmitRequest(HttpFormat.FormatForGet($"127.0.0.1:{port}/"), "127.0.0.1", port));

        Poll.True(() => { PumpTcp(); return !_http.IsBusy; }, "the request to be dropped");
        Assert.False(_http.HasResponse);
    }

    [Fact]
    public void A_host_that_does_not_resolve_or_a_given_address_not_connected_is_refused()
    {
        Assert.False(_http.TransmitRequest("GET / HTTP/1.1\r\n\r\n", "no such host.invalid", 80));
        Assert.False(_http.TransmitRequest("GET / HTTP/1.1\r\n\r\n", "127.0.0.1", _server.Port, useAddress: ServerAddress));
        Assert.False(_http.IsBusy);
    }

    [Fact]
    public void The_responses_are_handed_out_as_RakNet_removes_them_from_its_list()
    {
        // GetResponse takes the first completed request and moves the last into its place (HTTPConnection2.cpp:112-113).
        using var tcp = new Plugins.TcpInterface();
        var http = new Plugins.HttpConnection2();
        tcp.AttachPlugin(http);
        tcp.Start(0, 0, 4);
        using var a = new HttpTestServer();
        using var b = new HttpTestServer();
        using var c = new HttpTestServer();
        foreach (var (server, body) in new[] { (a, "a"), (b, "b"), (c, "c") })
        {
            server.Reply(close: false, $"HTTP/1.1 200 OK\r\nContent-Length: 1\r\n\r\n{body}");
            http.TransmitRequest(HttpFormat.FormatForGet($"127.0.0.1:{server.Port}/"), "127.0.0.1", server.Port);
            Poll.True(() => { Pump(tcp); return http.HasResponse && !http.IsBusy; }, $"the response from {body}");
        }

        var order = new List<string>();
        while (http.TryGetResponse(out var response))
            order.Add(response.ResponseText[^1..]);

        Assert.Equal(["a", "c", "b"], order);
    }

    private void PumpTcp() => Pump(_tcp);

    // ComprehensivePCGame.cpp:1201-1206: completed attempts, then Receive, then failed and lost.
    private static void Pump(Plugins.TcpInterface tcp)
    {
        tcp.HasCompletedConnectionAttempt();
        while (tcp.Receive() is not null)
        {
        }
        tcp.HasFailedConnectionAttempt();
        tcp.HasLostConnection();
    }

    private Plugins.HttpResponse PumpUntilResponse() => Poll.Until(() =>
    {
        PumpTcp();
        return _http.TryGetResponse(out var response) ? response : null;
    }, "an HTTP response");
}
