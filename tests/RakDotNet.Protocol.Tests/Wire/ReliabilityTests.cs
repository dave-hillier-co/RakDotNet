using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class ReliabilityTests
{
    [Fact]
    public void Reliability_values_follow_the_cpp_enum_order()
    {
        // PacketPriority.h:46-85 (PacketReliability), positional.
        Assert.Equal(0, (int)Reliability.Unreliable);
        Assert.Equal(1, (int)Reliability.UnreliableSequenced);
        Assert.Equal(2, (int)Reliability.Reliable);
        Assert.Equal(3, (int)Reliability.ReliableOrdered);
        Assert.Equal(4, (int)Reliability.ReliableSequenced);
        Assert.Equal(5, (int)Reliability.UnreliableWithAckReceipt);
        Assert.Equal(6, (int)Reliability.ReliableWithAckReceipt);
        Assert.Equal(7, (int)Reliability.ReliableOrderedWithAckReceipt);
    }

    [Fact]
    public void Priority_values_follow_the_cpp_enum_order()
    {
        // PacketPriority.h:21-40
        Assert.Equal(0, (int)Priority.Immediate);
        Assert.Equal(1, (int)Priority.High);
        Assert.Equal(2, (int)Priority.Medium);
        Assert.Equal(3, (int)Priority.Low);
    }

    [Theory]
    [InlineData(Reliability.Unreliable, false)]
    [InlineData(Reliability.UnreliableSequenced, false)]
    [InlineData(Reliability.Reliable, true)]
    [InlineData(Reliability.ReliableOrdered, true)]
    [InlineData(Reliability.ReliableSequenced, true)]
    [InlineData(Reliability.UnreliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableWithAckReceipt, true)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, true)]
    public void IsReliable_matches_the_types_that_carry_a_reliable_message_number(Reliability reliability, bool expected)
    {
        Assert.Equal(expected, reliability.IsReliable());
    }

    [Theory]
    [InlineData(Reliability.Unreliable, false)]
    [InlineData(Reliability.UnreliableSequenced, false)]
    [InlineData(Reliability.Reliable, false)]
    [InlineData(Reliability.ReliableOrdered, true)]
    [InlineData(Reliability.ReliableSequenced, false)]
    [InlineData(Reliability.UnreliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, true)]
    public void IsOrdered_is_true_only_for_the_ordered_types(Reliability reliability, bool expected)
    {
        Assert.Equal(expected, reliability.IsOrdered());
    }

    [Theory]
    [InlineData(Reliability.Unreliable, false)]
    [InlineData(Reliability.UnreliableSequenced, true)]
    [InlineData(Reliability.Reliable, false)]
    [InlineData(Reliability.ReliableOrdered, false)]
    [InlineData(Reliability.ReliableSequenced, true)]
    [InlineData(Reliability.UnreliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, false)]
    public void IsSequenced_is_true_only_for_the_sequenced_types(Reliability reliability, bool expected)
    {
        Assert.Equal(expected, reliability.IsSequenced());
    }

    [Theory]
    [InlineData(Reliability.Unreliable, false)]
    [InlineData(Reliability.UnreliableSequenced, true)]
    [InlineData(Reliability.Reliable, false)]
    [InlineData(Reliability.ReliableOrdered, true)]
    [InlineData(Reliability.ReliableSequenced, true)]
    [InlineData(Reliability.UnreliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableWithAckReceipt, false)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, true)]
    public void HasOrderingChannel_covers_ordered_and_sequenced_types(Reliability reliability, bool expected)
    {
        // ReliabilityLayer.cpp:2650-2658 - sequenced messages also carry the ordering index and channel.
        Assert.Equal(expected, reliability.HasOrderingChannel());
    }

    [Theory]
    [InlineData(Reliability.Unreliable, false)]
    [InlineData(Reliability.ReliableSequenced, false)]
    [InlineData(Reliability.UnreliableWithAckReceipt, true)]
    [InlineData(Reliability.ReliableWithAckReceipt, true)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, true)]
    public void HasAckReceipt_is_true_for_the_receipt_variants(Reliability reliability, bool expected)
    {
        Assert.Equal(expected, reliability.HasAckReceipt());
    }

    [Theory]
    [InlineData(Reliability.Unreliable, Reliability.Unreliable)]
    [InlineData(Reliability.UnreliableSequenced, Reliability.UnreliableSequenced)]
    [InlineData(Reliability.Reliable, Reliability.Reliable)]
    [InlineData(Reliability.ReliableOrdered, Reliability.ReliableOrdered)]
    [InlineData(Reliability.ReliableSequenced, Reliability.ReliableSequenced)]
    [InlineData(Reliability.UnreliableWithAckReceipt, Reliability.Unreliable)]
    [InlineData(Reliability.ReliableWithAckReceipt, Reliability.Reliable)]
    [InlineData(Reliability.ReliableOrderedWithAckReceipt, Reliability.ReliableOrdered)]
    public void ToWireReliability_maps_receipt_variants_to_their_base_types(Reliability reliability, Reliability expected)
    {
        // ReliabilityLayer.cpp:2619-2626 (WriteToBitStreamFromInternalPacket).
        Assert.Equal(expected, reliability.ToWireReliability());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(255, false)]
    public void Only_the_five_base_types_are_valid_on_the_wire(int value, bool expected)
    {
        Assert.Equal(expected, ((Reliability)value).IsValidOnWire());
    }
}
