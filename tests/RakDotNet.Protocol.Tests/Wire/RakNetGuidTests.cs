using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class RakNetGuidTests
{
    [Fact]
    public void Unassigned_is_all_ones_as_UNASSIGNED_RAKNET_GUID()
    {
        // RakNetTypes.h:364 - UNASSIGNED_RAKNET_GUID((uint64_t)-1)
        Assert.Equal(ulong.MaxValue, RakNetGuid.Unassigned.Value);
        Assert.False(RakNetGuid.Unassigned.IsAssigned);
    }

    [Fact]
    public void Any_other_value_is_assigned()
    {
        Assert.True(new RakNetGuid(0).IsAssigned);
        Assert.True(new RakNetGuid(1234567890).IsAssigned);
    }

    [Fact]
    public void ToString_is_decimal()
    {
        Assert.Equal("1520003954185392640", new RakNetGuid(1520003954185392640).ToString());
    }

    [Fact]
    public void ToString_of_unassigned_is_RakNet_text()
    {
        // RakNetTypes.cpp:784-785
        Assert.Equal("UNASSIGNED_RAKNET_GUID", RakNetGuid.Unassigned.ToString());
    }

    [Fact]
    public void TryFormat_writes_the_same_text_as_ToString()
    {
        Span<char> buffer = stackalloc char[32];

        Assert.True(new RakNetGuid(98765).TryFormat(buffer, out var written, default, null));

        Assert.Equal("98765", buffer[..written].ToString());
    }

    [Fact]
    public void TryFormat_fails_when_the_destination_is_too_short()
    {
        Span<char> buffer = stackalloc char[3];

        Assert.False(new RakNetGuid(98765).TryFormat(buffer, out _, default, null));
    }

    [Theory]
    [InlineData("98765", 98765ul)]
    [InlineData("UNASSIGNED_RAKNET_GUID", ulong.MaxValue)]
    public void Parse_reads_what_ToString_writes(string text, ulong expected)
    {
        Assert.Equal(new RakNetGuid(expected), RakNetGuid.Parse(text, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("12a")]
    [InlineData("18446744073709551616")]
    public void TryParse_rejects_text_that_is_not_an_unsigned_decimal(string text)
    {
        Assert.False(RakNetGuid.TryParse(text, null, out _));
    }
}
