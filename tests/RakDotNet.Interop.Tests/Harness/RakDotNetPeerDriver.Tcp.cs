using System.Net;
using System.Text;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The tcp*, httpConnection* and http2* commands on a <see cref="TcpInterface"/> or <see cref="PacketizedTcp"/>, with
/// interop-peer's results and errors (tools/interop-peer/src/TcpCommands.cpp), including its polling task.
/// </summary>
public sealed partial class RakDotNetPeerDriver : ITcpDriver
{
    private readonly Lock _tcpGate = new();
    private readonly List<TcpEvent> _tcpEvents = [];
    private TcpInterface? _tcp;
    private HttpConnection? _http;
    private HttpConnection2? _http2;
    private CancellationTokenSource? _tcpPolling;
    private Task _tcpPoller = Task.CompletedTask;

    public Task<TcpStarted> StartTcpAsync(TcpStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_tcpGate)
        {
            if (_tcp is null)
            {
                _tcp = options.Packetized ? new PacketizedTcp() : new TcpInterface();
                _tcpPolling = new CancellationTokenSource();
                var polling = _tcpPolling.Token;
                _tcpPoller = Task.Run(() => PollTcpLoopAsync(polling), CancellationToken.None);
            }
            else if (options.Packetized != _tcp is PacketizedTcp)
            {
                throw new PeerCommandException("tcpStart", "the TCP interface was created with another value of 'packetized'");
            }
            var started = _tcp.Start((ushort)options.Port, (ushort)options.MaxIncoming, (ushort)options.MaxConnections, options.Host);
            return Task.FromResult(new TcpStarted(started, _tcp.ListenEndPoint?.Port ?? 0));
        }
    }

    /// <summary>tcpStop, and the end of the polling task, which the native peer ends with its process.</summary>
    public async Task StopTcpAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? polling;
        lock (_tcpGate)
        {
            RequireTcp("tcpStop").Stop();
            polling = _tcpPolling;
            _tcpPolling = null;
        }
        if (polling is not null)
        {
            await polling.CancelAsync().ConfigureAwait(false);
            await _tcpPoller.ConfigureAwait(false);
            polling.Dispose();
        }
    }

    public Task<IPEndPoint?> TcpConnectAsync(IPEndPoint remote, bool block, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        lock (_tcpGate)
        {
            // A blocking connect holds the lock, as it holds interop-peer's.
            var address = RequireTcp("tcpConnect").Connect(remote.Address.ToString(), (ushort)remote.Port, block);
            return Task.FromResult(ToEndPoint(address));
        }
    }

    public Task TcpSendAsync(IPEndPoint? address, ReadOnlyMemory<byte> data, bool broadcast = false, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
            RequireTcp("tcpSend").Send(data.Span, ToAddress(address), broadcast);
        return Task.CompletedTask;
    }

    public Task<bool> TcpSendListAsync(IPEndPoint? address, IReadOnlyList<ReadOnlyMemory<byte>> parts, bool broadcast = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        lock (_tcpGate)
            return Task.FromResult(RequireTcp("tcpSendList").SendList(parts.ToArray(), ToAddress(address), broadcast));
    }

    public Task TcpCloseAsync(IPEndPoint address, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
            RequireTcp("tcpClose").CloseConnection(ToAddress(address));
        return Task.CompletedTask;
    }

    public Task<TcpInfo> GetTcpInfoAsync(IPEndPoint? address = null, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            var tcp = RequireTcp("tcpInfo");
            return Task.FromResult(new TcpInfo(
                tcp.WasStarted,
                [.. tcp.GetConnectionList().Take(64).Select(ToEndPoint)],
                tcp.ConnectionCount,
                address is null ? null : tcp.GetOutgoingDataBufferSize(ToAddress(address))));
        }
    }

    public Task<IReadOnlyList<TcpEvent>> PollTcpAsync(CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            RequireTcp("tcpPoll");
            IReadOnlyList<TcpEvent> events = [.. _tcpEvents];
            _tcpEvents.Clear();
            return Task.FromResult(events);
        }
    }

    public Task<byte[]> FormatHttpRequestAsync(HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = request.Method switch
        {
            "GET" => HttpFormat.FormatForGet(request.Uri, request.ExtraHeaders),
            "POST" => HttpFormat.FormatForPost(request.Uri, request.ContentType, request.Body, request.ExtraHeaders),
            "PUT" => HttpFormat.FormatForPut(request.Uri, request.ContentType, request.Body, request.ExtraHeaders),
            "DELETE" => HttpFormat.FormatForDelete(request.Uri, request.ExtraHeaders),
            _ => throw new PeerCommandException("httpFormat", "field 'method' must be GET, POST, PUT or DELETE"),
        };
        return Task.FromResult(Encoding.UTF8.GetBytes(text));
    }

    public Task StartHttpConnectionAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            var tcp = RequireTcp("httpConnectionStart");
            if (_http is not null)
                throw new PeerCommandException("httpConnectionStart", "an HTTPConnection is already started");
            _http = new HttpConnection(tcp, host, (ushort)port);
        }
        return Task.CompletedTask;
    }

    public Task HttpConnectionGetAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
            RequireHttp("httpConnectionGet").Get(path);
        return Task.CompletedTask;
    }

    public Task HttpConnectionPostAsync(string path, string data, string contentType = "application/x-www-form-urlencoded", CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
            RequireHttp("httpConnectionPost").Post(path, data, contentType);
        return Task.CompletedTask;
    }

    public Task<HttpConnectionStatus> PollHttpConnectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            var http = RequireHttp("httpConnectionPoll");
            var results = new List<byte[]>();
            while (http.HasRead)
                results.Add(Encoding.UTF8.GetBytes(http.Read()));
            var badResponses = new List<HttpBadResponse>();
            while (http.HasBadResponse(out var code, out var data))
                badResponses.Add(new HttpBadResponse(code, Encoding.UTF8.GetBytes(data)));
            return Task.FromResult(new HttpConnectionStatus(http.IsBusy, (int)http.State, ToEndPoint(http.ServerAddress), results, badResponses));
        }
    }

    public Task AttachHttpConnection2Async(CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            var tcp = RequireTcp("http2Attach");
            if (_http2 is null)
            {
                _http2 = new HttpConnection2();
                tcp.AttachPlugin(_http2);
            }
        }
        return Task.CompletedTask;
    }

    public Task<bool> TransmitHttpRequestAsync(ReadOnlyMemory<byte> request, string host, int port, IPEndPoint? useAddress = null, CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            // interop-peer passes the request as a C string, so it ends at a zero byte.
            var text = Encoding.UTF8.GetString(request.Span[..(request.Span.IndexOf((byte)0) is var end and >= 0 ? end : request.Length)]);
            return Task.FromResult(RequireHttp2().TransmitRequest(text, host, (ushort)port, useAddress: ToAddress(useAddress)));
        }
    }

    public Task<HttpConnection2Status> PollHttpConnection2Async(CancellationToken cancellationToken = default)
    {
        lock (_tcpGate)
        {
            var http2 = RequireHttp2();
            var responses = new List<HttpConnection2Response>();
            while (http2.TryGetResponse(out var response))
            {
                responses.Add(new HttpConnection2Response(
                    Encoding.UTF8.GetBytes(response.StringTransmitted),
                    response.HostTransmitted,
                    response.Response.ToArray(),
                    ToEndPoint(response.HostReceived),
                    response.ContentOffset));
            }
            return Task.FromResult(new HttpConnection2Status(http2.IsBusy, responses));
        }
    }

    // interop-peer's polling thread: a frame of a RakNet TCP sample every millisecond (TcpCommands.cpp, PollOnce).
    private async Task PollTcpLoopAsync(CancellationToken polling)
    {
        while (!polling.IsCancellationRequested)
        {
            lock (_tcpGate)
            {
                if (_tcp is { } tcp)
                    PollTcpOnce(tcp);
            }
            try
            {
                await Task.Delay(1, polling).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void PollTcpOnce(TcpInterface tcp)
    {
        SystemAddress address;
        if (_http is null)
        {
            while ((address = tcp.HasNewIncomingConnection()) != SystemAddress.Unassigned)
                RecordTcp("newIncomingConnection", address);
            while ((address = tcp.HasCompletedConnectionAttempt()) != SystemAddress.Unassigned)
                RecordTcp("completedConnectionAttempt", address);
        }

        while (tcp.Receive() is { } packet)
        {
            _tcpEvents.Add(new TcpEvent("packet", ToEndPoint(packet.Address), packet.Data.ToArray()));
            _http?.ProcessTcpPacket(packet);
        }

        if (_http is not null)
        {
            _http.Update();
            return;
        }
        while ((address = tcp.HasFailedConnectionAttempt()) != SystemAddress.Unassigned)
            RecordTcp("failedConnectionAttempt", address);
        while ((address = tcp.HasLostConnection()) != SystemAddress.Unassigned)
            RecordTcp("lostConnection", address);
    }

    private void RecordTcp(string kind, SystemAddress address) => _tcpEvents.Add(new TcpEvent(kind, ToEndPoint(address), []));

    private TcpInterface RequireTcp(string command) =>
        _tcp ?? throw new PeerCommandException(command, "no TCPInterface; send tcpStart first");

    private HttpConnection RequireHttp(string command) =>
        _http ?? throw new PeerCommandException(command, "no HTTPConnection; send httpConnectionStart first");

    private HttpConnection2 RequireHttp2() =>
        _http2 ?? throw new PeerCommandException("http2", "no HTTPConnection2 is attached; send http2Attach first");

    private static IPEndPoint? ToEndPoint(SystemAddress address) => address == SystemAddress.Unassigned ? null : address.ToIPEndPoint();

    private static SystemAddress ToAddress(IPEndPoint? endPoint) => endPoint is null ? SystemAddress.Unassigned : endPoint;
}
