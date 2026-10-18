using System.Net;
using System.Threading.Channels;

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

    Task<string> GetConnectionStateAsync(ulong target, CancellationToken cancellationToken = default);

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

/// <summary>RakNet PacketReliability, in C++ order.</summary>
public enum Reliability
{
    Unreliable,
    UnreliableSequenced,
    Reliable,
    ReliableOrdered,
    ReliableSequenced,
    UnreliableWithAckReceipt,
    ReliableWithAckReceipt,
    ReliableOrderedWithAckReceipt,
}

/// <summary>RakNet PacketPriority, in C++ order.</summary>
public enum Priority
{
    Immediate,
    High,
    Medium,
    Low,
}
