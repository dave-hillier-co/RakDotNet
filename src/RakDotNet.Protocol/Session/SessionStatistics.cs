namespace RakDotNet.Protocol.Session;

/// <summary>
/// One connection's RakNetStatistics, as ReliabilityLayer::GetStatistics fills it (ReliabilityLayer.cpp:3378-3415).
/// Arrays are indexed by <see cref="SessionMetric"/> and by priority.
/// </summary>
internal sealed record SessionStatistics
{
    public required ulong[] ValueOverLastSecond { get; init; }

    public required ulong[] RunningTotal { get; init; }

    public long ConnectionStartTimeUs { get; init; }

    public bool IsLimitedByCongestionControl { get; init; }

    /// <summary>GetBytesPerSecondLimitByCongestionControl, always 0 for the sliding window (CCRakNetSlidingWindow.cpp:355-358).</summary>
    public ulong BpsLimitByCongestionControl { get; init; }

    public bool IsLimitedByOutgoingBandwidthLimit { get; init; }

    public ulong BpsLimitByOutgoingBandwidthLimit { get; init; }

    public required uint[] MessagesInSendBuffer { get; init; }

    public required double[] BytesInSendBuffer { get; init; }

    public uint MessagesInResendBuffer { get; init; }

    public ulong BytesInResendBuffer { get; init; }

    public float PacketLossLastSecond { get; init; }

    public float PacketLossTotal { get; init; }
}
