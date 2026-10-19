using System.Net;
using System.Threading.Channels;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives one RakNet peer, native or RakDotNet, so the same scenario can run in every combination.
/// Mirrors the commands in docs/interop-protocol.md.
/// </summary>
public interface IPeerDriver : IAsyncDisposable
{
    /// <summary>Short label used in test output, e.g. "native" or "rakdotnet".</summary>
    string Kind { get; }

    /// <summary>Every packet the peer receives, in order, including RakNet's internal notifications.</summary>
    ChannelReader<ReceivedPacket> Packets { get; }

    Task<PeerStarted> StartupAsync(StartupOptions options, CancellationToken cancellationToken = default);

    Task<string> ConnectAsync(ConnectOptions options, CancellationToken cancellationToken = default);

    Task<uint> SendAsync(SendOptions options, CancellationToken cancellationToken = default);

    Task CloseAsync(ulong target, bool notify = true, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(IPEndPoint remote, bool onlyIfAccepting = false, CancellationToken cancellationToken = default);

    Task SetOfflinePingResponseAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::SendList: the parts are sent concatenated as one message.</summary>
    Task<uint> SendListAsync(SendListOptions options, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::CancelConnectionAttempt: drops a pending attempt silently.</summary>
    Task CancelConnectionAttemptAsync(IPEndPoint remote, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::Ping(SystemAddress): a connected ping, which updates the ping statistics.</summary>
    Task PingConnectedAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::AdvertiseSystem: sends ID_ADVERTISE_SYSTEM and the data to an unconnected system.</summary>
    Task<bool> AdvertiseSystemAsync(IPEndPoint remote, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::SetTimeoutTime for one connection, or for every connection and the default when target is null.</summary>
    Task SetTimeoutAsync(TimeSpan timeout, ulong? target = null, CancellationToken cancellationToken = default);

    Task SetMaximumIncomingConnectionsAsync(int count, CancellationToken cancellationToken = default);

    Task SetOccasionalPingAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::SetSplitMessageProgressInterval: report ID_DOWNLOAD_PROGRESS every interval parts; 0 never.</summary>
    Task SetSplitMessageProgressIntervalAsync(int interval, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::SetPerConnectionOutgoingBandwidthLimit; 0 is unlimited.</summary>
    Task SetOutgoingBandwidthLimitAsync(uint bitsPerSecond, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::AddToBanList; a zero duration bans forever.</summary>
    Task BanAsync(string ip, TimeSpan duration, CancellationToken cancellationToken = default);

    Task<string> GetConnectionStateAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>RakPeer::GetStatistics for one connection.</summary>
    Task<PeerStatistics> GetStatisticsAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>The peer's GUID and bound address, and every connection with its MTU, pings and our external address.</summary>
    Task<PeerInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>RakNet::GetTime in the peer: the clock ID_TIMESTAMP values are written in.</summary>
    Task<ulong> GetTimeAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(int blockMs = 100, CancellationToken cancellationToken = default);
}

public sealed record StartupOptions(int Port = 0, string Host = "127.0.0.1", int MaxConnections = 8, int MaxIncoming = 8);

public sealed record PeerStarted(IPEndPoint BoundAddress, ulong PeerGuid);

public sealed record ConnectOptions(
    IPEndPoint Remote,
    ReadOnlyMemory<byte> Password = default,
    int Attempts = 12,
    int AttemptIntervalMs = 500,
    int TimeoutMs = 0
);

public sealed record SendOptions(
    ulong Target,
    ReadOnlyMemory<byte> Data,
    Reliability Reliability = Reliability.ReliableOrdered,
    Priority Priority = Priority.High,
    byte Channel = 0,
    bool Broadcast = false
);

/// <param name="Data">The whole packet, including the message ID byte.</param>
/// <param name="Timestamp">Present when the packet starts with ID_TIMESTAMP.</param>
public sealed record ReceivedPacket(
    byte MessageId,
    string MessageName,
    ulong PeerGuid,
    IPEndPoint Address,
    ReadOnlyMemory<byte> Data,
    long TimeMs,
    ulong? Timestamp
);

public sealed record SendListOptions(
    ulong Target,
    IReadOnlyList<ReadOnlyMemory<byte>> Parts,
    Reliability Reliability = Reliability.ReliableOrdered,
    Priority Priority = Priority.High,
    byte Channel = 0,
    bool Broadcast = false
);

/// <param name="ExternalAddress">Our address as this connection sees it (RakPeer::GetExternalID).</param>
public sealed record ConnectionInfo(ulong PeerGuid, IPEndPoint Address, int Mtu, int AveragePing, int LastPing, int LowestPing, IPEndPoint ExternalAddress);

public sealed record PeerInfo(ulong PeerGuid, IPEndPoint BoundAddress, IReadOnlyList<ConnectionInfo> Connections);

/// <summary>RNSPerSecondMetrics (RakNetStatistics.h), named in the protocol by their C++ names.</summary>
public enum PerSecondMetric
{
    UserMessageBytesPushed,
    UserMessageBytesSent,
    UserMessageBytesResent,
    UserMessageBytesReceivedProcessed,
    UserMessageBytesReceivedIgnored,
    ActualBytesSent,
    ActualBytesReceived,
}

/// <summary>The RakNetStatistics fields the interop protocol reports.</summary>
public sealed record PeerStatistics(
    int MessagesInResendBuffer,
    ulong BytesInResendBuffer,
    double PacketLossLastSecond,
    double PacketLossTotal,
    bool IsLimitedByCongestionControl,
    ulong BpsLimitByCongestionControl,
    bool IsLimitedByOutgoingBandwidthLimit,
    ulong BpsLimitByOutgoingBandwidthLimit,
    IReadOnlyDictionary<PerSecondMetric, ulong> RunningTotal,
    IReadOnlyDictionary<PerSecondMetric, ulong> ValueOverLastSecond
);
