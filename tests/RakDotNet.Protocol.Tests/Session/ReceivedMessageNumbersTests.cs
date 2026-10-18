using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// The duplicate filter for reliable message numbers: receivedPacketsBaseIndex and hasReceivedPacketQueue
// (ReliabilityLayer.cpp:941-1044).
public class ReceivedMessageNumbersTests
{
    [Fact]
    public void Message_numbers_in_order_are_each_accepted_once()
    {
        var numbers = new ReceivedMessageNumbers();

        Assert.True(numbers.TryAccept(new UInt24(0)));
        Assert.True(numbers.TryAccept(new UInt24(1)));
        Assert.False(numbers.TryAccept(new UInt24(1)));
        Assert.False(numbers.TryAccept(new UInt24(0)));
        Assert.Equal(new UInt24(2), numbers.BaseIndex);
    }

    [Fact]
    public void A_number_ahead_of_the_base_is_accepted_and_leaves_holes_for_the_numbers_skipped()
    {
        var numbers = new ReceivedMessageNumbers();

        Assert.True(numbers.TryAccept(new UInt24(3)));
        Assert.False(numbers.TryAccept(new UInt24(3)));
        Assert.Equal(new UInt24(0), numbers.BaseIndex);

        Assert.True(numbers.TryAccept(new UInt24(1)));
        Assert.True(numbers.TryAccept(new UInt24(0)));
        Assert.Equal(new UInt24(2), numbers.BaseIndex);

        Assert.True(numbers.TryAccept(new UInt24(2)));
        Assert.Equal(new UInt24(4), numbers.BaseIndex);
    }

    [Fact]
    public void Numbers_more_than_a_million_ahead_are_rejected()
    {
        var numbers = new ReceivedMessageNumbers();

        Assert.False(numbers.TryAccept(new UInt24(1_000_001)));
        Assert.True(numbers.TryAccept(new UInt24(1_000_000)));
    }

    [Fact]
    public void The_base_index_wraps_at_24_bits()
    {
        var numbers = new ReceivedMessageNumbers(new UInt24(UInt24.Mask));

        Assert.True(numbers.TryAccept(new UInt24(UInt24.Mask)));
        Assert.True(numbers.TryAccept(new UInt24(0)));
        Assert.False(numbers.TryAccept(new UInt24(UInt24.Mask)));
        Assert.Equal(new UInt24(1), numbers.BaseIndex);
    }
}
