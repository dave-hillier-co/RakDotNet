using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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

    public readonly ulong this[RnsPerSecondMetric metric] => ((ReadOnlySpan<ulong>)this)[(int)metric];

    internal static MetricValues From(ReadOnlySpan<ulong> values)
    {
        var result = default(MetricValues);
        values.CopyTo(result);
        return result;
    }
}

/// <summary>One value per <see cref="Priority"/>, inline.</summary>
[InlineArray(4)]
public struct PriorityValues<T>
    where T : unmanaged
{
    private T _element;

    public readonly T this[Priority priority] => ((ReadOnlySpan<T>)this)[(int)priority];
}

/// <summary>RakNet's RakNetStatistics, by value with no arrays, so copying a snapshot allocates nothing.</summary>
/// <remarks>
/// The I/O cycle keeps each connection's counters and readers copy them without waiting for a cycle. The field set
/// covers the interop <c>stats</c> command, so the interop driver maps it field for field. Values the reliability
/// layer does not measure are 0, as RakNet reports before it has measured them.
/// </remarks>
public readonly struct RakNetStatistics
{
    private readonly MetricValues _valueOverLastSecond;
    private readonly MetricValues _runningTotal;
    private readonly ulong _snapshotTimeUs;

    internal RakNetStatistics(ReadOnlySpan<ulong> valueOverLastSecond, ReadOnlySpan<ulong> runningTotal, ulong connectionStartTimeUs,
                              uint messagesInResendBuffer, ulong snapshotTimeUs)
    {
        _valueOverLastSecond = MetricValues.From(valueOverLastSecond);
        _runningTotal = MetricValues.From(runningTotal);
        ConnectionStartTimeUs = connectionStartTimeUs;
        MessagesInResendBuffer = messagesInResendBuffer;
        _snapshotTimeUs = snapshotTimeUs;
    }

    private RakNetStatistics(in RakNetStatistics left, in RakNetStatistics right)
    {
        Span<ulong> last = stackalloc ulong[7];
        Span<ulong> total = stackalloc ulong[7];
        for (var i = 0; i < 7; i++)
        {
            last[i] = left._valueOverLastSecond[(RnsPerSecondMetric)i] + right._valueOverLastSecond[(RnsPerSecondMetric)i];
            total[i] = left._runningTotal[(RnsPerSecondMetric)i] + right._runningTotal[(RnsPerSecondMetric)i];
        }
        _valueOverLastSecond = MetricValues.From(last);
        _runningTotal = MetricValues.From(total);
        ConnectionStartTimeUs = Math.Min(left.ConnectionStartTimeUs, right.ConnectionStartTimeUs);
        MessagesInResendBuffer = left.MessagesInResendBuffer + right.MessagesInResendBuffer;
        BytesInResendBuffer = left.BytesInResendBuffer + right.BytesInResendBuffer;
        _snapshotTimeUs = Math.Max(left._snapshotTimeUs, right._snapshotTimeUs);
    }

    public MetricValues ValueOverLastSecond => _valueOverLastSecond;

    public MetricValues RunningTotal => _runningTotal;

    /// <summary>On the peer's <see cref="TimeProvider"/>.</summary>
    public ulong ConnectionStartTimeUs { get; }

    public bool IsLimitedByCongestionControl { get; }

    public ulong BpsLimitByCongestionControl { get; }

    public bool IsLimitedByOutgoingBandwidthLimit { get; }

    public ulong BpsLimitByOutgoingBandwidthLimit { get; }

    public PriorityValues<uint> MessagesInSendBuffer { get; }

    public PriorityValues<double> BytesInSendBuffer { get; }

    public uint MessagesInResendBuffer { get; }

    public ulong BytesInResendBuffer { get; }

    public float PacketLossLastSecond { get; }

    public float PacketLossTotal { get; }

    /// <summary>RakNetStatistics::operator+=.</summary>
    public static RakNetStatistics operator +(in RakNetStatistics left, in RakNetStatistics right) => new(left, right);

    /// <summary>StatisticsToString (RakNetStatistics.cpp:24-170).</summary>
    public string ToString(StatisticsVerbosity verbosity)
    {
        var last = _valueOverLastSecond;
        var total = _runningTotal;
        var elapsedSeconds = (_snapshotTimeUs - Math.Min(_snapshotTimeUs, ConnectionStartTimeUs)) / 1_000_000;
        var invariant = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        switch (verbosity)
        {
            case StatisticsVerbosity.Low:
                text.Append(invariant, $"Bytes per second sent     {last[RnsPerSecondMetric.ActualBytesSent]}\n");
                text.Append(invariant, $"Bytes per second received {last[RnsPerSecondMetric.ActualBytesReceived]}\n");
                text.Append(invariant, $"Current packetloss        {PacketLossLastSecond * 100.0f:0.0}%\n");
                return text.ToString();
            case StatisticsVerbosity.Medium:
                text.Append(invariant, $"Actual bytes per second sent       {last[RnsPerSecondMetric.ActualBytesSent]}\n");
                text.Append(invariant, $"Actual bytes per second received   {last[RnsPerSecondMetric.ActualBytesReceived]}\n");
                text.Append(invariant, $"Message bytes per second pushed    {last[RnsPerSecondMetric.UserMessageBytesPushed]}\n");
                text.Append(invariant, $"Total actual bytes sent            {total[RnsPerSecondMetric.ActualBytesSent]}\n");
                text.Append(invariant, $"Total actual bytes received        {total[RnsPerSecondMetric.ActualBytesReceived]}\n");
                text.Append(invariant, $"Total message bytes pushed         {total[RnsPerSecondMetric.UserMessageBytesPushed]}\n");
                text.Append(invariant, $"Current packetloss                 {PacketLossLastSecond * 100.0f:0.0}%\n");
                text.Append(invariant, $"Average packetloss                 {PacketLossTotal * 100.0f:0.0}%\n");
                text.Append(invariant, $"Elapsed connection time in seconds {elapsedSeconds}\n");
                break;
            default:
                text.Append(invariant, $"Actual bytes per second sent         {last[RnsPerSecondMetric.ActualBytesSent]}\n");
                text.Append(invariant, $"Actual bytes per second received     {last[RnsPerSecondMetric.ActualBytesReceived]}\n");
                text.Append(invariant, $"Message bytes per second sent        {last[RnsPerSecondMetric.UserMessageBytesSent]}\n");
                text.Append(invariant, $"Message bytes per second resent      {last[RnsPerSecondMetric.UserMessageBytesResent]}\n");
                text.Append(invariant, $"Message bytes per second pushed      {last[RnsPerSecondMetric.UserMessageBytesPushed]}\n");
                text.Append(invariant, $"Message bytes per second returned\t  {last[RnsPerSecondMetric.UserMessageBytesReceivedProcessed]}\n");
                text.Append(invariant, $"Message bytes per second ignored     {last[RnsPerSecondMetric.UserMessageBytesReceivedIgnored]}\n");
                text.Append(invariant, $"Total bytes sent                     {total[RnsPerSecondMetric.ActualBytesSent]}\n");
                text.Append(invariant, $"Total bytes received                 {total[RnsPerSecondMetric.ActualBytesReceived]}\n");
                text.Append(invariant, $"Total message bytes sent             {total[RnsPerSecondMetric.UserMessageBytesSent]}\n");
                text.Append(invariant, $"Total message bytes resent           {total[RnsPerSecondMetric.UserMessageBytesResent]}\n");
                text.Append(invariant, $"Total message bytes pushed           {total[RnsPerSecondMetric.UserMessageBytesPushed]}\n");
                text.Append(invariant, $"Total message bytes returned\t\t  {total[RnsPerSecondMetric.UserMessageBytesReceivedProcessed]}\n");
                text.Append(invariant, $"Total message bytes ignored          {total[RnsPerSecondMetric.UserMessageBytesReceivedIgnored]}\n");
                var messages = MessagesInSendBuffer;
                var bytes = BytesInSendBuffer;
                text.Append(invariant, $"Messages in send buffer, by priority {messages[Priority.Immediate]},{messages[Priority.High]},{messages[Priority.Medium]},{messages[Priority.Low]}\n");
                text.Append(invariant, $"Bytes in send buffer, by priority    {(uint)bytes[Priority.Immediate]},{(uint)bytes[Priority.High]},{(uint)bytes[Priority.Medium]},{(uint)bytes[Priority.Low]}\n");
                text.Append(invariant, $"Messages in resend buffer            {MessagesInResendBuffer}\n");
                text.Append(invariant, $"Bytes in resend buffer               {BytesInResendBuffer}\n");
                text.Append(invariant, $"Current packetloss                   {PacketLossLastSecond * 100.0f:0.0}%\n");
                text.Append(invariant, $"Average packetloss                   {PacketLossTotal * 100.0f:0.0}%\n");
                text.Append(invariant, $"Elapsed connection time in seconds   {elapsedSeconds}\n");
                break;
        }

        if (BpsLimitByCongestionControl != 0)
            text.Append(invariant, $"Send capacity                    {BpsLimitByCongestionControl} bytes per second ({100.0f * last[RnsPerSecondMetric.ActualBytesSent] / BpsLimitByCongestionControl:0}%)\n");
        if (BpsLimitByOutgoingBandwidthLimit != 0)
            text.Append(invariant, $"Send limit                       {BpsLimitByOutgoingBandwidthLimit} ({100.0f * last[RnsPerSecondMetric.ActualBytesSent] / BpsLimitByOutgoingBandwidthLimit:0}%)\n");
        return text.ToString();
    }

    /// <inheritdoc cref="ToString(StatisticsVerbosity)"/>
    public bool TryFormat(Span<char> destination, out int charsWritten, StatisticsVerbosity verbosity)
    {
        var text = ToString(verbosity);
        if (!text.TryCopyTo(destination))
        {
            charsWritten = 0;
            return false;
        }
        charsWritten = text.Length;
        return true;
    }
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
/// disposed or a pool cap is too small. The allocation-budget test checks that it stays zero after warm-up.
/// </summary>
public readonly record struct PoolStatistics(long SegmentsOutstanding, long OwnedEventsOutstanding, long Misses);
