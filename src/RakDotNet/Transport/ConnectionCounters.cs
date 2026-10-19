using RakDotNet.Protocol.Session;

namespace RakDotNet.Transport;

/// <summary>
/// The statistics of one connection as its reliability layer last reported them (ReliabilityLayer::GetStatistics,
/// ReliabilityLayer.cpp:3378-3415). The I/O cycle copies them in each cycle without allocating; readers take the value
/// snapshot under a lock.
/// </summary>
internal sealed class ConnectionCounters(long startUs)
{
    private readonly Lock _gate = new();
    private readonly SessionStatistics _scratch = new();
    private RakNetStatistics _published = RakNetStatistics.Empty((ulong)startUs);

    /// <summary>Copies what <paramref name="session"/> reports. Called by the I/O cycle.</summary>
    public void Publish(ReliabilityLayer session)
    {
        session.CopyStatistics(_scratch);
        var statistics = new RakNetStatistics(_scratch);
        lock (_gate)
            _published = statistics;
    }

    public RakNetStatistics Snapshot(long nowUs)
    {
        lock (_gate)
            return _published.At((ulong)nowUs);
    }
}
