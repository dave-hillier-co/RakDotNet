namespace RakDotNet.Interop.Tests.Harness;

/// <summary>Which way a datagram crosses the proxy: a is the client side, b is the server.</summary>
public enum ProxyDirection
{
    AToB,
    BToA,
}

/// <summary>What the proxy does to one datagram.</summary>
public readonly record struct FaultDecision(bool Drop, bool Duplicate, TimeSpan Delay);

/// <summary>
/// Seeded fault injection. Each decision is a pure function of (seed, direction, index of the datagram in that
/// direction), so the same seed gives the same faults for the same datagram sequence regardless of how the
/// proxy's receive loops interleave. Reordering comes from per-datagram delays of <see cref="Delay"/> plus up
/// to <see cref="Jitter"/>.
/// </summary>
public sealed record FaultOptions(
    int Seed = 0,
    double LossProbability = 0,
    double DuplicationProbability = 0,
    TimeSpan Delay = default,
    TimeSpan Jitter = default)
{
    public static FaultOptions None { get; } = new();

    public FaultDecision Decide(ProxyDirection direction, long index)
    {
        var state = Mix(Mix(Mix((ulong)(uint)Seed) ^ (ulong)direction) ^ (ulong)index);
        var drop = NextUnit(ref state) < LossProbability;
        var duplicate = NextUnit(ref state) < DuplicationProbability;
        var jitter = Jitter > TimeSpan.Zero ? TimeSpan.FromTicks((long)(NextUnit(ref state) * Jitter.Ticks)) : TimeSpan.Zero;
        return new FaultDecision(drop, duplicate, Delay + jitter);
    }

    // SplitMix64 (Steele, Lea, Flood 2014): cheap, stateless, well distributed.
    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static double NextUnit(ref ulong state)
    {
        state = Mix(state);
        return (state >> 11) * (1.0 / (1UL << 53));
    }
}
