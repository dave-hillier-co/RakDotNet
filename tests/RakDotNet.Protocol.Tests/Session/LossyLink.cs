using RakDotNet.Protocol.Session;

namespace RakDotNet.Protocol.Tests.Session;

/// <summary>
/// Two reliability layers joined by a seeded in-memory link that drops, duplicates and delays datagrams. Delays are
/// drawn per datagram, so datagrams overtake each other. Time advances in fixed ticks, as RakPeer's update thread does.
/// </summary>
internal sealed class LossyLink
{
    private readonly Random _random;
    private readonly List<InFlight> _inFlight = [];
    private long _sequence;

    public LossyLink(int seed, double lossProbability, double duplicateProbability, long maxDelayUs, int mtu = 1492, long startUs = 1_000_000)
    {
        _random = new Random(seed);
        LossProbability = lossProbability;
        DuplicateProbability = duplicateProbability;
        MaxDelayUs = maxDelayUs;
        NowUs = startUs;
        A = new ReliabilityLayer(mtu, startUs);
        B = new ReliabilityLayer(mtu, startUs);
    }

    public ReliabilityLayer A { get; }

    public ReliabilityLayer B { get; }

    public double LossProbability { get; set; }

    public double DuplicateProbability { get; set; }

    public long MaxDelayUs { get; set; }

    public long NowUs { get; private set; }

    public int Sent { get; private set; }

    public int Dropped { get; private set; }

    public int Duplicated { get; private set; }

    public List<SessionEvent> EventsAtA { get; } = [];

    public List<SessionEvent> EventsAtB { get; } = [];

    /// <summary>Runs ticks until <paramref name="done"/> holds or <paramref name="limitUs"/> of link time passes.</summary>
    public bool RunUntil(Func<bool> done, long limitUs, long tickUs = 10_000)
    {
        var end = NowUs + limitUs;
        while (NowUs < end)
        {
            Tick(tickUs);
            if (done())
                return true;
        }
        return false;
    }

    public void Tick(long tickUs = 10_000)
    {
        NowUs += tickUs;
        DeliverDue();
        A.Update(NowUs);
        B.Update(NowUs);
        Carry(A, B);
        Carry(B, A);
        Drain(A, EventsAtA);
        Drain(B, EventsAtB);
    }

    private void Carry(ReliabilityLayer from, ReliabilityLayer to)
    {
        while (from.TryDequeueDatagram(out var datagram))
        {
            Sent++;
            if (_random.NextDouble() < LossProbability)
            {
                Dropped++;
                continue;
            }
            var copies = 1;
            if (_random.NextDouble() < DuplicateProbability)
            {
                copies = 2;
                Duplicated++;
            }
            for (var i = 0; i < copies; i++)
                _inFlight.Add(new InFlight(NowUs + _random.NextInt64(MaxDelayUs + 1), _sequence++, datagram, to));
        }
    }

    private void DeliverDue()
    {
        var due = _inFlight.Where(d => d.DeliverAtUs <= NowUs).OrderBy(d => d.DeliverAtUs).ThenBy(d => d.Sequence).ToList();
        foreach (var datagram in due)
        {
            _inFlight.Remove(datagram);
            Assert.True(datagram.To.Receive(datagram.Bytes, NowUs));
        }
    }

    private static void Drain(ReliabilityLayer layer, List<SessionEvent> events)
    {
        while (layer.TryDequeueEvent(out var sessionEvent))
            events.Add(sessionEvent);
    }

    private sealed record InFlight(long DeliverAtUs, long Sequence, byte[] Bytes, ReliabilityLayer To);
}
