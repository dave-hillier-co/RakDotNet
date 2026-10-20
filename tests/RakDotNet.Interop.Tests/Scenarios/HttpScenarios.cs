using System.Net;
using System.Text;
using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// HTTPConnection and HTTPConnection2 on the client peer, talking to the server peer's TCPInterface acting as a web
/// server: HTTPConnection as Samples/PHPDirectoryServer2 uses it, HTTPConnection2 as Samples/ComprehensivePCGame uses it
/// with RakString::FormatFor* requests. The server checks every request byte for byte, so a C# client must send what
/// a native one sends.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class HttpScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task HttpConnection_sends_HTTP_1_0_requests_and_reads_each_response_once_the_server_closes(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "web server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(cancellationToken: Token);
        await clientPeer.Tcp.StartHttpConnectionAsync("127.0.0.1", serverPeer.Port, Token);

        await clientPeer.Tcp.HttpConnectionGetAsync("/DirectoryServer.php?query=upload", Token);
        // HTTPConnection.cpp:173-178.
        var get = $"GET /DirectoryServer.php?query=upload HTTP/1.0\r\nHost: 127.0.0.1:{serverPeer.Port}\r\n\r\n";
        var first = await ReceiveRequestAsync(serverPeer, get);
        // The text is URL-decoded as it arrives, and Read returns it from its first '%' (HTTPConnection.cpp:206-239).
        await serverPeer.Tcp.TcpSendAsync(first, Encoding.ASCII.GetBytes("HTTP/1.0 200 OK\r\nContent-Type: text/plain\r\n\r\nGAME%25LIST%201"), cancellationToken: Token);
        await serverPeer.CloseWhenSentAsync(first, Timeout, Token);
        var status = await WaitForHttpAsync(clientPeer, s => s.Results.Count > 0, "a result");
        Assert.Equal(["%LIST 1"], status.Results.Select(r => Encoding.ASCII.GetString(r)));
        Assert.Equal(serverPeer.EndPoint, status.ServerAddress);

        await clientPeer.Tcp.HttpConnectionPostAsync("/DirectoryServer.php", "__GAME_PORT=60000&__GAME_NAME=test", cancellationToken: Token);
        // HTTPConnection.cpp:156-167; each request has a connection of its own.
        var post = $"POST /DirectoryServer.php HTTP/1.0\r\nHost: 127.0.0.1:{serverPeer.Port}\r\nContent-Type: application/x-www-form-urlencoded\r\nContent-Length: 34\r\n\r\n__GAME_PORT=60000&__GAME_NAME=test";
        var second = await ReceiveRequestAsync(serverPeer, post);
        Assert.NotEqual(first, second);
        // A status above 299 is a bad response with the first packet (HTTPConnection.cpp:224-235).
        await serverPeer.Tcp.TcpSendAsync(second, Encoding.ASCII.GetBytes("HTTP/1.0 403 Forbidden\r\n\r\n"), cancellationToken: Token);
        await serverPeer.CloseWhenSentAsync(second, Timeout, Token);
        var bad = Assert.Single((await WaitForHttpAsync(clientPeer, s => s.BadResponses.Count > 0, "a bad response")).BadResponses);
        Assert.Equal(403, bad.Code);
        Assert.Equal("HTTP/1.0 403 Forbidden\r\n\r\n", Encoding.ASCII.GetString(bad.Data));

        var idle = await WaitForHttpAsync(clientPeer, s => !s.Busy, "the connection to go idle");
        Assert.Empty(idle.Results);
    }

    [Theory]
    [InlineData(PeerKind.Native)]
    [InlineData(PeerKind.RakDotNet)]
    public async Task A_request_over_511_bytes_is_cut_by_POSIX_RakNet_and_formatted_whole_by_RakDotNet(PeerKind kind)
    {
        // RakString::Assign checks the length of the format, not of its output, so on POSIX the output is cut at 511
        // bytes (RakString.cpp:1376-1386) and Content-Length no longer matches the body; on Windows it is whole.
        // RakDotNet formats the request whole on every platform.
        await using var peer = await TcpPeer.LaunchAsync(kind, "client", Token);
        var body = new string('b', 600);

        var request = await peer.Tcp.FormatHttpRequestAsync(new HttpRequestSpec("POST", "http://example.com/p", "text/plain", body), Token);

        var whole = $"POST /p HTTP/1.1\r\nHost: example.com\r\nContent-Type: text/plain\r\nContent-Length: 600\r\n\r\n{body}";
        var expected = kind == PeerKind.Native && !OperatingSystem.IsWindows() ? whole[..511] : whole;
        Assert.Equal(expected, Encoding.ASCII.GetString(request));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task HttpConnection2_keeps_the_connection_for_the_next_request_and_joins_a_chunked_response(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "web server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(maxConnections: 4, cancellationToken: Token);
        await clientPeer.Tcp.AttachHttpConnection2Async(Token);

        // ComprehensivePCGame.cpp:262-267: a GET formatted by RakString::FormatForGET.
        var get = await clientPeer.Tcp.FormatHttpRequestAsync(new HttpRequestSpec("GET", $"127.0.0.1:{serverPeer.Port}/testServer?__gameId=comprehensivePCGame"), Token);
        Assert.Equal($"GET /testServer?__gameId=comprehensivePCGame HTTP/1.1\r\nHost: 127.0.0.1:{serverPeer.Port}\r\n\r\n", Encoding.ASCII.GetString(get));
        Assert.True(await clientPeer.Tcp.TransmitHttpRequestAsync(get, "127.0.0.1", serverPeer.Port, cancellationToken: Token));
        var connection = await ReceiveRequestAsync(serverPeer, Encoding.ASCII.GetString(get));
        await serverPeer.Tcp.TcpSendAsync(connection, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n{\"GET\":[]}"), cancellationToken: Token);

        // The response is complete when its body reaches the Content-Length (HTTPConnection2.cpp:325-352).
        var response = Assert.Single((await WaitForHttp2Async(clientPeer, s => s.Responses.Count > 0, "the first response")).Responses);
        var text = Encoding.ASCII.GetString(response.Response);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\n{\"GET\":[]}", text);
        Assert.Equal(text.IndexOf('{', StringComparison.Ordinal), response.ContentOffset);
        Assert.Equal(get, response.StringTransmitted);
        Assert.Equal("127.0.0.1", response.HostTransmitted);
        Assert.Equal(serverPeer.EndPoint, response.HostReceived);

        // ComprehensivePCGame.cpp:1455-1465: a POST, on the connection already open.
        var post = await clientPeer.Tcp.FormatHttpRequestAsync(new HttpRequestSpec("POST", $"127.0.0.1:{serverPeer.Port}/testServer", "application/json", "{\"__gameId\":\"comprehensivePCGame\"}"), Token);
        Assert.Equal(
            $"POST /testServer HTTP/1.1\r\nHost: 127.0.0.1:{serverPeer.Port}\r\nContent-Type: application/json\r\nContent-Length: 34\r\n\r\n{{\"__gameId\":\"comprehensivePCGame\"}}",
            Encoding.ASCII.GetString(post));
        Assert.True(await clientPeer.Tcp.TransmitHttpRequestAsync(post, "127.0.0.1", serverPeer.Port, cancellationToken: Token));
        Assert.Equal(connection, await ReceiveRequestAsync(serverPeer, Encoding.ASCII.GetString(post)));

        // A chunked response in three writes holds only the body, at offset 0 (HTTPConnection2.cpp:226-303).
        foreach (var piece in new[] { "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\na\r\n{\"POST\"", ":1}\r\n", "0\r\n\r\n" })
        {
            await serverPeer.Tcp.TcpSendAsync(connection, Encoding.ASCII.GetBytes(piece), cancellationToken: Token);
            await Task.Delay(100, Token);
        }
        var chunked = Assert.Single((await WaitForHttp2Async(clientPeer, s => s.Responses.Count > 0, "the chunked response")).Responses);
        Assert.Equal("{\"POST\":1}", Encoding.ASCII.GetString(chunked.Response));
        Assert.Equal(0, chunked.ContentOffset);
        Assert.False((await clientPeer.Tcp.PollHttpConnection2Async(Token)).Busy);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task HttpConnection2_completes_a_request_whose_connection_closes_and_drops_one_that_cannot_connect(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "web server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(maxConnections: 4, cancellationToken: Token);
        await clientPeer.Tcp.AttachHttpConnection2Async(Token);

        var delete = await clientPeer.Tcp.FormatHttpRequestAsync(new HttpRequestSpec("DELETE", $"127.0.0.1:{serverPeer.Port}/testServer", ExtraHeaders: "X-Token: 1"), Token);
        Assert.Equal($"DELETE /testServer HTTP/1.1\r\nX-Token: 1\r\nContent-Length: 0\r\nHost: 127.0.0.1:{serverPeer.Port}\r\nConnection: close\r\n\r\n", Encoding.ASCII.GetString(delete));
        await clientPeer.Tcp.TransmitHttpRequestAsync(delete, "127.0.0.1", serverPeer.Port, cancellationToken: Token);
        var connection = await ReceiveRequestAsync(serverPeer, Encoding.ASCII.GetString(delete));
        await serverPeer.Tcp.TcpSendAsync(connection, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 50\r\n\r\npart"), cancellationToken: Token);
        await serverPeer.CloseWhenSentAsync(connection, Timeout, Token);

        // HTTPConnection2.cpp:547-601: the lost connection completes the request with what arrived.
        var response = Assert.Single((await WaitForHttp2Async(clientPeer, s => s.Responses.Count > 0, "the cut-off response")).Responses);
        var text = Encoding.ASCII.GetString(response.Response);
        Assert.EndsWith("part", text, StringComparison.Ordinal);
        Assert.Equal(text.Length - 4, response.ContentOffset);

        // HTTPConnection2.cpp:537-546: a failed connection attempt drops the request.
        await serverPeer.Tcp.StopTcpAsync(Token);
        var unreachable = await clientPeer.Tcp.FormatHttpRequestAsync(new HttpRequestSpec("GET", $"127.0.0.1:{serverPeer.Port}/"), Token);
        Assert.True(await clientPeer.Tcp.TransmitHttpRequestAsync(unreachable, "127.0.0.1", serverPeer.Port, cancellationToken: Token));
        await clientPeer.WaitForKindAsync("failedConnectionAttempt", Timeout, Token);
        var idle = await WaitForHttp2Async(clientPeer, s => !s.Busy, "the request to be dropped");
        Assert.Empty(idle.Responses);
    }

    // Collects request bytes from the next connection with data until the expected request has arrived.
    private static async Task<IPEndPoint> ReceiveRequestAsync(TcpPeer server, string expected)
    {
        var first = await server.WaitForAsync(e => e.Kind == "packet", "a request", Timeout, Token);
        var from = first.Address!;
        var rest = first.Data.Length >= expected.Length
            ? []
            : await server.ReceiveBytesAsync(from, r => first.Data.Length + r.Length >= expected.Length, "the rest of the request", Timeout, Token);
        Assert.Equal(expected, Encoding.ASCII.GetString([.. first.Data, .. rest]));
        return from;
    }

    private static async Task<HttpConnectionStatus> WaitForHttpAsync(TcpPeer client, Func<HttpConnectionStatus, bool> done, string expectation)
    {
        var results = new List<byte[]>();
        var bad = new List<HttpBadResponse>();
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var status = await client.Tcp.PollHttpConnectionAsync(Token);
            results.AddRange(status.Results);
            bad.AddRange(status.BadResponses);
            var collected = status with { Results = [.. results], BadResponses = [.. bad] };
            if (done(collected))
                return collected;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {expectation}: {collected}");
            await Task.Delay(10, Token);
        }
    }

    private static async Task<HttpConnection2Status> WaitForHttp2Async(TcpPeer client, Func<HttpConnection2Status, bool> done, string expectation)
    {
        var responses = new List<HttpConnection2Response>();
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var status = await client.Tcp.PollHttpConnection2Async(Token);
            responses.AddRange(status.Responses);
            var collected = status with { Responses = [.. responses] };
            if (done(collected))
                return collected;
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {expectation}: {collected}");
            await Task.Delay(10, Token);
        }
    }
}
