using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.RandSync;

// The sequence for seed 0 is printed by native RakNet (tools/interop-peer's rakNetRandom command); the replay values by
// its randSync* commands, which drive a RakNetRandomSync through the same calls.
public sealed class RakNetRandomSyncTests
{
    private static readonly uint[] SeedZero =
        [3796174982u, 4182529786, 2180050607, 3809380472, 2960793660, 4051247694, 3989107140, 2094500863, 3349580919, 3767281029, 2434534585, 2858763828];

    [Fact]
    public void Construction_is_the_seed_then_the_call_count_big_endian()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(0x01020304);
        Draw(sync, 5);
        using var stream = new BitStream();

        sync.SerializeConstruction(stream);

        Assert.Equal(Convert.FromHexString("0102030400000005"), stream.Written.ToArray());
    }

    [Fact]
    public void A_system_constructed_from_another_draws_what_the_other_draws_next()
    {
        // The commented-out test at the end of RandSync.cpp.
        var r1 = new RakNetRandomSync();
        r1.Seed(0);
        Draw(r1, 3);
        var r2 = new RakNetRandomSync();
        using var stream = new BitStream();
        r1.SerializeConstruction(stream);

        var reader = stream.CreateReader();
        Assert.True(r2.DeserializeConstruction(ref reader));

        Assert.Equal(0u, r2.CurrentSeed);
        Assert.Equal(3u, r2.CallCount);
        Assert.Equal(SeedZero[3..6], Draw(r1, 3));
        Assert.Equal(SeedZero[3..6], Draw(r2, 3));
    }

    [Fact]
    public void A_construction_without_a_call_count_is_refused_and_changes_nothing()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(7);
        var reader = new BitStreamReader(Convert.FromHexString("000000010000"));

        Assert.False(sync.DeserializeConstruction(ref reader));
        Assert.Equal(7u, sync.CurrentSeed);
    }

    [Fact]
    public void A_system_ahead_of_the_sender_replays_the_numbers_it_drew_too_many()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(0);
        Draw(sync, 7);

        Deserialize(sync, 4);

        // RandSync.cpp:65-70: the fifth to seventh again, then a new eighth; replays are not counted.
        Assert.Equal([SeedZero[4], SeedZero[5], SeedZero[6], SeedZero[7]], Draw(sync, 4));
        Assert.Equal(8u, sync.CallCount);
    }

    [Fact]
    public void A_system_behind_the_sender_skips_the_numbers_the_sender_drew()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(0);
        Draw(sync, 8);

        Deserialize(sync, 10);

        Assert.Equal(10u, sync.CallCount);
        Assert.Equal(SeedZero[10..12], Draw(sync, 2));
        Assert.Equal(12u, sync.CallCount);
    }

    [Fact]
    public void A_sender_further_ahead_than_the_replay_skips_even_while_replaying()
    {
        // RandSync.cpp:132-140: the replay count drops only when the difference fits, and the difference is skipped anyway.
        var sync = new RakNetRandomSync();
        sync.Seed(0);
        Draw(sync, 6);
        Deserialize(sync, 4);

        Deserialize(sync, 7);

        Assert.Equal(7u, sync.CallCount);
        // One number is left to replay, the sixth, then the generator continues past the skipped seventh.
        Assert.Equal([SeedZero[5], SeedZero[7], SeedZero[8]], Draw(sync, 3));
    }

    [Fact]
    public void Only_the_last_64_numbers_are_kept_for_replay()
    {
        var reference = new RakNetRandom();
        reference.Seed(0);
        var expected = Enumerable.Range(0, 200).Select(_ => reference.NextUInt32()).ToArray();
        var sync = new RakNetRandomSync();
        sync.Seed(0);
        Draw(sync, 100);

        Deserialize(sync, 30);

        // 70 to replay but only 64 kept: the first six replays are fresh numbers from the generator, uncounted.
        Assert.Equal(expected[100..106], Draw(sync, 6));
        Assert.Equal(expected[36..100], Draw(sync, 64));
        Assert.Equal(100u, sync.CallCount);
        Assert.Equal(expected[106], sync.NextUInt32());
    }

    [Fact]
    public void An_update_further_ahead_than_the_skip_limit_is_refused_and_changes_nothing()
    {
        // RakNet draws every skipped number on the caller's thread: 2^32 of them take seconds.
        var sync = new RakNetRandomSync();
        sync.Seed(0);
        Draw(sync, 2);
        using var stream = new BitStream();
        stream.WriteUInt32(uint.MaxValue);
        var reader = stream.CreateReader();

        Assert.False(sync.Deserialize(ref reader));

        Assert.Equal(2u, sync.CallCount);
        Assert.Equal(SeedZero[2..4], Draw(sync, 2));
    }

    [Fact]
    public void A_construction_further_ahead_than_the_skip_limit_is_refused_and_changes_nothing()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(7);
        var reader = new BitStreamReader(Convert.FromHexString("00000000ffffffff"));

        Assert.False(sync.DeserializeConstruction(ref reader));

        Assert.Equal((7u, 0u), (sync.CurrentSeed, sync.CallCount));
    }

    [Fact]
    public void The_skip_limit_allows_skips_up_to_it()
    {
        var sync = new RakNetRandomSync { MaxSkip = 10 };
        sync.Seed(0);
        Draw(sync, 1);
        using var stream = new BitStream();
        stream.WriteUInt32(11);
        stream.WriteUInt32(12);
        var reader = stream.CreateReader();

        Assert.True(sync.Deserialize(ref reader));
        Assert.Equal(11u, sync.CallCount);
        Assert.True(sync.Deserialize(ref reader));
        Assert.Equal(12u, sync.CallCount);
        var construction = new BitStreamReader(Convert.FromHexString("000000000000000b"));
        Assert.False(new RakNetRandomSync { MaxSkip = 10 }.DeserializeConstruction(ref construction));
    }

    [Fact]
    public void Serialize_writes_the_call_count()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(0, skipValues: 0x0102);
        using var stream = new BitStream();

        sync.Serialize(stream);

        Assert.Equal(Convert.FromHexString("00000102"), stream.Written.ToArray());
    }

    [Fact]
    public void NextSingle_divides_by_two_to_the_32_minus_one()
    {
        var sync = new RakNetRandomSync();
        sync.Seed(0);

        // Printed by the native randSyncNext command with float: true.
        Assert.Equal([1063404809u, 1064914007, 1057091823, 1063456392], Enumerable.Range(0, 4).Select(_ => BitConverter.SingleToUInt32Bits(sync.NextSingle())));
    }

    [Fact]
    public void Before_the_first_seed_the_seed_reads_as_all_ones()
    {
        Assert.Equal(uint.MaxValue, new RakNetRandomSync().CurrentSeed);
    }

    private static void Deserialize(RakNetRandomSync sync, uint remoteCallCount)
    {
        using var stream = new BitStream();
        stream.WriteUInt32(remoteCallCount);
        var reader = stream.CreateReader();
        sync.Deserialize(ref reader);
    }

    private static uint[] Draw(RakNetRandomSync sync, int count) => [.. Enumerable.Range(0, count).Select(_ => sync.NextUInt32())];
}
