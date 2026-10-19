namespace RakDotNet.Protocol.Session;

/// <summary>
/// The per-second metrics of RakNetStatistics, in RNSPerSecondMetrics order (RakNetStatistics.h:24-57).
/// </summary>
internal enum SessionMetric
{
    UserMessageBytesPushed,
    UserMessageBytesSent,
    UserMessageBytesResent,
    UserMessageBytesReceivedProcessed,
    UserMessageBytesReceivedIgnored,
    ActualBytesSent,
    ActualBytesReceived,
}

/// <summary>
/// BPSTracker (ReliabilityLayer.h:84-114, ReliabilityLayer.cpp:69-108): a running total and the sum over roughly the
/// last second. Entries leave the sum only when <see cref="ClearExpired"/> runs, which the layer does at most every
/// 100 ms (ReliabilityLayer.cpp:1844-1858), so <see cref="LastSecond"/> can include entries up to that much older.
/// </summary>
internal sealed class BpsTracker
{
    private const long WindowUs = 1_000_000;

    private readonly Queue<(long TimeUs, ulong Value)> _entries = new();

    public ulong Total { get; private set; }

    /// <summary>GetBPS1: the sum of the entries not yet cleared.</summary>
    public ulong LastSecond { get; private set; }

    /// <summary>Push1.</summary>
    public void Push(long timeUs, ulong value)
    {
        _entries.Enqueue((timeUs, value));
        Total += value;
        LastSecond += value;
    }

    /// <summary>ClearExpired1: drops entries more than a second older than <paramref name="timeUs"/>.</summary>
    public void ClearExpired(long timeUs)
    {
        while (_entries.TryPeek(out var entry) && entry.TimeUs + WindowUs < timeUs)
        {
            LastSecond -= entry.Value;
            _entries.Dequeue();
        }
    }
}
