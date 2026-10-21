namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives a RakNetRandomSync and a RakNetRandom on a peer: the randSync* and rakNetRandom commands of
/// docs/interop-protocol.md. Both drivers implement it; neither needs an attach.
/// </summary>
public interface IRandSyncDriver
{
    /// <summary>
    /// A fresh RakNetRandom seeded with <paramref name="seed"/>: <paramref name="count"/> RandomMT values, then
    /// <paramref name="floatCount"/> FrandomMT values as their bits, then, with <paramref name="fillBytes"/>, FillBufferMT
    /// of that many bytes and the RandomMT value after it.
    /// </summary>
    Task<RakNetRandomOutput> DrawRakNetRandomAsync(uint seed, int count, int floatCount = 0, int? fillBytes = null, CancellationToken cancellationToken = default);

    /// <summary>SeedMT, with the skip count when given.</summary>
    Task SeedRandSyncAsync(uint seed, uint? skip = null, CancellationToken cancellationToken = default);

    /// <summary>RandomMT <paramref name="count"/> times, or FrandomMT as its bits.</summary>
    Task<IReadOnlyList<uint>> NextRandSyncAsync(int count, bool asFloat = false, CancellationToken cancellationToken = default);

    /// <summary>SerializeConstruction</summary>
    Task<byte[]> SerializeRandSyncConstructionAsync(CancellationToken cancellationToken = default);

    /// <summary>DeserializeConstruction</summary>
    Task<bool> DeserializeRandSyncConstructionAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>Serialize</summary>
    Task<byte[]> SerializeRandSyncAsync(CancellationToken cancellationToken = default);

    /// <summary>Deserialize; the data holds at least a call count.</summary>
    Task DeserializeRandSyncAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>GetSeed and GetCallCount.</summary>
    Task<(uint Seed, uint CallCount)> GetRandSyncInfoAsync(CancellationToken cancellationToken = default);
}

public sealed record RakNetRandomOutput(IReadOnlyList<uint> Values, IReadOnlyList<uint> FloatBits, byte[]? Fill, uint? AfterFill);
