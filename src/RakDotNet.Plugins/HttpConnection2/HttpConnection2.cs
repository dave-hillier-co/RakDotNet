using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's HTTPConnection2: a <see cref="TcpInterface"/> plugin that connects to a server, transmits a request and
/// collects the response, keeping the connection for the next request to the same server.
/// </summary>
/// <remarks>
/// <para>
/// Attach it with <see cref="TcpInterface.AttachPlugin"/> and poll the interface each frame, completed attempts,
/// Receive, then failed attempts and lost connections, as Samples/ComprehensivePCGame does: the plugin sends when a
/// connection completes and reads every packet, which still reaches the application.
/// </para>
/// <para>
/// RakNet's rules for a complete response are kept (HTTPConnection2.cpp:181-407). A chunked response holds the
/// joined body only, at offset 0, and is complete when a chunk size of 0 is read, which a packet ending just after a
/// chunk also produces. Otherwise a response is complete when the body reaches its Content-Length, at once with
/// offset -1 when that is 0, at the end of its headers when it has none, or when the connection is lost. A request
/// whose connection attempt fails is dropped, along with others to that server that RakNet's removal loop does not
/// skip (HTTPConnection2.cpp:490-508). A request pending when a connection completes goes to it even when meant for
/// another server, as RakNet assigns it (HTTPConnection2.cpp:459-488). Requests and hosts are IPv4.
/// </para>
/// <para>
/// Received data is treated as RakNet treats its null-terminated packets: up to the first zero byte. TLS is
/// <see cref="TcpInterface.StartSslClient"/>, started before the request is sent when <c>useSsl</c> is set.
/// </para>
/// </remarks>
public sealed class HttpConnection2 : ITcpPlugin
{
    private readonly Lock _gate = new();
    private readonly List<Request> _pendingRequests = [];
    private readonly List<Request> _sentRequests = [];
    private readonly List<Request> _completedRequests = [];
    private TcpInterface? _tcp;

    /// <summary>IsBusy: a request waits to be sent or for its response.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
                return _pendingRequests.Count > 0 || _sentRequests.Count > 0;
        }
    }

    /// <summary>HasResponse: a response waits for <see cref="TryGetResponse"/>.</summary>
    public bool HasResponse
    {
        get
        {
            lock (_gate)
                return _completedRequests.Count > 0;
        }
    }

    /// <summary>
    /// TransmitRequest: sends <paramref name="request"/> (see <see cref="HttpFormat"/>) to <paramref name="host"/>,
    /// connecting first when not connected, or once the request outstanding on a connection completes. With
    /// <paramref name="useAddress"/> the request goes to that connection, which must exist. False when the host does not
    /// resolve or <paramref name="useAddress"/> is not connected (HTTPConnection2.cpp:28-100).
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="TcpInterface"/>.</exception>
    public bool TransmitRequest(string request, string host, ushort port = 80, bool useSsl = false, SystemAddress useAddress = default, object? userData = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        var tcp = _tcp ?? throw new InvalidOperationException("HttpConnection2 is not attached to a TcpInterface.");
        SystemAddress estimated;
        if (useAddress != SystemAddress.Unassigned)
        {
            if (!IsConnected(tcp, useAddress))
                return false;
            estimated = useAddress.WithPort(port);
        }
        else
        {
            if (TcpInterface.Resolve(host) is not { } resolved)
                return false;
            estimated = new SystemAddress(resolved, port);
        }

        var pending = new Request(request, host, port, useSsl, userData) { HostEstimatedAddress = estimated };
        if (IsConnected(tcp, estimated))
        {
            lock (_gate)
            {
                if (_sentRequests.Count > 0)
                {
                    _pendingRequests.Add(pending);
                    return true;
                }
                pending.HostCompletedAddress = estimated;
                _sentRequests.Add(pending);
            }
            SendRequest(tcp, pending);
        }
        else
        {
            lock (_gate)
                _pendingRequests.Add(pending);
            tcp.Connect(host, port, block: false);
        }
        return true;
    }

    /// <summary>
    /// GetResponse: the first completed request, whose place the last then takes, as RakNet removes it
    /// (HTTPConnection2.cpp:107-131).
    /// </summary>
    public bool TryGetResponse([System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out HttpResponse response)
    {
        lock (_gate)
        {
            if (_completedRequests.Count == 0)
            {
                response = null;
                return false;
            }
            var completed = _completedRequests[0];
            RemoveAtIndexFast(_completedRequests, 0);
            response = new HttpResponse(completed.StringToTransmit, completed.Host, completed.StringReceived.ToArray(),
                                        completed.HostCompletedAddress, completed.ContentOffset, completed.UserData);
            return true;
        }
    }

    void ITcpPlugin.OnAttached(TcpInterface tcp) => _tcp = tcp;

    void ITcpPlugin.OnDetached() => _tcp = null;

    PluginResult ITcpPlugin.OnReceive(TcpPacket packet)
    {
        Request? sent;
        lock (_gate)
        {
            var index = _sentRequests.FindIndex(r => r.HostCompletedAddress == packet.Address);
            if (index < 0)
                return PluginResult.Continue;
            sent = _sentRequests[index];
            RemoveAtIndexFast(_sentRequests, index);
        }

        var data = HttpFormat.CString(packet.Data.Span);
        bool done;
        if (data.IndexOf("Transfer-Encoding: chunked"u8) >= 0)
            done = FirstChunk(sent, data);
        else if (sent.Chunked)
            done = ReadChunkBlock(sent, data);
        else
            done = Unchunked(sent, data);

        lock (_gate)
            (done ? _completedRequests : _sentRequests).Add(sent);
        if (done)
            SendPendingRequestToConnectedSystem(packet.Address);
        return PluginResult.Continue;
    }

    void ITcpPlugin.OnConnected(SystemAddress address) => SendPendingRequestToConnectedSystem(address);

    // OnFailedConnectionAttempt (HTTPConnection2.cpp:537-546).
    void ITcpPlugin.OnConnectFailed(SystemAddress address)
    {
        if (address == SystemAddress.Unassigned)
            return;
        RemovePendingRequest(address);
        SendNextPendingRequest();
    }

    // OnClosedConnection (HTTPConnection2.cpp:547-601): the requests sent on the connection complete with what arrived.
    void ITcpPlugin.OnDisconnected(SystemAddress address, TcpDisconnectReason reason)
    {
        if (address == SystemAddress.Unassigned)
            return;
        lock (_gate)
        {
            var i = 0;
            while (i < _sentRequests.Count)
            {
                var sent = _sentRequests[i];
                if (sent.HostCompletedAddress != address)
                {
                    i++;
                    continue;
                }
                if (!sent.Chunked && sent.StringReceived.Count > 0)
                {
                    var received = sent.StringReceived.ToArray().AsSpan();
                    var body = received.IndexOf("\r\n\r\n"u8);
                    sent.ContentOffset = received.IndexOf("Content-Length: "u8) >= 0 && body >= 0 ? body + 4 : 0;
                }
                _completedRequests.Add(sent);
                RemoveAtIndexFast(_sentRequests, i);
            }
        }
        SendNextPendingRequest();
    }

    // HTTPConnection2.cpp:226-280: the first packet of a chunked response.
    private static bool FirstChunk(Request sent, ReadOnlySpan<byte> data)
    {
        sent.Chunked = true;
        var headersEnd = data.IndexOf("\r\n\r\n"u8);
        if (headersEnd < 0)
        {
            // RakNet asserts the headers end in this packet and dereferences past them regardless; here the request
            // keeps waiting.
            return false;
        }
        var chunkStart = data[(headersEnd + 4)..];
        sent.ThisChunkSize = ReadChunkSize(chunkStart, out var sizeEnd);
        sent.BytesReadForThisChunk = 0;
        sent.ContentOffset = 0;
        if (sent.ThisChunkSize == 0)
            return true;
        var body = chunkStart[sizeEnd..];
        return ReadChunkBlock(sent, body.Length >= 2 ? body[2..] : []);
    }

    // ReadChunkBlock (HTTPConnection2.cpp:148-180); true once a chunk size of 0 is read.
    private static bool ReadChunkBlock(Request sent, ReadOnlySpan<byte> text)
    {
        while (true)
        {
            var toRead = sent.ThisChunkSize - sent.BytesReadForThisChunk;
            if ((ulong)text.Length < toRead)
                toRead = (ulong)text.Length;
            sent.StringReceived.AddRange(text[..(int)toRead]);
            text = text[(int)toRead..];
            sent.BytesReadForThisChunk += toRead;
            if (text.IsEmpty)
                return sent.ThisChunkSize == 0;
            if (text.Length >= 2 && text[0] == '\r' && text[1] == '\n')
                text = text[2..];
            sent.ThisChunkSize = ReadChunkSize(text, out var sizeEnd);
            if (sent.ThisChunkSize == 0)
                return true;
            sent.BytesReadForThisChunk = 0;
            text = sizeEnd + 2 <= text.Length ? text[(sizeEnd + 2)..] : [];
        }
    }

    // HTTPConnection2.cpp:304-396: a response that is not chunked.
    private static bool Unchunked(Request sent, ReadOnlySpan<byte> data)
    {
        sent.StringReceived.AddRange(data);
        var received = sent.StringReceived.ToArray().AsSpan();
        if (sent.ContentLength == -1)
        {
            var header = received.IndexOf("Content-Length: "u8);
            if (header >= 0)
            {
                var digits = received[(header + 16)..];
                var count = 0;
                while (count < digits.Length && digits[count] is >= (byte)'0' and <= (byte)'9')
                    count++;
                if (count > 0 && count < digits.Length && digits[count] is (byte)'\r' or (byte)'\n')
                    sent.ContentLength = ReadIntFromSubstring(digits[..count]);
            }
        }

        var body = received.IndexOf("\r\n\r\n"u8);
        if (sent.ContentLength == -1)
        {
            if (body < 0)
                return false;
            sent.ContentOffset = body + 4 == received.Length ? -1 : body + 4;
            return true;
        }
        // Any length not above 0 completes with no body, a negative one read from a long number included
        // (HTTPConnection2.cpp:326-374).
        if (sent.ContentLength <= 0)
        {
            sent.ContentOffset = -1;
            return true;
        }
        if (body < 0 || received.Length - (body + 4) < sent.ContentLength)
            return false;
        sent.ContentOffset = body + 4;
        return true;
    }

    // SendPendingRequestToConnectedSystem (HTTPConnection2.cpp:416-489): the first request for the address, else the
    // first request of all.
    private void SendPendingRequestToConnectedSystem(SystemAddress address)
    {
        if (address == SystemAddress.Unassigned || _tcp is not { } tcp)
            return;
        Request? request;
        lock (_gate)
        {
            var index = _pendingRequests.FindIndex(r => r.HostEstimatedAddress == address);
            if (index < 0 && _pendingRequests.Count > 0)
                index = 0;
            if (index < 0)
                return;
            request = _pendingRequests[index];
            _pendingRequests.RemoveAt(index);
            request.HostCompletedAddress = address;
            _sentRequests.Add(request);
        }
        if (request.UseSsl)
            tcp.StartSslClient(address, request.Host);
        SendRequest(tcp, request);
    }

    // RemovePendingRequest (HTTPConnection2.cpp:490-508): the loop advances twice past a request it keeps, so the one
    // after it is never looked at.
    private void RemovePendingRequest(SystemAddress address)
    {
        lock (_gate)
        {
            for (var i = 0; i < _pendingRequests.Count; i++)
            {
                if (_pendingRequests[i].HostEstimatedAddress == address)
                    _pendingRequests.RemoveAt(i);
                else
                    i++;
            }
        }
    }

    // SendNextPendingRequest (HTTPConnection2.cpp:509-535): connects for the first pending request.
    private void SendNextPendingRequest()
    {
        Request? first;
        lock (_gate)
            first = _pendingRequests.Count > 0 ? _pendingRequests[0] : null;
        if (first is not null)
            _tcp?.Connect(first.Host, first.Port, block: false);
    }

    // IsConnected (HTTPConnection2.cpp:602-615) looks at the first 64 connections.
    private static bool IsConnected(TcpInterface tcp, SystemAddress address) =>
        tcp.GetConnectionList().Take(64).Contains(address);

    private static void SendRequest(TcpInterface tcp, Request request) =>
        tcp.Send(Encoding.UTF8.GetBytes(request.StringToTransmit), request.HostCompletedAddress, broadcast: false);

    // DataStructures::List::RemoveAtIndexFast: the last element takes the place of the removed one.
    private static void RemoveAtIndexFast(List<Request> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    // ReadChunkSize (HTTPConnection2.cpp:140-147): strtoul in base 16, its result through an int.
    private static ulong ReadChunkSize(ReadOnlySpan<byte> text, out int end)
    {
        var i = 0;
        while (i < text.Length && text[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r')
            i++;
        var negative = false;
        if (i < text.Length && text[i] is (byte)'+' or (byte)'-')
            negative = text[i++] == '-';
        if (i + 1 < text.Length && text[i] == '0' && text[i + 1] is (byte)'x' or (byte)'X' && i + 2 < text.Length && IsHex(text[i + 2]))
            i += 2;
        var digitsStart = i;
        ulong value = 0;
        var overflow = false;
        while (i < text.Length && IsHex(text[i]))
        {
            var digit = (ulong)HexDigit(text[i++]);
            if (value > (ulong.MaxValue - digit) / 16)
                overflow = true;
            value = unchecked(value * 16 + digit);
        }
        if (i == digitsStart)
        {
            end = 0;
            return 0;
        }
        end = i;
        if (overflow)
            value = ulong.MaxValue;
        else if (negative)
            value = unchecked(0 - value);
        // int ReadChunkSize, assigned to size_t: truncated to 32 bits, then sign-extended.
        return unchecked((ulong)(long)(int)value);
    }

    private static bool IsHex(byte c) => c is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static int HexDigit(byte c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    // RakString::ReadIntFromSubstring: atoi of at most 31 characters; 32 or more give 0 (RakString.cpp:1463-1471).
    private static int ReadIntFromSubstring(ReadOnlySpan<byte> digits)
    {
        if (digits.Length >= 32)
            return 0;
        long value = 0;
        foreach (var digit in digits)
            value = unchecked(value * 10 + (digit - '0'));
        return unchecked((int)value);
    }

    /// <summary>HTTPConnection2::Request.</summary>
    private sealed class Request(string stringToTransmit, string host, ushort port, bool useSsl, object? userData)
    {
        public string StringToTransmit { get; } = stringToTransmit;

        public string Host { get; } = host;

        public ushort Port { get; } = port;

        public bool UseSsl { get; } = useSsl;

        public object? UserData { get; } = userData;

        public List<byte> StringReceived { get; } = [];

        public SystemAddress HostEstimatedAddress { get; init; }

        public SystemAddress HostCompletedAddress { get; set; }

        public int ContentOffset { get; set; }

        public int ContentLength { get; set; } = -1;

        public bool Chunked { get; set; }

        public ulong ThisChunkSize { get; set; }

        public ulong BytesReadForThisChunk { get; set; }
    }
}

/// <summary>
/// What HTTPConnection2::GetResponse returns: the request as transmitted, the host it was given, the response, the
/// server it came from, and the offset of the body in the response.
/// </summary>
/// <param name="StringTransmitted">The request, as passed to <see cref="HttpConnection2.TransmitRequest"/>.</param>
/// <param name="HostTransmitted">The host passed to <see cref="HttpConnection2.TransmitRequest"/>.</param>
/// <param name="Response">The response bytes, each packet up to its first zero byte.</param>
/// <param name="HostReceived">The connection the response came from.</param>
/// <param name="UserData">What was passed to <see cref="HttpConnection2.TransmitRequest"/>.</param>
/// <param name="ContentOffset">
/// Where the body starts in <paramref name="Response"/>: 0 for a chunked response, which holds only the body, and -1 when
/// there is no body.
/// </param>
public sealed record HttpResponse(string StringTransmitted, string HostTransmitted, ReadOnlyMemory<byte> Response,
                                  SystemAddress HostReceived, int ContentOffset, object? UserData)
{
    /// <summary><see cref="Response"/> as UTF-8 text.</summary>
    public string ResponseText => Encoding.UTF8.GetString(Response.Span);
}
