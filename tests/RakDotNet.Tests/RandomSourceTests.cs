namespace RakDotNet.Tests;

public class RandomSourceTests
{
    [Fact]
    public void Sources_with_the_same_seed_produce_the_same_sequence()
    {
        var a = RandomSource.FromSeed(1234);
        var b = RandomSource.FromSeed(1234);

        Assert.Equal(
            [a.NextUInt64(), a.NextUInt64(), a.NextUInt64()],
            [b.NextUInt64(), b.NextUInt64(), b.NextUInt64()]);
    }

    [Fact]
    public void Sources_with_different_seeds_differ()
    {
        Assert.NotEqual(RandomSource.FromSeed(1).NextUInt64(), RandomSource.FromSeed(2).NextUInt64());
    }

    [Fact]
    public void The_shared_source_is_one_instance()
    {
        Assert.Same(RandomSource.Shared, RandomSource.Shared);
    }
}
