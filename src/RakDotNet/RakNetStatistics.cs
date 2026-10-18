using System.Runtime.CompilerServices;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>RakNet's RNSPerSecondMetrics, in the same order.</summary>
public enum RnsPerSecondMetric : byte
{
    UserMessageBytesPushed,
    UserMessageBytesSent,
    UserMessageBytesResent,
    UserMessageBytesReceivedProcessed,

    /// <summary>Also counts malformed or hostile input that was dropped.</summary>
    UserMessageBytesReceivedIgnored,
    ActualBytesSent,
    ActualBytesReceived,
}

/// <summary>One value per <see cref="RnsPerSecondMetric"/>, inline.</summary>
[InlineArray(7)]
public struct MetricValues
{
    private ulong _element;

    public readonly ulong this[RnsPerSecondMetric metric] => throw new NotImplementedException();
}

/// <summary>One value per <see cref="Priority"/>, inline.</summary>
[InlineArray(4)]
public struct PriorityValues<T>
    where T : unmanaged
{
    private T _element;

    public readonly T this[Priority priority] => throw new NotImplementedException();
}

/// <summary>RakNet's RakNetStatistics, by value with no arrays, so copying a snapshot allocates nothing.</summary>
/// <remarks>
/// The I/O cycle publishes each connection's block under a sequence lock every cycle; readers copy it without blocking.
/// Peer-wide totals are also published through <c>System.Diagnostics.Metrics</c> as <c>Meter("RakDotNet")</c>.
/// </remarks>
public readonly struct RakNetStatistics
{
    public MetricValues ValueOverLastSecond => throw new NotImplementedException();

    public MetricValues RunningTotal => throw new NotImplementedException();

    /// <summary>On the peer's <see cref="TimeProvider"/>.</summary>
    public ulong ConnectionStartTimeUs => throw new NotImplementedException();

    public bool IsLimitedByCongestionControl => throw new NotImplementedException();

    public ulong BpsLimitByCongestionControl => throw new NotImplementedException();

    public bool IsLimitedByOutgoingBandwidthLimit => throw new NotImplementedException();

    public ulong BpsLimitByOutgoingBandwidthLimit => throw new NotImplementedException();

    public PriorityValues<uint> MessagesInSendBuffer => throw new NotImplementedException();

    public PriorityValues<double> BytesInSendBuffer => throw new NotImplementedException();

    public uint MessagesInResendBuffer => throw new NotImplementedException();

    public ulong BytesInResendBuffer => throw new NotImplementedException();

    public float PacketLossLastSecond => throw new NotImplementedException();

    public float PacketLossTotal => throw new NotImplementedException();

    /// <summary>RakNetStatistics::operator+=.</summary>
    public static RakNetStatistics operator +(in RakNetStatistics left, in RakNetStatistics right) => throw new NotImplementedException();

    /// <summary>StatisticsToString</summary>
    public string ToString(StatisticsVerbosity verbosity) => throw new NotImplementedException();

    /// <inheritdoc cref="ToString(StatisticsVerbosity)"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, StatisticsVerbosity verbosity) =>
        throw new NotImplementedException();
}

/// <summary>StatisticsToString's verbosityLevel 0 to 3.</summary>
public enum StatisticsVerbosity : byte
{
    Low,
    Medium,
    High,
    CongestionControl,
}

/// <summary>One entry of <see cref="RakPeer.CopyStatistics"/>.</summary>
public readonly record struct ConnectionStatistics(RakConnection Connection, RakNetStatistics Statistics);

/// <summary>
/// <see cref="RakPeer.Pools"/>. A <see cref="Misses"/> count that grows in steady state means an owned event was never
/// disposed or a pool cap is too small.
/// </summary>
public readonly record struct PoolStatistics(long SegmentsOutstanding, long OwnedEventsOutstanding, long Misses);
