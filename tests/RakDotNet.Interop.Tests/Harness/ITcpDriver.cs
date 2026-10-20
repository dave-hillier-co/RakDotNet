using System.Net;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A TCPInterface or PacketizedTCP next to the peer, with HTTPConnection and HTTPConnection2 on it: the tcp*,
/// httpConnection* and http2* commands of docs/interop-protocol.md. Both drivers implement it. The driver polls the
/// interface as RakNet's samples do and keeps what it reports until <see cref="PollTcpAsync"/>.
/// </summary>
public interface ITcpDriver
{
    /// <summary>Creates the interface on first use, then Start; the listening port, 0 when not listening.</summary>
    Task<TcpStarted> StartTcpAsync(TcpStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>Stop</summary>
    Task StopTcpAsync(CancellationToken cancellationToken = default);

    /// <summary>Connect; the returned address, null for UNASSIGNED_SYSTEM_ADDRESS.</summary>
    Task<IPEndPoint?> TcpConnectAsync(IPEndPoint remote, bool block, CancellationToken cancellationToken = default);

    /// <summary>Send; a null address is UNASSIGNED_SYSTEM_ADDRESS.</summary>
    Task TcpSendAsync(IPEndPoint? address, ReadOnlyMemory<byte> data, bool broadcast = false, CancellationToken cancellationToken = default);

    /// <summary>SendList</summary>
    Task<bool> TcpSendListAsync(IPEndPoint? address, IReadOnlyList<ReadOnlyMemory<byte>> parts, bool broadcast = false, CancellationToken cancellationToken = default);

    /// <summary>CloseConnection</summary>
    Task TcpCloseAsync(IPEndPoint address, CancellationToken cancellationToken = default);

    /// <summary>WasStarted, GetConnectionList, GetConnectionCount, and GetOutgoingDataBufferSize of <paramref name="address"/>.</summary>
    Task<TcpInfo> GetTcpInfoAsync(IPEndPoint? address = null, CancellationToken cancellationToken = default);

    /// <summary>What the polling reported since the last call, in order.</summary>
    Task<IReadOnlyList<TcpEvent>> PollTcpAsync(CancellationToken cancellationToken = default);

    /// <summary>RakString::FormatForGET, FormatForPOST, FormatForPUT or FormatForDELETE.</summary>
    Task<byte[]> FormatHttpRequestAsync(HttpRequestSpec request, CancellationToken cancellationToken = default);

    /// <summary>An HTTPConnection on the interface, Init with the host and port; the polling then feeds it.</summary>
    Task StartHttpConnectionAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>HTTPConnection::Get</summary>
    Task HttpConnectionGetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>HTTPConnection::Post</summary>
    Task HttpConnectionPostAsync(string path, string data, string contentType = "application/x-www-form-urlencoded", CancellationToken cancellationToken = default);

    /// <summary>IsBusy, GetState, GetServerAddress, then every Read and HasBadResponse result.</summary>
    Task<HttpConnectionStatus> PollHttpConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of an HTTPConnection2.</summary>
    Task AttachHttpConnection2Async(CancellationToken cancellationToken = default);

    /// <summary>HTTPConnection2::TransmitRequest</summary>
    Task<bool> TransmitHttpRequestAsync(ReadOnlyMemory<byte> request, string host, int port, IPEndPoint? useAddress = null, CancellationToken cancellationToken = default);

    /// <summary>IsBusy, then every GetResponse result.</summary>
    Task<HttpConnection2Status> PollHttpConnection2Async(CancellationToken cancellationToken = default);
}

/// <summary>The fields of tcpStart. A null host listens on every address.</summary>
public sealed record TcpStartOptions(bool Packetized = false, int Port = 0, int MaxIncoming = 0, int MaxConnections = 0, string? Host = "127.0.0.1");

public sealed record TcpStarted(bool Started, int Port);

/// <summary>
/// One report of the polling: newIncomingConnection, completedConnectionAttempt, failedConnectionAttempt,
/// lostConnection, or packet with its data.
/// </summary>
public sealed record TcpEvent(string Kind, IPEndPoint? Address, byte[] Data);

public sealed record TcpInfo(bool Started, IReadOnlyList<IPEndPoint?> Connections, int ConnectionCount, int? OutgoingBytes);

/// <summary>The fields of httpFormat; <see cref="Method"/> is GET, POST, PUT or DELETE.</summary>
public sealed record HttpRequestSpec(string Method, string Uri, string ContentType = "", string Body = "", string ExtraHeaders = "");

/// <summary>An HTTPConnection's state (HTTPConnection.h:139-146 as an integer), and the results and bad responses read.</summary>
public sealed record HttpConnectionStatus(bool Busy, int State, IPEndPoint? ServerAddress, IReadOnlyList<byte[]> Results, IReadOnlyList<HttpBadResponse> BadResponses);

public sealed record HttpBadResponse(int Code, byte[] Data);

public sealed record HttpConnection2Status(bool Busy, IReadOnlyList<HttpConnection2Response> Responses);

/// <summary>One GetResponse: the request transmitted, the host given, the response, its source and the body offset.</summary>
public sealed record HttpConnection2Response(byte[] StringTransmitted, string HostTransmitted, byte[] Response, IPEndPoint? HostReceived, int ContentOffset);
