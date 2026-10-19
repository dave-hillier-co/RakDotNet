using System.Diagnostics.CodeAnalysis;

namespace RakDotNet;

/// <summary>GUIDs and other non-security randomness. Key generation, cookies and handshakes never use it.</summary>
public abstract class RandomSource
{
    /// <summary>Thread-safe and unseeded.</summary>
    public static RandomSource Shared { get; } = new SystemRandomSource(Random.Shared);

    /// <summary>A deterministic source for tests. Not thread-safe.</summary>
    public static RandomSource FromSeed(int seed) => new SystemRandomSource(new Random(seed));

    /// <summary>Get64BitUniqueRandomNumber.</summary>
    public abstract ulong NextUInt64();

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "RandomSource is for GUIDs and other non-security randomness only.")]
    private sealed class SystemRandomSource(Random random) : RandomSource
    {
        public override ulong NextUInt64() => (ulong)random.NextInt64(long.MinValue, long.MaxValue);
    }
}
