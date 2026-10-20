using RakDotNet.Protocol.Wire;
using static RakDotNet.Protocol.Tests.Wire.NativeBits;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are known answers printed by native RakNet 1a169895 with tools/bitstream-vectors, after
// AddString("zebra"), AddString("apple") and AddString("mango").
public class StringTableTests
{
    private static StringTable Fruit()
    {
        var table = new StringTable();
        table.AddString("zebra");
        table.AddString("apple"u8);
        table.AddString("mango");
        return table;
    }

    [Theory]
    [InlineData("apple", "c000")]
    [InlineData("mango", "c040")]
    [InlineData("zebra", "c080")]
    public void A_registered_string_is_a_1_bit_then_its_index_in_sorted_order(string text, string hex)
    {
        // StringTable.cpp:90-100: the list is ordered by strcmp, so the index is the sorted position, not the order added.
        var table = Fruit();
        AssertWritten((ref BitStreamWriter w) => table.EncodeString(ref w, text, 256), 10, hex);

        var reader = Reader(hex, 10);
        Assert.True(table.TryDecodeString(ref reader, 256, out var decoded));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(text), decoded);
    }

    [Fact]
    public void An_unregistered_string_is_a_0_bit_then_the_compressed_string()
    {
        // StringTable.cpp:101-106.
        var table = Fruit();
        AssertWritten((ref BitStreamWriter w) => table.EncodeString(ref w, "kiwi"u8, 256), 59, "800000030fd29440");

        var reader = Reader("800000030fd29440", 59);
        Assert.True(table.TryDecodeString(ref reader, 256, out var decoded));
        Assert.Equal("kiwi"u8.ToArray(), decoded);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void A_decoded_registered_string_keeps_one_less_than_the_limit()
    {
        // StringTable.cpp:135-136: strncpy then a NUL at maxCharsToWrite - 1; native decodes index 2 with 4 as "zeb".
        var table = Fruit();
        var reader = new BitStreamReader([0x81, 0x00], 9);
        Assert.True(table.TryDecodeString(ref reader, 4, out var decoded));
        Assert.Equal("zeb"u8.ToArray(), decoded);
    }

    [Fact]
    public void An_index_past_the_table_fails()
    {
        // StringTable.cpp:124-132.
        var table = Fruit();
        var reader = new BitStreamReader([0x81, 0x80], 9);
        Assert.False(table.TryDecodeString(ref reader, 16, out _));
        Assert.Equal(0, reader.BitPosition);
    }

    [Fact]
    public void Decoding_fails_with_no_room_or_truncated_input()
    {
        // StringTable.cpp:113-116; a truncated compressed string fails where RakNet ignores DecodeString's result.
        var table = Fruit();
        var reader = new BitStreamReader([0x80, 0x00], 9);
        Assert.False(table.TryDecodeString(ref reader, 0, out _));
        reader = new BitStreamReader([0x80], 4);
        Assert.False(table.TryDecodeString(ref reader, 16, out _));
        reader = Reader("800000030fd29440", 40);
        Assert.False(table.TryDecodeString(ref reader, 256, out _));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void Adding_a_string_twice_is_rejected()
    {
        // StringTable.cpp:83: OrderedList::Insert asserts on a duplicate.
        var table = Fruit();
        Assert.Throws<ArgumentException>(() => table.AddString("apple"));
    }

    [Fact]
    public void The_table_holds_at_most_254_strings()
    {
        // StringTable.cpp:86: the size must stay below (StringTableType)-1, 255.
        var table = new StringTable();
        for (var i = 0; i < 254; i++)
            table.AddString($"s{i:D3}");
        Assert.Throws<InvalidOperationException>(() => table.AddString("one too many"));
    }

    [Fact]
    public void A_string_ends_at_its_first_NUL()
    {
        // AddString copies with strcpy and lookups compare with strcmp.
        var table = new StringTable();
        table.AddString("ab\0cd"u8);
        AssertWritten((ref BitStreamWriter w) => table.EncodeString(ref w, "ab"u8, 256), 10, "c000");
        AssertWritten((ref BitStreamWriter w) => table.EncodeString(ref w, "ab\0zz"u8, 256), 10, "c000");
    }

    [Fact]
    public void Instance_is_one_shared_table()
    {
        Assert.Same(StringTable.Instance, StringTable.Instance);
    }
}
