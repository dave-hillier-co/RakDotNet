namespace RakDotNet.Protocol.Session;

/// <summary>
/// One connection's RakNetStatistics, as ReliabilityLayer::GetStatistics fills it (ReliabilityLayer.cpp:3378-3415).
/// Arrays are indexed by <see cref="SessionMetric"/> and by priority. A snapshot may be reused:
/// <see cref="ReliabilityLayer.CopyStatistics"/> overwrites every field in place.
/// </summary>
internal sealed class SessionStatistics
{
    public ulong[] ValueOverLastSecond { get; } = new ulong[7];

    public ulong[] RunningTotal { get; } = new ulong[7];

    public long ConnectionStartTimeUs { get; set; }

    public bool IsLimitedByCongestionControl { get; set; }

    /// <summary>GetBytesPerSecondLimitByCongestionControl, always 0 for the sliding window (CCRakNetSlidingWindow.cpp:355-358).</summary>
    public ulong BpsLimitByCongestionControl { get; set; }

    public bool IsLimitedByOutgoingBandwidthLimit { get; set; }

    public ulong BpsLimitByOutgoingBandwidthLimit { get; set; }

    public uint[] MessagesInSendBuffer { get; } = new uint[4];

    public double[] BytesInSendBuffer { get; } = new double[4];

    public uint MessagesInResendBuffer { get; set; }

    public ulong BytesInResendBuffer { get; set; }

    public float PacketLossLastSecond { get; set; }

    public float PacketLossTotal { get; set; }
}
