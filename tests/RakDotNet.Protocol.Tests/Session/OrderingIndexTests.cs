using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// ReliabilityLayer::IsOlderOrderedPacket (ReliabilityLayer.cpp:2901-2922): half of the 24-bit space behind the
// index being waited for is "older", the other half is newer.
public class OrderingIndexTests
{
    [Theory]
    [InlineData(9u, 10u, true)]
    [InlineData(0u, 10u, true)]
    [InlineData(10u, 10u, false)]
    [InlineData(11u, 10u, false)]
    [InlineData(0x80000Au, 10u, true)] // 10 - 0x800000, wrapped: the far end of the older half
    [InlineData(0x800009u, 10u, false)]
    [InlineData(0xFFFFFFu, 10u, true)]
    [InlineData(0xFFFFFEu, 0xFFFFFFu, true)]
    [InlineData(0u, 0xFFFFFFu, false)] // wrapped past the maximum: newer
    [InlineData(0x800000u, 0xFFFFFFu, false)] // waiting - half: outside the older range
    [InlineData(0x800001u, 0xFFFFFFu, true)]
    public void Indexes_up_to_half_the_space_behind_are_older(uint candidate, uint waitingFor, bool older)
    {
        Assert.Equal(older, OrderingIndex.IsOlder(new UInt24(candidate), new UInt24(waitingFor)));
    }
}
