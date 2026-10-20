using System.Buffers.Binary;
using System.Net;
using System.Threading.Channels;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives a RakDotNet peer in this process through the public API, behaving as tools/interop-peer does: the same command
/// results and errors, and every event reported as the packet RakPeer::Receive would return for it
/// (docs/api-design.md section 10.2).
/// </summary>
public sealed partial class RakDotNetPeerDriver : IPeerDriver
{
    // interop-peer rejects offline ping responses of 400 bytes or more (main.cpp, kMaxOfflinePingResponse).
    private const int MaxOfflinePingResponse = 399;

    private readonly Channel<ReceivedPacket> _packets = Channel.CreateUnbounded<ReceivedPacket>(new UnboundedChannelOptions { SingleWriter = true });
    private RakPeer? _peer;
    private Task _pump = Task.CompletedTask;
    private int _disposed;

    private RakDotNetPeerDriver()
    {
    }

    public string Kind => "rakdotnet";

    public ChannelReader<ReceivedPacket> Packets => _packets.Reader;

    public static RakDotNetPeerDriver Launch() => new();

    public Task<PeerStarted> StartupAsync(StartupOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_peer is not null)
        {
            throw new PeerCommandException("startup", "Startup failed: RAKNET_ALREADY_STARTED");
        }

        RakPeer peer;
        try
        {
            peer = RakPeer.Start(new RakPeerOptions
            {
                MaxConnections = options.MaxConnections,
                MaxIncomingConnections = options.MaxIncoming,
                Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Parse(options.Host), options.Port))],
                // A driver stands for a process of its own, and RakNet's clock starts with the process (GetTime.cpp:183-189);
                // peers on one TimeProvider share a clock, so each driver gets its own.
                TimeProvider = new ProcessClock(),
            });
        }
        catch (RakStartupException exception)
        {
            throw new PeerCommandException("startup", $"Startup failed: {ToProtocolName(exception.Error)}");
        }
        _peer = peer;
        _pump = Task.Run(() => PumpAsync(peer), CancellationToken.None);
        return Task.FromResult(new PeerStarted(peer.Sockets[0].LocalAddress.ToIPEndPoint(), peer.RakNetGuid.Value));
    }

    public Task<string> ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = Started("connect").Connect(options.Remote, new RakDotNet.ConnectOptions
        {
            Password = options.Password,
            AttemptCount = options.Attempts,
            AttemptInterval = TimeSpan.FromMilliseconds(options.AttemptIntervalMs),
            Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs),
        });
        return Task.FromResult(ToProtocolName(result));
    }

    public Task<uint> SendAsync(SendOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Data.IsEmpty)
        {
            throw new PeerCommandException("send", "field 'data' must contain at least the message ID byte");
        }

        var peer = Started("send");
        var target = new RakNetGuid(options.Target);
        if (options.Broadcast)
        {
            return Task.FromResult(peer.Broadcast(options.Data.Span, options.Reliability, options.Priority, options.Channel, except: target).Receipt.Value);
        }
        // RakPeer.cpp:1323-1324: an undefined target returns 0 without using a receipt.
        if (!target.IsAssigned)
        {
            return Task.FromResult(0u);
        }
        return Task.FromResult(peer.Send(options.Data.Span, target, options.Reliability, options.Priority, options.Channel).Receipt.Value);
    }

    public Task CloseAsync(ulong target, bool notify = true, CancellationToken cancellationToken = default)
    {
        var peer = Started("close");
        var guid = new RakNetGuid(target);
        if (guid.IsAssigned)
        {
            // CloseConnection's defaults: ordering channel 0 and LOW_PRIORITY (RakPeerInterface.h).
            peer.Close(guid, notify);
        }
        return Task.CompletedTask;
    }

    public Task<bool> PingAsync(IPEndPoint remote, bool onlyIfAccepting = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return Task.FromResult(Started("ping").PingUnconnected(remote, onlyIfAccepting));
    }

    public Task SetOfflinePingResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length > MaxOfflinePingResponse)
        {
            throw new PeerCommandException("setOfflinePingResponse", "offline ping response must be shorter than 400 bytes");
        }
        Started("setOfflinePingResponse").OfflinePingResponse = data;
        return Task.CompletedTask;
    }

    public Task<uint> SendListAsync(SendListOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Parts.Count == 0 || options.Parts.Any(p => p.IsEmpty))
        {
            throw new PeerCommandException("sendList", "field 'parts' must be a non-empty array of hex strings");
        }

        var peer = Started("sendList");
        // RakPeer::SendList sends the parts as one message (RakPeer.cpp:1371-1435).
        var message = new OutgoingMessage(Sequence(options.Parts));
        var target = new RakNetGuid(options.Target);
        if (options.Broadcast)
        {
            return Task.FromResult(peer.Broadcast(message, options.Reliability, options.Priority, options.Channel, except: target).Receipt.Value);
        }
        if (!target.IsAssigned)
        {
            return Task.FromResult(0u);
        }
        return Task.FromResult(peer.Send(message, target, options.Reliability, options.Priority, options.Channel).Receipt.Value);
    }

    public Task CancelConnectionAttemptAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        Started("cancelConnect").CancelConnectionAttempt(remote);
        return Task.CompletedTask;
    }

    public Task PingConnectedAsync(ulong target, CancellationToken cancellationToken = default)
    {
        Connection("pingConnected", target).Ping();
        return Task.CompletedTask;
    }

    public Task<bool> AdvertiseSystemAsync(IPEndPoint remote, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return Task.FromResult(Started("advertise").AdvertiseSystem(remote, data.Span));
    }

    public Task SetTimeoutAsync(TimeSpan timeout, ulong? target = null, CancellationToken cancellationToken = default)
    {
        // UNASSIGNED_SYSTEM_ADDRESS applies to every system and the default (RakPeer.cpp:2526).
        if (target is { } guid)
        {
            Connection("setTimeout", guid).Timeout = timeout;
        }
        else
        {
            Started("setTimeout").DefaultTimeout = timeout;
        }
        return Task.CompletedTask;
    }

    public Task SetMaximumIncomingConnectionsAsync(int count, CancellationToken cancellationToken = default)
    {
        Started("setMaxIncoming").MaxIncomingConnections = count;
        return Task.CompletedTask;
    }

    public Task SetOccasionalPingAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        Started("setOccasionalPing").OccasionalPing = enabled;
        return Task.CompletedTask;
    }

    public Task SetSplitMessageProgressIntervalAsync(int interval, CancellationToken cancellationToken = default)
    {
        Started("setSplitMessageProgressInterval").SplitMessageProgressInterval = interval;
        return Task.CompletedTask;
    }

    public Task SetOutgoingBandwidthLimitAsync(uint bitsPerSecond, CancellationToken cancellationToken = default)
    {
        Started("setOutgoingBandwidthLimit").PerConnectionOutgoingBandwidthLimit = bitsPerSecond;
        return Task.CompletedTask;
    }

    public Task BanAsync(string ip, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Started("ban").BanList.Add(ip, duration);
        return Task.CompletedTask;
    }

    public Task<PeerStatistics> GetStatisticsAsync(ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToPeerStatistics(Connection("stats", target).GetStatistics()));

    public Task<PeerInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("info");
        // GetConnectionList, GetMTUSize, GetAveragePing, GetLastPing, GetLowestPing and GetExternalID, as interop-peer
        // reports them: -1 for no average, 65535 for no ping yet (RakPeer.cpp:2149-2216).
        var connections = peer.Connections.Select(c => new ConnectionInfo(
            c.RakNetGuid.Value,
            c.Address.ToIPEndPoint(),
            c.Mtu,
            c.AveragePing is { } average ? (int)average.TotalMilliseconds : -1,
            c.LastPing is { } last ? (int)last.TotalMilliseconds : ushort.MaxValue,
            c.LowestPing is { } lowest ? (int)lowest.TotalMilliseconds : ushort.MaxValue,
            c.ExternalAddress.ToIPEndPoint())).ToList();
        return Task.FromResult(new PeerInfo(peer.RakNetGuid.Value, peer.Sockets[0].LocalAddress.ToIPEndPoint(), connections));
    }

    public Task<ulong> GetTimeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Started("time").GetTime());

    public Task<string> GetConnectionStateAsync(ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToProtocolName(Started("connectionState").GetConnectionState(new RakNetGuid(target))));

    public async Task ShutdownAsync(int blockMs = 100, CancellationToken cancellationToken = default)
    {
        if (_peer is { } peer)
        {
            await peer.ShutdownAsync(TimeSpan.FromMilliseconds(blockMs), cancellationToken: cancellationToken).ConfigureAwait(false);
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
    /// The packet interop-peer would report: its ID (the one after the timestamp for ID_TIMESTAMP), C++ name, sender and
    /// data (tools/interop-peer/src/PacketEvent.cpp).
    /// </summary>
    internal static ReceivedPacket ToReceivedPacket(ReadOnlySpan<byte> data, RakNetGuid sender, SystemAddress address, long timeMs)
    {
        const int timeSize = sizeof(ulong);
        var timestamped = data[0] == (byte)MessageId.Timestamp && data.Length >= 1 + timeSize + 1;
        var id = timestamped ? data[1 + timeSize] : data[0];
        ulong? timestamp = timestamped ? BinaryPrimitives.ReadUInt64BigEndian(data[1..]) : null;
        return new ReceivedPacket(id, MessageNames.Of(id), sender.Value, address.ToIPEndPoint(), data.ToArray(), timeMs, timestamp);
    }

    // RakNetTypes.h:58-66, as interop-peer names them.
    internal static string ToProtocolName(ConnectAttemptResult result) => result switch
    {
        ConnectAttemptResult.Started => "CONNECTION_ATTEMPT_STARTED",
        ConnectAttemptResult.CannotResolveDomainName => "CANNOT_RESOLVE_DOMAIN_NAME",
        ConnectAttemptResult.AlreadyConnectedToEndpoint => "ALREADY_CONNECTED_TO_ENDPOINT",
        ConnectAttemptResult.ConnectionAttemptAlreadyInProgress => "CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS",
        ConnectAttemptResult.SecurityInitializationFailed => "SECURITY_INITIALIZATION_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    // RakNetTypes.h:69-85, as interop-peer names them.
    internal static string ToProtocolName(ConnectionState state) => state switch
    {
        ConnectionState.Pending => "IS_PENDING",
        ConnectionState.Connecting => "IS_CONNECTING",
        ConnectionState.Connected => "IS_CONNECTED",
        ConnectionState.Disconnecting => "IS_DISCONNECTING",
        ConnectionState.SilentlyDisconnecting => "IS_SILENTLY_DISCONNECTING",
        ConnectionState.Disconnected => "IS_DISCONNECTED",
        ConnectionState.NotConnected => "IS_NOT_CONNECTED",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    // RakNetTypes.h:41-55, as interop-peer names them.
    internal static string ToProtocolName(StartupError error) => error switch
    {
        StartupError.SocketFamilyNotSupported => "SOCKET_FAMILY_NOT_SUPPORTED",
        StartupError.SocketPortAlreadyInUse => "SOCKET_PORT_ALREADY_IN_USE",
        StartupError.SocketFailedToBind => "SOCKET_FAILED_TO_BIND",
        StartupError.SocketFailedTestSend => "SOCKET_FAILED_TEST_SEND",
        StartupError.FailedToCreateNetworkThread => "FAILED_TO_CREATE_NETWORK_THREAD",
        StartupError.OtherFailure => "STARTUP_OTHER_FAILURE",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };

    /// <summary>The fields of the interop <c>stats</c> reply (tools/interop-peer/src/main.cpp, Stats).</summary>
    internal static PeerStatistics ToPeerStatistics(in RakNetStatistics statistics)
    {
        var runningTotal = new Dictionary<PerSecondMetric, ulong>();
        var valueOverLastSecond = new Dictionary<PerSecondMetric, ulong>();
        foreach (var metric in Enum.GetValues<RnsPerSecondMetric>())
        {
            runningTotal[(PerSecondMetric)metric] = statistics.RunningTotal[metric];
            valueOverLastSecond[(PerSecondMetric)metric] = statistics.ValueOverLastSecond[metric];
        }
        return new PeerStatistics(
            (int)statistics.MessagesInResendBuffer,
            statistics.BytesInResendBuffer,
            statistics.PacketLossLastSecond,
            statistics.PacketLossTotal,
            statistics.IsLimitedByCongestionControl,
            statistics.BpsLimitByCongestionControl,
            statistics.IsLimitedByOutgoingBandwidthLimit,
            statistics.BpsLimitByOutgoingBandwidthLimit,
            runningTotal,
            valueOverLastSecond);
    }

    private RakPeer Started(string command) =>
        _peer ?? throw new PeerCommandException(command, "the peer has not been started");

    // interop-peer's RequireConnectedAddress: the command fails for a GUID with no connection.
    private RakConnection Connection(string command, ulong target) =>
        Started(command).TryGetConnection(new RakNetGuid(target), out var connection)
            ? connection
            : throw new PeerCommandException(command, $"no connection with guid {target}");

    private static System.Buffers.ReadOnlySequence<byte> Sequence(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        var first = new Segment(parts[0], 0);
        var last = first;
        foreach (var part in parts.Skip(1))
        {
            last = last.Append(part);
        }
        return new System.Buffers.ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : System.Buffers.ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }

    // Reads the stream as a Receive() loop does, dropping the entries RakNet never produces (decision 11.4).
    private async Task PumpAsync(RakPeer peer)
    {
        try
        {
            while (await peer.Events.WaitToReadAsync().ConfigureAwait(false))
            {
                Drain(peer);
            }
            await peer.Events.Completion.ConfigureAwait(false);
            _packets.Writer.TryComplete();
        }
        catch (RakTransportException exception)
        {
            // The peer's socket failed; waits on the packet stream end with the reason.
            _packets.Writer.TryComplete(exception);
        }
    }

    private void Drain(RakPeer peer)
    {
        try
        {
            while (peer.Events.TryRead(out var e))
            {
                if (e.IsRakNetPacket)
                {
                    _packets.Writer.TryWrite(ToReceivedPacket(e.Data, e.RakNetGuid, e.Address, (long)e.ReceivedTime));
                }
            }
        }
        catch (ObjectDisposedException) when (!peer.IsActive)
        {
            // Shutdown released the event being read. It discards undelivered entries, as RakNet clears its queue
            // (docs/api-design.md section 7), so the stream ends here and no data is lost.
        }
    }

    /// <summary>The system clock under an identity of its own, so this driver's peer has its own RakNet clock origin.</summary>
    private sealed class ProcessClock : TimeProvider;
}
