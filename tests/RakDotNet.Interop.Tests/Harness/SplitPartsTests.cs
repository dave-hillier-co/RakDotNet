namespace RakDotNet.Interop.Tests.Harness;

public sealed class SplitPartsTests
{
    private const int PartLength = 4;

    // Three full parts and a short last part of two bytes.
    private static readonly byte[] Message = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];

    [Fact]
    public void Parts_joined_in_index_order_are_a_join_in_some_order()
    {
        Assert.True(SplitParts.IsJoinedInSomeOrder(Message, Message, PartLength));
    }

    [Fact]
    public void Full_parts_joined_out_of_order_are_a_join_in_some_order()
    {
        byte[] joined = [4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 12, 13];

        Assert.True(SplitParts.IsJoinedInSomeOrder(joined, Message, PartLength));
    }

    [Fact]
    public void The_short_last_part_joined_before_the_others_is_a_join_in_some_order()
    {
        byte[] first = [12, 13, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
        byte[] middle = [8, 9, 10, 11, 12, 13, 0, 1, 2, 3, 4, 5, 6, 7];

        Assert.True(SplitParts.IsJoinedInSomeOrder(first, Message, PartLength));
        Assert.True(SplitParts.IsJoinedInSomeOrder(middle, Message, PartLength));
    }

    [Fact]
    public void A_single_part_message_is_joined_only_as_itself()
    {
        Assert.True(SplitParts.IsJoinedInSomeOrder(new byte[] { 1, 2 }, new byte[] { 1, 2 }, PartLength));
        Assert.False(SplitParts.IsJoinedInSomeOrder(new byte[] { 2, 1 }, new byte[] { 1, 2 }, PartLength));
    }

    [Fact]
    public void A_changed_byte_is_not_a_join()
    {
        byte[] joined = [4, 5, 6, 7, 0, 1, 2, 3, 8, 9, 99, 11, 12, 13];

        Assert.False(SplitParts.IsJoinedInSomeOrder(joined, Message, PartLength));
    }

    [Fact]
    public void A_part_repeated_in_place_of_another_is_not_a_join()
    {
        byte[] joined = [0, 1, 2, 3, 0, 1, 2, 3, 8, 9, 10, 11, 12, 13];

        Assert.False(SplitParts.IsJoinedInSomeOrder(joined, Message, PartLength));
    }

    [Fact]
    public void Bytes_that_straddle_the_part_boundaries_are_not_a_join()
    {
        // Every byte is present, but shifted by one rather than moved a whole part at a time.
        byte[] joined = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 0];

        Assert.False(SplitParts.IsJoinedInSomeOrder(joined, Message, PartLength));
    }

    [Fact]
    public void A_join_of_a_different_length_is_not_a_join()
    {
        Assert.False(SplitParts.IsJoinedInSomeOrder(Message.AsMemory(0, 12), Message, PartLength));
    }

    [Fact]
    public void Repeated_part_contents_are_matched_by_count()
    {
        // BigPacketTest's data repeats every 256 bytes, so parts at the same offset modulo 256 are equal.
        byte[] message = [7, 7, 8, 8, 7, 7, 9];
        byte[] swapped = [8, 8, 7, 7, 7, 7, 9];
        byte[] lost = [8, 8, 8, 8, 7, 7, 9];

        Assert.True(SplitParts.IsJoinedInSomeOrder(swapped, message, 2));
        Assert.False(SplitParts.IsJoinedInSomeOrder(lost, message, 2));
    }
}
