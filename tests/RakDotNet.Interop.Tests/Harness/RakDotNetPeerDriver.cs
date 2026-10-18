using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Transport;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives a RakDotNet peer in this process, behaving as tools/interop-peer does: the same command results and errors,
/// and every event reported as the packet RakPeer::Receive would return for it.
/// </summary>
public sealed class RakDotNetPeerDriver : IPeerDriver
{
    // interop-peer rejects offline ping responses of 400 bytes or more (main.cpp, kMaxOfflinePingResponse).
    private const int MaxOfflinePingResponse = 399;

    private readonly Channel<ReceivedPacket> _packets = Channel.CreateUnbounded<ReceivedPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private PeerTransport? _transport;
    private Task _pump = Task.CompletedTask;
    private int _disposed;

    private RakDotNetPeerDriver()
    {
    }

    public string Kind => "rakdotnet";

    public ChannelReader<ReceivedPacket> Packets => _packets.Reader;

    public static RakDotNetPeerDriver Launch() => new();

    public async Task<PeerStarted> StartupAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_transport is not null)
        {
            throw new PeerCommandException("startup", "Startup failed: RAKNET_ALREADY_STARTED");
        }

        var transport = await PeerTransport.StartAsync(
            new PeerTransportOptions
            {
                LocalEndPoint = new IPEndPoint(IPAddress.Parse(options.Host), options.Port),
                MaxConnections = options.MaxConnections,
                MaxIncomingConnections = options.MaxIncoming,
            },
            cancellationToken).ConfigureAwait(false);
        _transport = transport;
        _pump = Task.Run(() => PumpAsync(transport), CancellationToken.None);
        return new PeerStarted(transport.BoundEndPoint, transport.Guid);
    }

    public Task<string> ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = Started("connect").Execute((core, nowUs) =>
            core.Connect(options.Remote, options.Password.Span, options.Attempts, options.AttemptIntervalMs, options.TimeoutMs, nowUs));
        return Task.FromResult(ToProtocolName(result));
    }

    public Task<uint> SendAsync(SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Data.IsEmpty)
        {
            throw new PeerCommandException("send", "field 'data' must contain at least the message ID byte");
        }
        if (options.Broadcast)
        {
            throw new NotSupportedException("The RakDotNet peer core does not broadcast yet.");
        }

        // RakPeer::Send assigns the receipt (RakPeer.cpp:1308-1352); the core does the same.
        var receipt = Started("send").Execute((core, nowUs) =>
            core.Send(options.Target, options.Data.Span, options.Reliability, options.Priority, options.Channel, nowUs));
        return Task.FromResult(receipt);
    }

    public Task CloseAsync(ulong target, bool notify = true, CancellationToken cancellationToken = default)
    {
        // CloseConnection's defaults: ordering channel 0 and LOW_PRIORITY (RakPeerInterface.h).
        Started("close").Execute((core, nowUs) => core.CloseConnection(target, notify, 0, Priority.Low, nowUs));
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(IPEndPoint remote, bool onlyIfAccepting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return Task.FromResult(Started("ping").Execute((core, nowUs) => core.Ping(remote, onlyIfAccepting, nowUs)));
    }

    public Task SetOfflinePingResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > MaxOfflinePingResponse)
        {
            throw new PeerCommandException("setOfflinePingResponse", "offline ping response must be shorter than 400 bytes");
        }
        Started("setOfflinePingResponse").Execute((core, _) => core.SetOfflinePingResponse(data.Span));
        return Task.CompletedTask;
    }

    public Task<string> GetConnectionStateAsync(ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToProtocolName(Started("connectionState").Execute((core, _) => core.GetConnectionState(target))));

    public async Task ShutdownAsync(int blockMs = 100, CancellationToken cancellationToken = default)
    {
        if (_transport is { } transport)
        {
            await transport.ShutdownAsync(TimeSpan.FromMilliseconds(blockMs), cancellationToken).ConfigureAwait(false);
        }
        await _pump.ConfigureAwait(false);
        _packets.Writer.TryComplete();
    }

    /// <summary>Shuts down as interop-peer does at stdin EOF: Shutdown(100), notifying connected systems.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        await ShutdownAsync(100, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The packet interop-peer would report for <paramref name="peerEvent"/>: its ID (the one after the timestamp for
    /// ID_TIMESTAMP), C++ name, sender and data (tools/interop-peer/src/PacketEvent.cpp).
    /// </summary>
    internal static ReceivedPacket ToReceivedPacket(PeerEvent peerEvent, long timeMs)
    {
        ArgumentNullException.ThrowIfNull(peerEvent);
        const int timeSize = sizeof(ulong);
        var data = peerEvent.Packet;
        var span = data.Span;
        var timestamped = span[0] == (byte)MessageId.Timestamp && span.Length >= 1 + timeSize + 1;
        var id = timestamped ? span[1 + timeSize] : span[0];
        ulong? timestamp = timestamped ? BinaryPrimitives.ReadUInt64BigEndian(span[1..]) : null;
        return new ReceivedPacket(id, MessageNames.Of(id), peerEvent.Guid, peerEvent.Remote, data, timeMs, timestamp);
    }

    // RakNetTypes.h:58-66, as interop-peer names them.
    internal static string ToProtocolName(ConnectionAttemptResult result) => result switch
    {
        ConnectionAttemptResult.ConnectionAttemptStarted => "CONNECTION_ATTEMPT_STARTED",
        ConnectionAttemptResult.InvalidParameter => "INVALID_PARAMETER",
        ConnectionAttemptResult.CannotResolveDomainName => "CANNOT_RESOLVE_DOMAIN_NAME",
        ConnectionAttemptResult.AlreadyConnectedToEndpoint => "ALREADY_CONNECTED_TO_ENDPOINT",
        ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress => "CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS",
        ConnectionAttemptResult.SecurityInitializationFailed => "SECURITY_INITIALIZATION_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    // RakNetTypes.h:69-85, as interop-peer names them.
    internal static string ToProtocolName(ConnectionState state) => state switch
    {
        ConnectionState.IsPending => "IS_PENDING",
        ConnectionState.IsConnecting => "IS_CONNECTING",
        ConnectionState.IsConnected => "IS_CONNECTED",
        ConnectionState.IsDisconnecting => "IS_DISCONNECTING",
        ConnectionState.IsSilentlyDisconnecting => "IS_SILENTLY_DISCONNECTING",
        ConnectionState.IsDisconnected => "IS_DISCONNECTED",
        ConnectionState.IsNotConnected => "IS_NOT_CONNECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private PeerTransport Started(string command) =>
        _transport ?? throw new PeerCommandException(command, "the peer has not been started");

    private async Task PumpAsync(PeerTransport transport)
    {
        try
        {
            await foreach (var peerEvent in transport.Events.ReadAllAsync().ConfigureAwait(false))
            {
                _packets.Writer.TryWrite(ToReceivedPacket(peerEvent, transport.NowMs));
            }
            _packets.Writer.TryComplete();
        }
        catch (System.Net.Sockets.SocketException exception)
        {
            // The transport's socket failed; waits on the packet stream end with the reason.
            _packets.Writer.TryComplete(exception);
        }
    }
}
