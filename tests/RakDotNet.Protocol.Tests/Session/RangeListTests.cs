using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// DS_RangeList.h:159-218 (Insert) and 64-110 (Serialize with clearSerialized).
public class RangeListTests
{
    private readonly RangeList _list = new();

    [Fact]
    public void Inserted_numbers_are_kept_sorted_and_joined_into_ranges()
    {
        foreach (var n in new uint[] { 5, 3, 4, 9, 1, 10 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(1, 1), Range(3, 5), Range(9, 10)], _list.Ranges);
    }

    // GetIndexFromKey compares against minIndex only (DS_RangeList.h:39-46), so Insert looks at the first range whose
    // minIndex is above the number and never at the range before it: the "Insert here" branch (DS_RangeList.h:182-188)
    // gives a number that extends or repeats the preceding range a range of its own.
    [Fact]
    public void A_number_extending_the_preceding_range_gets_its_own_range_when_a_later_range_exists()
    {
        foreach (var n in new uint[] { 1, 2, 3, 10, 4 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(1, 3), Range(4, 4), Range(10, 10)], _list.Ranges);
    }

    [Fact]
    public void A_duplicate_inside_a_range_that_is_not_the_last_gets_its_own_range()
    {
        foreach (var n in new uint[] { 1, 2, 3, 10, 2 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(1, 3), Range(2, 2), Range(10, 10)], _list.Ranges);
    }

    [Fact]
    public void A_duplicate_inside_the_last_range_is_ignored()
    {
        foreach (var n in new uint[] { 1, 2, 3, 2 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(1, 3)], _list.Ranges);
    }

    // minIndex-1 is uint24_t arithmetic (RakNetTypes.h:486), so for a range starting at 0 it is 0xFFFFFF and a repeated 0
    // takes the "Insert here" branch (DS_RangeList.h:182-188) even though GetIndexFromKey found it.
    [Fact]
    public void A_repeated_zero_at_the_start_of_a_range_gets_its_own_range()
    {
        foreach (var n in new uint[] { 0, 1, 0 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(0, 0), Range(0, 1)], _list.Ranges);
    }

    // uint24_t arithmetic masks to 24 bits (RakNetTypes.h:485), so maxIndex+1 of a range ending at 0xFFFFFF is 0 and any
    // later number lands in the "Insert at end" branch (DS_RangeList.h:174-178), even one inside that range.
    [Fact]
    public void A_number_inside_a_last_range_ending_at_the_wrap_is_appended_again()
    {
        _list.Insert(new UInt24(UInt24.MaxValue.Value - 1));
        _list.Insert(UInt24.MaxValue);
        _list.Insert(UInt24.MaxValue);

        Assert.Equal([new AckRange(new UInt24(UInt24.MaxValue.Value - 1), UInt24.MaxValue), new AckRange(UInt24.MaxValue, UInt24.MaxValue)], _list.Ranges);
    }

    [Fact]
    public void A_number_filling_the_gap_between_two_ranges_joins_them()
    {
        foreach (var n in new uint[] { 1, 2, 4, 5, 3 })
            _list.Insert(new UInt24(n));

        Assert.Equal([Range(1, 5)], _list.Ranges);
    }

    [Fact]
    public void Numbers_either_side_of_the_wrap_are_never_joined()
    {
        _list.Insert(UInt24.MaxValue);
        _list.Insert(new UInt24(0));

        Assert.Equal([Range(0, 0), new AckRange(UInt24.MaxValue, UInt24.MaxValue)], _list.Ranges);
    }

    [Fact]
    public void Writing_removes_the_ranges_written_and_keeps_the_rest()
    {
        for (uint n = 0; n < 20; n += 2)
            _list.Insert(new UInt24(n));
        var writer = new BitStreamWriter(new byte[64]);

        // 16 + bits + 65 > maxBits stops before each range; single ranges cost 40 bits (DS_RangeList.h:74-92).
        var written = _list.Write(ref writer, maxBits: 16 + 40 * 3 + 65);

        Assert.Equal(4, written);
        Assert.Equal(6, _list.Count);
        Assert.Equal(Range(8, 8), _list.Ranges[0]);
        var reader = new BitStreamReader(writer.Written);
        Assert.True(AckRangeList.TryRead(ref reader, out var decoded));
        Assert.Equal([Range(0, 0), Range(2, 2), Range(4, 4), Range(6, 6)], decoded.Ranges);
    }

    private static AckRange Range(uint min, uint max) => new(new UInt24(min), new UInt24(max));
}
