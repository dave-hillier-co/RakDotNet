using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class UInt24Tests
{
    [Fact]
    public void Construction_masks_to_24_bits()
    {
        Assert.Equal(0x345678u, new UInt24(0x12345678).Value);
    }

    [Fact]
    public void Arithmetic_wraps()
    {
        Assert.Equal(UInt24.MinValue, UInt24.MaxValue + 1);
        Assert.Equal(UInt24.MaxValue, UInt24.MinValue - 1);

        var value = UInt24.MaxValue;
        value++;
        Assert.Equal(UInt24.MinValue, value);
    }

    [Theory]
    [InlineData(1u, 0u, true)]
    [InlineData(0u, 0xFFFFFFu, true)]
    [InlineData(0u, 1u, false)]
    [InlineData(5u, 5u, false)]
    public void GreaterThan_uses_wrapping_order(uint a, uint b, bool expected)
    {
        Assert.Equal(expected, UInt24.GreaterThan(new UInt24(a), new UInt24(b)));
    }

    [Theory]
    [InlineData(0u, 1u, true)]
    [InlineData(0xFFFFFFu, 0u, true)]
    [InlineData(1u, 0u, false)]
    [InlineData(5u, 5u, false)]
    public void LessThan_uses_wrapping_order(uint a, uint b, bool expected)
    {
        Assert.Equal(expected, UInt24.LessThan(new UInt24(a), new UInt24(b)));
    }

    [Fact]
    public void Exactly_half_the_span_apart_is_neither_greater_nor_less()
    {
        // Matches CCRakNetSlidingWindow: b - a == halfSpan fails both strict comparisons.
        var a = new UInt24(0);
        var b = new UInt24(UInt24.HalfSpan);

        Assert.False(UInt24.LessThan(a, b));
        Assert.False(UInt24.GreaterThan(a, b));
    }
}
