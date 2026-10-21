using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace RakDotNet.Plugins.Tests.EmailSender;

/// <summary>
/// An in-process TCP server for the external services' tests: each connection it accepts runs the next script, which
/// reads what the client sent and writes replies. It records every byte each connection sent, decoded as Latin-1.
/// </summary>
internal sealed class ScriptedServer : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentQueue<Func<Conversation, Task>> _scripts = new();
    private readonly ConcurrentQueue<Task> _running = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptLoop;

    public ScriptedServer()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public ushort Port => (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>What each connection sent, in the order the connections were accepted.</summary>
    public ConcurrentQueue<Conversation> Conversations { get; } = new();

    /// <summary>The script for the next connection accepted; a connection with no script is held open, unread.</summary>
    public void Expect(Func<Conversation, Task> script) => _scripts.Enqueue(script);

    /// <summary>Waits for every script started so far to finish, rethrowing a script's failure.</summary>
    public async Task CompletionAsync()
    {
        foreach (var task in _running)
            await task.WaitAsync(TimeSpan.FromSeconds(30));
    }

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
        foreach (var conversation in Conversations)
            conversation.Dispose();
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
            var conversation = new Conversation(socket, _stopping.Token);
            Conversations.Enqueue(conversation);
            if (_scripts.TryDequeue(out var script))
                _running.Enqueue(Task.Run(() => script(conversation)));
        }
    }

    /// <summary>One accepted connection.</summary>
    public sealed class Conversation : IDisposable
    {
        private readonly Socket _socket;
        private readonly CancellationToken _stopping;
        private readonly StringBuilder _unread = new();
        private readonly StringBuilder _all = new();
        private readonly Lock _gate = new();
        private Stream _stream;

        public Conversation(Socket socket, CancellationToken stopping)
        {
            _socket = socket;
            _stopping = stopping;
            _stream = new NetworkStream(socket, ownsSocket: false);
        }

        /// <summary>Everything the client sent so far.</summary>
        public string Received
        {
            get
            {
                lock (_gate)
                    return _all.ToString();
            }
        }

        /// <summary>Reads until what arrived since the last read ends with <paramref name="end"/>, and returns it.</summary>
        public async Task<string> ReadUntilAsync(string end)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(ReadTimeout);
            var buffer = new byte[4096];
            while (true)
            {
                var text = _unread.ToString();
                var index = text.IndexOf(end, StringComparison.Ordinal);
                if (index >= 0)
                {
                    _unread.Remove(0, index + end.Length);
                    return text[..(index + end.Length)];
                }
                var read = await _stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    throw new EndOfStreamException($"The client closed the connection; unread: {text}");
                var chunk = Encoding.Latin1.GetString(buffer, 0, read);
                _unread.Append(chunk);
                lock (_gate)
                    _all.Append(chunk);
            }
        }

        /// <summary>Reads until the client closes the connection and returns what arrived since the last read.</summary>
        public async Task<string> ReadToEndAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(ReadTimeout);
            var buffer = new byte[4096];
            while (true)
            {
                int read;
                try
                {
                    read = await _stream.ReadAsync(buffer, timeout.Token);
                }
                catch (IOException)
                {
                    read = 0;
                }
                if (read == 0)
                {
                    var text = _unread.ToString();
                    _unread.Clear();
                    return text;
                }
                var chunk = Encoding.Latin1.GetString(buffer, 0, read);
                _unread.Append(chunk);
                lock (_gate)
                    _all.Append(chunk);
            }
        }

        public async Task SendAsync(string text)
        {
            await _stream.WriteAsync(Encoding.Latin1.GetBytes(text), _stopping);
            await _stream.FlushAsync(_stopping);
        }

        /// <summary>Continues over TLS as the server, with <paramref name="certificate"/>.</summary>
        public async Task StartTlsAsync(X509Certificate2 certificate)
        {
            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.None,
            }, _stopping);
            _stream = ssl;
        }

        public void Close()
        {
            try
            {
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (SocketException)
            {
            }
            _socket.Close();
        }

        public void Dispose()
        {
            _stream.Dispose();
            _socket.Dispose();
        }
    }
}
