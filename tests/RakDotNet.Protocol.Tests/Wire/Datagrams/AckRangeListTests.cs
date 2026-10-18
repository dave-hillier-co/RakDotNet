using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using static RakDotNet.Protocol.Tests.Wire.Datagrams.WireWriter;

namespace RakDotNet.Protocol.Tests.Wire.Datagrams;

// Expected bytes are derived by hand from DataStructures::RangeList<uint24_t>::Serialize (DS_RangeList.h:64-113):
// align, u16 count big-endian, then per range an unsigned char minEqualsMax (a whole byte, not a bit), the uint24
// min (aligned little-endian) and, when min != max, the uint24 max.
public class AckRangeListTests
{
    private static AckRange R(uint min, uint max) => new(new UInt24(min), new UInt24(max));

    private static AckRange R(uint value) => R(value, value);

    [Fact]
    public void Ranges_are_written_as_count_then_flag_byte_min_and_optional_max()
    {
        var list = new AckRangeList([R(5), R(7, 9)]);

        var bytes = Write((ref BitStreamWriter w) => list.Write(ref w));

        Assert.Equal(Hex("0002 01 050000 00 070000 090000"), bytes);
    }

    [Fact]
    public void Empty_list_is_a_zero_count()
    {
        var bytes = Write((ref BitStreamWriter w) => AckRangeList.Empty.Write(ref w));

        Assert.Equal(Hex("0000"), bytes);
    }

    [Fact]
    public void Ack_datagram_is_the_header_byte_followed_by_the_list()
    {
        // ReliabilityLayer.cpp:3647-3649
        var list = new AckRangeList([R(0x010203)]);

        var bytes = Write((ref BitStreamWriter w) =>
        {
            DatagramHeader.ForAck().Write(ref w);
            list.Write(ref w);
        });

        Assert.Equal(Hex("C0 0001 01 030201"), bytes);
    }

    [Fact]
    public void Nak_datagram_list_aligns_after_the_three_bit_header()
    {
        // The NAK header is 3 bits (ReliabilityLayer.cpp:165-169); RangeList::Serialize aligns (DS_RangeList.h:94).
        var list = new AckRangeList([R(3)]);

        var bytes = Write((ref BitStreamWriter w) =>
        {
            DatagramHeader.ForNak().Write(ref w);
            list.Write(ref w);
        }, out var bits);

        Assert.Equal(Hex("A0 0001 01 030000"), bytes);
        Assert.Equal(8 + 16 + 32, bits);
    }

    [Fact]
    public void Reading_aligns_before_the_count()
    {
        var bytes = Hex("A0 0001 01 030000");
        var reader = new BitStreamReader(bytes);
        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Nak, header.Kind);

        Assert.True(AckRangeList.TryRead(ref reader, out var list));
        Assert.Equal([R(3)], list.Ranges);
        Assert.Equal(0, reader.BitsRemaining);
    }

    public static TheoryData<AckRange[]> Lists =>
        new()
        {
            Array.Empty<AckRange>(),
            new[] { R(0) },
            new[] { R(0, 0xFFFFFE) },
            new[] { R(1), R(3, 4), R(0xFFFFFF) },
            new[] { R(10, 20), R(30, 40), R(50) },
        };

    [Theory]
    [MemberData(nameof(Lists))]
    public void Lists_round_trip(AckRange[] ranges)
    {
        var list = new AckRangeList(ranges);
        var bytes = Write((ref BitStreamWriter w) => list.Write(ref w));
        var reader = new BitStreamReader(bytes);

        Assert.True(AckRangeList.TryRead(ref reader, out var decoded));
        Assert.Equal(list, decoded);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void FromNumbers_condenses_consecutive_numbers_and_drops_duplicates()
    {
        var list = AckRangeList.FromNumbers([new(9), new(5), new(7), new(8), new(5)]);

        Assert.Equal([R(5), R(7, 9)], list.Ranges);
    }

    [Fact]
    public void FromNumbers_splits_at_the_24_bit_wrap_and_sorts_by_plain_value()
    {
        // DS_RangeList::Insert compares uint24_t values numerically and never joins 0xFFFFFF with 0
        // (DS_RangeList.h:159-218), and the receiver rejects any range with max < min (DS_RangeList.h:134), so a
        // range may not straddle the wrap.
        var list = AckRangeList.FromNumbers([new(0xFFFFFE), new(0xFFFFFF), new(0), new(1)]);

        Assert.Equal([R(0, 1), R(0xFFFFFE, 0xFFFFFF)], list.Ranges);
    }

    [Fact]
    public void FromNumbers_with_no_numbers_is_empty()
    {
        Assert.Equal(AckRangeList.Empty, AckRangeList.FromNumbers([]));
    }

    [Fact]
    public void Range_with_min_above_max_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => R(5, 4));
    }

    [Fact]
    public void Range_count_and_membership_do_not_expand_the_range()
    {
        var list = new AckRangeList([R(10, 20), R(0xFFFFFF)]);

        Assert.Equal(11u, list.Ranges[0].Count);
        Assert.Equal(12L, list.TotalNumbers);
        Assert.True(list.Contains(new UInt24(15)));
        Assert.True(list.Contains(UInt24.MaxValue));
        Assert.False(list.Contains(new UInt24(21)));
    }

    [Fact]
    public void Budgeted_write_uses_cpp_accounting_of_four_byte_uint24()
    {
        // DS_RangeList.h:76 breaks when 16 + bitsWritten + sizeof(uint24_t)*8*2 + 1 > maxBits, and line 85 adds
        // sizeof(uint24_t)*8 + 8 per single range. sizeof(uint24_t) is 4 (RakNetTypes.h:464-466, a uint32_t val), so
        // each single range is budgeted at 40 bits although it occupies 32 on the wire.
        // maxBits = 200: before range i the test is 81 + 40i > 200, so ranges 0..2 are written and range 3 is not.
        var list = new AckRangeList([R(1), R(3), R(5), R(7), R(9)]);
        var written = 0;

        var bytes = Write((ref BitStreamWriter w) => written = list.Write(ref w, maxBits: 200));

        Assert.Equal(3, written);
        Assert.Equal(Hex("0003 01 010000 01 030000 01 050000"), bytes);
        Assert.Equal([R(7), R(9)], list.Skip(written).Ranges);
    }

    [Fact]
    public void Budgeted_write_boundary_is_inclusive_of_maxBits()
    {
        // 81 + 40*3 = 201 is not > 201, so the fourth range fits.
        var list = new AckRangeList([R(1), R(3), R(5), R(7), R(9)]);
        var written = 0;

        Write((ref BitStreamWriter w) => written = list.Write(ref w, maxBits: 201));

        Assert.Equal(4, written);
    }

    [Fact]
    public void Budgeted_write_counts_ranged_entries_at_72_bits()
    {
        // A min != max range adds a further sizeof(uint24_t)*8 = 32 (DS_RangeList.h:86-90): 72 bits.
        // maxBits = 152: range 0 fits (81 <= 152), range 1 does not (16 + 72 + 65 = 153 > 152).
        var list = new AckRangeList([R(1, 2), R(4, 5)]);
        var written = 0;

        Write((ref BitStreamWriter w) => written = list.Write(ref w, maxBits: 152));

        Assert.Equal(1, written);
    }

    [Fact]
    public void At_the_maximum_mtu_cpp_fits_295_single_ranges_per_ack()
    {
        // maxBits = (1492 - 9) * 8 = 11864 (ReliabilityLayer.cpp:3618, 3909-3924). 81 + 40i <= 11864 for i <= 294.
        var list = AckRangeList.FromNumbers(Enumerable.Range(0, 400).Select(i => new UInt24((uint)i * 2)));
        var written = 0;

        var bytes = Write((ref BitStreamWriter w) =>
        {
            DatagramHeader.ForAck().Write(ref w);
            written = list.Write(ref w, DatagramLimits.MaxPayloadBits(DatagramLimits.MaximumMtuSize));
        });

        Assert.Equal(295, written);
        Assert.Equal(1 + 2 + 295 * 4, bytes.Length);
        Assert.Equal(105, list.Skip(written).Ranges.Count);
    }

    [Fact]
    public void Budget_too_small_for_any_range_writes_a_zero_count()
    {
        var written = -1;

        var bytes = Write((ref BitStreamWriter w) => written = new AckRangeList([R(1)]).Write(ref w, maxBits: 80));

        Assert.Equal(0, written);
        Assert.Equal(Hex("0000"), bytes);
    }

    [Fact]
    public void Any_nonzero_flag_byte_means_min_equals_max()
    {
        // DS_RangeList.h:130 tests maxEqualToMin==false, so only zero means a max follows.
        var reader = new BitStreamReader(Hex("0001 02 050000"));

        Assert.True(AckRangeList.TryRead(ref reader, out var list));
        Assert.Equal([R(5)], list.Ranges);
    }

    [Fact]
    public void Zero_flag_with_equal_min_and_max_is_accepted()
    {
        var reader = new BitStreamReader(Hex("0001 00 050000 050000"));

        Assert.True(AckRangeList.TryRead(ref reader, out var list));
        Assert.Equal([R(5)], list.Ranges);
    }

    [Fact]
    public void Range_with_max_below_min_is_rejected()
    {
        // DS_RangeList.h:134-135. The comparison is numeric, so a range that would wrap is invalid.
        var reader = new BitStreamReader(Hex("0001 00 FFFFFF 000000"));

        Assert.False(AckRangeList.TryRead(ref reader, out _));
    }

    [Fact]
    public void Widest_range_decodes_to_one_range_without_expansion()
    {
        var reader = new BitStreamReader(Hex("0001 00 000000 FFFFFF"));

        Assert.True(AckRangeList.TryRead(ref reader, out var list));
        Assert.Single(list.Ranges);
        Assert.Equal(1L << 24, list.TotalNumbers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("0001")]
    [InlineData("0001 01")]
    [InlineData("0001 01 0500")]
    [InlineData("0001 00 050000")]
    [InlineData("0001 00 050000 0900")]
    [InlineData("0002 01 050000")]
    public void Truncated_lists_fail(string hex)
    {
        var reader = new BitStreamReader(Hex(hex));

        Assert.False(AckRangeList.TryRead(ref reader, out _));
    }

    [Fact]
    public void Range_count_is_capped_to_what_the_maximum_mtu_can_carry()
    {
        // A single range is at least 4 bytes; after the 1-byte header and 2-byte count a 1492-byte datagram holds 372.
        Assert.Equal(372, AckRangeList.MaxRangesPerDatagram);

        var atCap = new BitStreamReader(SingleRanges(372));
        Assert.True(AckRangeList.TryRead(ref atCap, out var list));
        Assert.Equal(372, list.Ranges.Count);

        var overCap = new BitStreamReader(SingleRanges(373));
        Assert.False(AckRangeList.TryRead(ref overCap, out _));
    }

    [Fact]
    public void Huge_count_with_little_data_fails_without_reading_ranges()
    {
        var reader = new BitStreamReader(Hex("FFFF 01 050000"));

        Assert.False(AckRangeList.TryRead(ref reader, out _));
    }

    private static byte[] SingleRanges(int count)
    {
        var bytes = new byte[2 + count * 4];
        bytes[0] = (byte)(count >> 8);
        bytes[1] = (byte)count;
        for (var i = 0; i < count; i++)
        {
            bytes[2 + i * 4] = 1;
            bytes[3 + i * 4] = (byte)(i * 2);
            bytes[4 + i * 4] = (byte)(i * 2 >> 8);
        }
        return bytes;
    }
}
