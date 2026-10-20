using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RakDotNet.Plugins.Tests.HttpConnection;

/// <summary>
/// An in-process HTTP server for the HTTP clients' tests: it records every request's bytes and answers each with the
/// next scripted reply, written in the given pieces, then closes the connection or keeps it for the next request.
/// </summary>
internal sealed class HttpTestServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<ScriptedReply> _replies = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    public HttpTestServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every request received, in order, as the raw bytes decoded as Latin-1.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public int ConnectionsAccepted => _connectionsAccepted;

    private int _connectionsAccepted;

    /// <summary>Queues the reply to the next request: its pieces with a pause between each, then a close unless kept open.</summary>
    public void Reply(bool close, params string[] pieces) => _replies.Enqueue(new ScriptedReply(pieces, close));

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }
        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stopping.Token);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _connectionsAccepted);
            _ = Task.Run(() => ServeAsync(socket));
        }
    }

    private async Task ServeAsync(Socket socket)
    {
        using var _ = socket;
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                var request = await ReadRequestAsync(socket, buffer, chunk);
                if (request is null)
                    return;
                Requests.Enqueue(request);
                if (!_replies.TryDequeue(out var reply))
                    return;
                foreach (var piece in reply.Pieces)
                {
                    await socket.SendAsync(Encoding.Latin1.GetBytes(piece), _stopping.Token);
                    await Task.Delay(50, _stopping.Token);
                }
                if (reply.Close)
                {
                    socket.Shutdown(SocketShutdown.Both);
                    return;
                }
            }
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    // A request is its headers and as many body bytes as its Content-Length says.
    private async Task<string?> ReadRequestAsync(Socket socket, List<byte> buffer, byte[] chunk)
    {
        while (true)
        {
            var text = Encoding.Latin1.GetString([.. buffer]);
            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0)
            {
                var length = headerEnd + 4 + ContentLength(text[..headerEnd]);
                if (buffer.Count >= length)
                {
                    buffer.RemoveRange(0, length);
                    return text[..length];
                }
            }
            var read = await socket.ReceiveAsync(chunk, _stopping.Token);
            if (read == 0)
                return null;
            buffer.AddRange(chunk.AsSpan(0, read));
        }
    }

    private static int ContentLength(string headers)
    {
        const string name = "\r\nContent-Length: ";
        var at = headers.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return 0;
        var end = headers.IndexOf("\r\n", at + name.Length, StringComparison.Ordinal);
        return int.Parse(end < 0 ? headers[(at + name.Length)..] : headers[(at + name.Length)..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record ScriptedReply(string[] Pieces, bool Close);
}
