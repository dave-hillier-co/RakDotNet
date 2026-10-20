using System.Net;
using RakDotNet.Plugins.Tests.TcpInterface;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.HttpConnection;

/// <summary>
/// HTTPConnection as Samples/PHPDirectoryServer2 drives it: Update each frame, every TCP packet passed to
/// ProcessTCPPacket, and a result once the server closes the connection.
/// </summary>
public sealed class HttpConnectionTests : IDisposable
{
    private readonly HttpTestServer _server = new();
    private readonly Plugins.TcpInterface _tcp = new();
    private readonly Plugins.HttpConnection _http;

    public HttpConnectionTests()
    {
        _tcp.Start(0, 0);
        _http = new Plugins.HttpConnection(_tcp, "127.0.0.1", _server.Port);
    }

    public void Dispose()
    {
        _http.Dispose();
        _tcp.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void A_get_is_an_HTTP_1_0_request_and_the_whole_response_is_read_once_the_server_closes()
    {
        _server.Reply(close: true, "HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\n", "hello");
        Assert.False(_http.IsBusy);

        _http.Get("/index.html");
        var result = PumpUntilRead();

        // HTTPConnection.cpp:173-178.
        Assert.Equal($"GET /index.html HTTP/1.0\r\nHost: 127.0.0.1:{_server.Port}\r\n\r\n", Assert.Single(_server.Requests));
        Assert.Equal("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\nhello", result);
        Assert.Equal(new SystemAddress(IPAddress.Loopback, _server.Port), _http.ServerAddress);
        Poll.True(() => { Pump(); return !_http.IsBusy; }, "the connection to go idle");
    }

    [Fact]
    public void A_post_sends_the_content_type_and_length()
    {
        _server.Reply(close: true, "HTTP/1.0 200 OK\r\n\r\nok");

        _http.Post("/submit.php", "a=1&b=2");
        PumpUntilRead();

        // HTTPConnection.cpp:156-167.
        Assert.Equal(
            $"POST /submit.php HTTP/1.0\r\nHost: 127.0.0.1:{_server.Port}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: 7\r\n\r\na=1&b=2",
            Assert.Single(_server.Requests));
    }

    [Fact]
    public void The_response_is_url_decoded_and_read_from_the_first_percent_sign()
    {
        // HTTPConnection.cpp:206-211 and 237-239: the received text is URL-decoded, and Read returns it from the first
        // byte 1, 2, 3 or '%', or whole when there is none.
        _server.Reply(close: true, "HTTP/1.0 200 OK\r\n\r\nfield%3d1%25rest");

        _http.Get("/");

        Assert.Equal("%rest", PumpUntilRead());
    }

    [Fact]
    public void A_response_code_above_299_is_a_bad_response_and_gives_no_result()
    {
        _server.Reply(close: true, "HTTP/1.0 404 Not Found\r\n\r\nmissing");

        _http.Get("/missing");

        var (code, data) = Poll.Until(() => { Pump(); return _http.HasBadResponse(out var c, out var d) ? Tuple.Create(c, d) : null; }, "the bad response");
        Assert.Equal(404, code);
        Assert.StartsWith("HTTP/1.0 404 Not Found", data, StringComparison.Ordinal);
        Poll.True(() => { Pump(); return !_http.IsBusy; }, "the connection to go idle");
        Assert.False(_http.HasRead);
    }

    [Fact]
    public void Requests_are_sent_one_at_a_time_each_on_its_own_connection()
    {
        _server.Reply(close: true, "HTTP/1.0 200 OK\r\n\r\nfirst");
        _server.Reply(close: true, "HTTP/1.0 200 OK\r\n\r\nsecond");

        _http.Get("/1");
        _http.Get("/2");

        Assert.EndsWith("first", PumpUntilRead(), StringComparison.Ordinal);
        Assert.EndsWith("second", PumpUntilRead(), StringComparison.Ordinal);
        Assert.Equal(2, _server.ConnectionsAccepted);
        Assert.Equal(["GET /1", "GET /2"], _server.Requests.Select(r => r[..6]));
    }

    [Fact]
    public void A_server_that_cannot_be_reached_is_tried_again_while_the_request_is_queued()
    {
        using var http = new Plugins.HttpConnection(_tcp, "127.0.0.1", TcpInterfaceTests.ClosedPort());

        http.Get("/");

        // A failed attempt returns the connection to idle, but the request was never taken from the queue, so the next
        // Update connects again (HTTPConnection.cpp:90-96, 109-117, 152).
        Poll.True(() => { http.Update(); return !http.IsBusy; }, "the attempt to fail");
        http.Update();
        Assert.True(http.IsBusy);
        Assert.False(http.HasRead);
    }

    private void Pump()
    {
        _http.Update();
        while (_tcp.Receive() is { } packet)
            _http.ProcessTcpPacket(packet);
    }

    private string PumpUntilRead() => Poll.Until(() =>
    {
        Pump();
        return _http.HasRead ? _http.Read() : null;
    }, "an HTTP result");
}
