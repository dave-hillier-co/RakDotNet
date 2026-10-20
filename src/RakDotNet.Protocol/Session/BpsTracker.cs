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
/// <remarks>
/// Unlike the C++, which keeps one entry per push, pushes less than <see cref="BucketUs"/> after the newest entry are
/// added to it, and leave the sum with it: at most 1 ms early, well inside the 100 ms the clearing interval already
/// allows. A second then holds at most about a thousand entries whatever the message rate, in a buffer allocated once.
/// </remarks>
internal sealed class BpsTracker
{
    /// <summary>The span of push times one entry covers.</summary>
    public const long BucketUs = 1_000;

    private const long WindowUs = 1_000_000;

    // A second of entries, plus the clearing interval and some slack, so the queue never grows in use.
    private const int InitialCapacity = 2 * (int)(WindowUs / BucketUs);

    private readonly RingQueue<(long TimeUs, ulong Value)> _entries = new(InitialCapacity);

    public ulong Total { get; private set; }

    /// <summary>GetBPS1: the sum of the entries not yet cleared.</summary>
    public ulong LastSecond { get; private set; }

    /// <summary>Push1.</summary>
    public void Push(long timeUs, ulong value)
    {
        Total += value;
        LastSecond += value;
        var last = _entries.Count - 1;
        if (last >= 0 && _entries[last] is var (newestUs, newestValue) && timeUs >= newestUs && timeUs - newestUs < BucketUs)
            _entries[last] = (newestUs, newestValue + value);
        else
            _entries.Enqueue((timeUs, value));
    }

    /// <summary>ClearExpired1: drops entries more than a second older than <paramref name="timeUs"/>.</summary>
    public void ClearExpired(long timeUs)
    {
        while (_entries.Count > 0 && _entries.Peek() is var (entryUs, entryValue) && entryUs + WindowUs < timeUs)
        {
            LastSecond -= entryValue;
            _entries.Dequeue();
        }
    }
}
