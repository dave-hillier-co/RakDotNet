using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : ITcpDriver
{
    public async Task<TcpStarted> StartTcpAsync(TcpStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var fields = new JsonObject
        {
            ["packetized"] = options.Packetized,
            ["port"] = options.Port,
            ["maxIncoming"] = options.MaxIncoming,
            ["maxConnections"] = options.MaxConnections,
        };
        if (options.Host is { } host)
            fields["host"] = host;
        var reply = await SendCommandAsync("tcpStart", fields, cancellationToken).ConfigureAwait(false);
        return new TcpStarted((bool)reply["started"]!, (int)reply["port"]!);
    }

    public Task StopTcpAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("tcpStop", new JsonObject(), cancellationToken);

    public async Task<IPEndPoint?> TcpConnectAsync(IPEndPoint remote, bool block, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var reply = await SendCommandAsync("tcpConnect", new JsonObject
        {
            ["host"] = remote.Address.ToString(),
            ["port"] = remote.Port,
            ["block"] = block,
        }, cancellationToken).ConfigureAwait(false);
        return ParseTcpAddress(reply["address"]);
    }

    public Task TcpSendAsync(IPEndPoint? address, ReadOnlyMemory<byte> data, bool broadcast = false, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tcpSend", new JsonObject
        {
            ["address"] = FormatTcpAddress(address),
            ["data"] = Convert.ToHexStringLower(data.Span),
            ["broadcast"] = broadcast,
        }, cancellationToken);

    public async Task<bool> TcpSendListAsync(IPEndPoint? address, IReadOnlyList<ReadOnlyMemory<byte>> parts, bool broadcast = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var reply = await SendCommandAsync("tcpSendList", new JsonObject
        {
            ["address"] = FormatTcpAddress(address),
            ["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)Convert.ToHexStringLower(p.Span))]),
            ["broadcast"] = broadcast,
        }, cancellationToken).ConfigureAwait(false);
        return (bool)reply["result"]!;
    }

    public Task TcpCloseAsync(IPEndPoint address, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tcpClose", new JsonObject { ["address"] = FormatTcpAddress(address) }, cancellationToken);

    public async Task<TcpInfo> GetTcpInfoAsync(IPEndPoint? address = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject();
        if (address is not null)
            fields["address"] = FormatTcpAddress(address);
        var reply = await SendCommandAsync("tcpInfo", fields, cancellationToken).ConfigureAwait(false);
        return new TcpInfo(
            (bool)reply["started"]!,
            [.. reply["connections"]!.AsArray().Select(ParseTcpAddress)],
            (int)reply["connectionCount"]!,
            (int?)reply["outgoingBytes"]);
    }

    public async Task<IReadOnlyList<TcpEvent>> PollTcpAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("tcpPoll", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return [.. reply["events"]!.AsArray().Select(e => new TcpEvent(
            (string)e!["kind"]!,
            ParseTcpAddress(e["address"]),
            Convert.FromHexString((string?)e["data"] ?? "")))];
    }

    public async Task<byte[]> FormatHttpRequestAsync(HttpRequestSpec request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await SendCommandAsync("httpFormat", new JsonObject
        {
            ["method"] = request.Method,
            ["uri"] = request.Uri,
            ["contentType"] = request.ContentType,
            ["body"] = request.Body,
            ["extraHeaders"] = request.ExtraHeaders,
        }, cancellationToken).ConfigureAwait(false);
        return Convert.FromHexString((string)reply["request"]!);
    }

    public Task StartHttpConnectionAsync(string host, int port, CancellationToken cancellationToken = default) =>
        SendCommandAsync("httpConnectionStart", new JsonObject { ["host"] = host, ["port"] = port }, cancellationToken);

    public Task HttpConnectionGetAsync(string path, CancellationToken cancellationToken = default) =>
        SendCommandAsync("httpConnectionGet", new JsonObject { ["path"] = path }, cancellationToken);

    public Task HttpConnectionPostAsync(string path, string data, string contentType = "application/x-www-form-urlencoded", CancellationToken cancellationToken = default) =>
        SendCommandAsync("httpConnectionPost", new JsonObject { ["path"] = path, ["data"] = data, ["contentType"] = contentType }, cancellationToken);

    public async Task<HttpConnectionStatus> PollHttpConnectionAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("httpConnectionPoll", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return new HttpConnectionStatus(
            (bool)reply["busy"]!,
            (int)reply["state"]!,
            ParseTcpAddress(reply["serverAddress"]),
            [.. reply["results"]!.AsArray().Select(r => Convert.FromHexString((string)r!))],
            [.. reply["badResponses"]!.AsArray().Select(b => new HttpBadResponse((int)b!["code"]!, Convert.FromHexString((string)b["data"]!)))]);
    }

    public Task AttachHttpConnection2Async(CancellationToken cancellationToken = default) =>
        SendCommandAsync("http2Attach", new JsonObject(), cancellationToken);

    public async Task<bool> TransmitHttpRequestAsync(ReadOnlyMemory<byte> request, string host, int port, IPEndPoint? useAddress = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject
        {
            ["request"] = Convert.ToHexStringLower(request.Span),
            ["host"] = host,
            ["port"] = port,
        };
        if (useAddress is not null)
            fields["useAddress"] = FormatTcpAddress(useAddress);
        var reply = await SendCommandAsync("http2Transmit", fields, cancellationToken).ConfigureAwait(false);
        return (bool)reply["result"]!;
    }

    public async Task<HttpConnection2Status> PollHttpConnection2Async(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("http2Poll", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return new HttpConnection2Status(
            (bool)reply["busy"]!,
            [.. reply["responses"]!.AsArray().Select(r => new HttpConnection2Response(
                Convert.FromHexString((string)r!["stringTransmitted"]!),
                (string)r["hostTransmitted"]!,
                Convert.FromHexString((string)r["response"]!),
                ParseTcpAddress(r["hostReceived"]),
                (int)r["contentOffset"]!))]);
    }

    // interop-peer writes UNASSIGNED_SYSTEM_ADDRESS as an empty string.
    internal static IPEndPoint? ParseTcpAddress(JsonNode? node) =>
        IPEndPoint.TryParse((string?)node ?? "", out var address) ? address : null;

    internal static string FormatTcpAddress(IPEndPoint? address) => address?.ToString() ?? "";
}
