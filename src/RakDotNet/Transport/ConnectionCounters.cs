namespace RakDotNet.Transport;

/// <summary>
/// The per-connection statistics the I/O cycle keeps: running totals of the RNSPerSecondMetrics it can see, and the
/// value over the last whole second. Written by the cycle, copied by readers under a lock.
/// </summary>
internal sealed class ConnectionCounters(long startUs)
{
    private const int MetricCount = 7;
    private static readonly long SecondUs = 1_000_000;

    private readonly Lock _gate = new();
    private readonly ulong[] _totals = new ulong[MetricCount];
    private readonly ulong[] _atSecondStart = new ulong[MetricCount];
    private readonly ulong[] _lastSecond = new ulong[MetricCount];
    private long _secondStartUs = startUs;
    private uint _messagesInResendBuffer;

    public long StartUs { get; } = startUs;

    public void Add(RnsPerSecondMetric metric, int bytes)
    {
        lock (_gate)
            _totals[(int)metric] += (ulong)bytes;
    }

    /// <summary>Closes the current second when it has passed, and records what the reliability layer holds.</summary>
    public void Tick(long nowUs, int messagesInResendBuffer)
    {
        lock (_gate)
        {
            _messagesInResendBuffer = (uint)messagesInResendBuffer;
            if (nowUs - _secondStartUs < SecondUs)
                return;
            for (var i = 0; i < MetricCount; i++)
            {
                _lastSecond[i] = _totals[i] - _atSecondStart[i];
                _atSecondStart[i] = _totals[i];
            }
            _secondStartUs = nowUs;
        }
    }

    public RakNetStatistics Snapshot(long nowUs)
    {
        lock (_gate)
            return new RakNetStatistics(_lastSecond, _totals, (ulong)StartUs, _messagesInResendBuffer, (ulong)nowUs);
    }
}
