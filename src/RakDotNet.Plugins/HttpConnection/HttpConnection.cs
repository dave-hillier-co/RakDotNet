using System.Globalization;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's HTTPConnection: HTTP/1.0 GET and POST requests to one server over a <see cref="TcpInterface"/>, one at a
/// time, each on a new connection whose response is complete when the server closes it. RakNet deprecates it in favour
/// of <see cref="HttpConnection2"/>.
/// </summary>
/// <remarks>
/// <para>
/// The application drives it as RakNet's: <see cref="Update"/> regularly, and every packet the
/// <see cref="TcpInterface"/> returns passed to <see cref="ProcessTcpPacket"/>. Update polls the interface's
/// completed, failed and lost connection queues itself, so the application leaves those to it.
/// </para>
/// <para>
/// RakNet's handling of the response is kept: the received text is URL-decoded as it arrives, a first packet whose
/// status code is above 299 becomes a bad response, a response is complete early only when a header line named
/// "Length" says so, and <see cref="Read"/> returns the text from its first '%' or byte 1 to 3, or all of it when it
/// has none (HTTPConnection.cpp:199-290). A request whose connection attempt fails stays queued, so the next
/// <see cref="Update"/> tries again.
/// </para>
/// </remarks>
public sealed class HttpConnection : IDisposable
{
    private const string DefaultContentType = "application/x-www-form-urlencoded";

    private readonly TcpInterface _tcp;
    private readonly string _host;
    private readonly ushort _port;
    private readonly Queue<OutgoingCommand> _outgoingCommands = new();
    private readonly Queue<(int Code, string Data)> _badResponses = new();
    private readonly Queue<byte[]> _results = new();
    private readonly List<byte> _incomingData = [];
    private ConnectionState _state;

    /// <summary>HTTPConnection plus Init: requests go to <paramref name="host"/>:<paramref name="port"/> through <paramref name="tcp"/>, which must be started.</summary>
    public HttpConnection(TcpInterface tcp, string host, ushort port = 80)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(host);
        _tcp = tcp;
        _host = host;
        _port = port;
    }

    /// <summary>The connection states of HTTPConnection.h:139-146; GetState returns them as integers.</summary>
    public enum ConnectionState
    {
        None,
        Disconnecting,
        Connecting,
        Connected,
        Processing,
    }

    /// <summary>GetServerAddress: the server once a connection to it completed.</summary>
    public SystemAddress ServerAddress { get; private set; }

    /// <summary>GetState</summary>
    public ConnectionState State => _state;

    /// <summary>IsBusy: false only when idle.</summary>
    public bool IsBusy => _state != ConnectionState.None;

    /// <summary>HasRead: a result is ready for <see cref="Read"/>.</summary>
    public bool HasRead => _results.Count > 0;

    /// <summary>Post: queues a POST of <paramref name="data"/> to <paramref name="path"/> (HTTPConnection.cpp:44-53).</summary>
    public void Post(string path, string data, string contentType = DefaultContentType) =>
        _outgoingCommands.Enqueue(new OutgoingCommand(path, data, contentType, IsPost: true));

    /// <summary>Get: queues a GET of <paramref name="path"/> (HTTPConnection.cpp:55-61).</summary>
    public void Get(string path) => _outgoingCommands.Enqueue(new OutgoingCommand(path, "", "", IsPost: false));

    /// <summary>
    /// Read: the next result from the first '%' or byte 1 to 3 on, or all of it when there is none; empty when there is
    /// no result (HTTPConnection.cpp:199-212).
    /// </summary>
    public string Read()
    {
        if (!_results.TryDequeue(out var result))
            return "";
        var start = result.AsSpan().IndexOfAny("\u0001\u0002\u0003%"u8);
        return Encoding.UTF8.GetString(start < 0 ? result : result.AsSpan(start));
    }

    /// <summary>HasBadResponse: the status code and first packet of the next response above 299 (HTTPConnection.cpp:63-73).</summary>
    public bool HasBadResponse(out int code, out string data)
    {
        if (!_badResponses.TryDequeue(out var bad))
        {
            code = 0;
            data = "";
            return false;
        }
        (code, data) = bad;
        return true;
    }

    /// <summary>
    /// Update: takes the interface's connection events, then connects for a queued request, sends it once connected, or
    /// finishes a closed response once no packets are waiting (HTTPConnection.cpp:78-194).
    /// </summary>
    public void Update()
    {
        for (var address = _tcp.HasCompletedConnectionAttempt(); address != SystemAddress.Unassigned; address = _tcp.HasCompletedConnectionAttempt())
        {
            _state = ConnectionState.Connected;
            ServerAddress = address;
        }
        while (_tcp.HasFailedConnectionAttempt() != SystemAddress.Unassigned)
            CloseConnection();
        while (_tcp.HasLostConnection() != SystemAddress.Unassigned)
            CloseConnection();

        switch (_state)
        {
            case ConnectionState.None:
                if (_outgoingCommands.Count == 0)
                    return;
                ServerAddress = _tcp.Connect(_host, _port, block: false);
                _state = ConnectionState.Connecting;
                break;
            case ConnectionState.Disconnecting:
                if (!_tcp.ReceiveHasPackets())
                {
                    if (_incomingData.Count > 0)
                        _results.Enqueue([.. _incomingData]);
                    _incomingData.Clear();
                    _tcp.CloseConnection(ServerAddress);
                    _state = ConnectionState.None;
                }
                break;
            case ConnectionState.Connected:
                if (_outgoingCommands.Count == 0)
                {
                    CloseConnection();
                    return;
                }
                var command = _outgoingCommands.Dequeue();
                var request = command.IsPost
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"POST {command.RemotePath} HTTP/1.0\r\nHost: {_host}:{_port}\r\nContent-Type: {command.ContentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(command.Data)}\r\n\r\n{command.Data}")
                    : string.Create(CultureInfo.InvariantCulture, $"GET {command.RemotePath} HTTP/1.0\r\nHost: {_host}:{_port}\r\n\r\n");
                _tcp.Send(Encoding.UTF8.GetBytes(request), ServerAddress, broadcast: false);
                _state = ConnectionState.Processing;
                break;
            case ConnectionState.Connecting:
            case ConnectionState.Processing:
                break;
        }
    }

    /// <summary>
    /// ProcessTCPPacket: takes a packet from the server. The first packet of a response with a status code above 299
    /// becomes a bad response and ends the exchange (HTTPConnection.cpp:217-290).
    /// </summary>
    public void ProcessTcpPacket(TcpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Address != ServerAddress)
            return;
        var data = HttpFormat.CString(packet.Data.Span);
        if (_incomingData.Count == 0)
        {
            // atoi(data + strlen("HTTP/1.0 ")).
            var code = Atoi(data.Length > 9 ? data[9..] : []);
            if (code > 299)
            {
                _badResponses.Enqueue((code, Encoding.UTF8.GetString(data)));
                CloseConnection();
                return;
            }
        }

        _incomingData.AddRange(HttpFormat.UrlDecode(data));
        if (_state != ConnectionState.Processing)
            return;
        var text = _incomingData.ToArray().AsSpan();
        var bodyStart = text.IndexOf("\r\n\r\n"u8);
        if (bodyStart < 0)
            return;
        var headersLength = bodyStart + 4;
        // RakNet looks for a header named "Length", not "Content-Length" (HTTPConnection.cpp:268).
        var lengthHeader = text.IndexOf("\r\nLength: "u8);
        if (lengthHeader >= 0 && text.Length >= Atol(text[(lengthHeader + 10)..]) + headersLength)
            CloseConnection();
    }

    /// <summary>~HTTPConnection: closes the connection to the server (HTTPConnection.cpp:303-307).</summary>
    public void Dispose() => _tcp.CloseConnection(ServerAddress);

    private void CloseConnection() => _state = ConnectionState.Disconnecting;

    // atoi and atol: leading white space, a sign, then decimal digits.
    private static int Atoi(ReadOnlySpan<byte> text) => unchecked((int)Atol(text));

    private static long Atol(ReadOnlySpan<byte> text)
    {
        var i = 0;
        while (i < text.Length && text[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            i++;
        var negative = false;
        if (i < text.Length && text[i] is (byte)'+' or (byte)'-')
            negative = text[i++] == '-';
        long value = 0;
        while (i < text.Length && text[i] is >= (byte)'0' and <= (byte)'9')
            value = unchecked(value * 10 + (text[i++] - '0'));
        return negative ? -value : value;
    }

    private sealed record OutgoingCommand(string RemotePath, string Data, string ContentType, bool IsPost);
}
