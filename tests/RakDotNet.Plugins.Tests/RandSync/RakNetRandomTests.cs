namespace RakDotNet.Plugins.Tests.RandSync;

// Expected values are printed by native RakNet: tools/interop-peer's rakNetRandom command, which runs RakNetRandom
// (Rand.cpp) after SeedMT.
public sealed class RakNetRandomTests
{
    [Fact]
    public void Seed_zero_gives_native_RakNets_sequence()
    {
        var random = new RakNetRandom();
        random.Seed(0);

        Assert.Equal(
            [3796174982u, 4182529786, 2180050607, 3809380472, 2960793660, 4051247694, 3989107140, 2094500863, 3349580919, 3767281029, 2434534585, 2858763828],
            Draw(random, 12));
    }

    [Fact]
    public void An_even_seed_is_made_odd()
    {
        // Rand.cpp:139: x = seed | 1.
        var even = new RakNetRandom();
        even.Seed(12344);
        var odd = new RakNetRandom();
        odd.Seed(12345);

        Assert.Equal([2505241820u, 4267588480, 2524958330], Draw(even, 3));
        Assert.Equal([2505241820u, 4267588480, 2524958330], Draw(odd, 3));
    }

    [Fact]
    public void The_sequence_continues_across_the_reload_after_624_numbers()
    {
        var random = new RakNetRandom();
        random.Seed(4357);

        var values = Draw(random, 700);

        Assert.Equal([3510405877u, 4290933890, 2191955339], values[..3]);
        Assert.Equal([3006882189u, 281458735, 3800499491, 730882493, 2222118351, 4107301035], values[620..626]);
        Assert.Equal([1966689481u, 1576556523], values[698..]);
    }

    [Fact]
    public void NextSingle_divides_by_two_to_the_32()
    {
        var random = new RakNetRandom();
        random.Seed(0);
        Draw(random, 5);

        // The sixth to eighth numbers, 4051247694, 3989107140 and 2094500863, over 2^32 as floats.
        Assert.Equal([1064401186u, 1064158450, 1056550680], Enumerable.Range(0, 3).Select(_ => BitConverter.SingleToUInt32Bits(random.NextSingle())));
    }

    [Fact]
    public void FillBuffer_writes_little_endian_numbers_and_draws_one_more_than_whole_words_need()
    {
        var random = new RakNetRandom();
        random.Seed(0);
        var buffer = new byte[8];

        random.FillBuffer(buffer);

        // 3796174982 and 4182529786 little-endian; the third number is drawn and dropped (Rand.cpp:217-230).
        Assert.Equal(Convert.FromHexString("860845e2fa564cf9"), buffer);
        Assert.Equal(3809380472u, random.NextUInt32());
    }

    [Fact]
    public void FillBuffer_copies_the_low_bytes_of_the_last_number_into_a_partial_word()
    {
        var random = new RakNetRandom();
        random.Seed(0);
        Draw(random, 8);
        var buffer = new byte[6];

        random.FillBuffer(buffer);

        Assert.Equal(Convert.FromHexString("778ca6c78525"), buffer);
        Assert.Equal(2434534585u, random.NextUInt32());
    }

    private static uint[] Draw(RakNetRandom random, int count) => [.. Enumerable.Range(0, count).Select(_ => random.NextUInt32())];
}
