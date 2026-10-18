using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class SystemAddressTests
{
    [Fact]
    public void Unassigned_is_the_default_value_and_is_IPv4_any_port_zero()
    {
        // RakNetTypes.cpp:378-385 - the default SystemAddress is AF_INET, all zero
        Assert.Equal(default, SystemAddress.Unassigned);
        Assert.Equal(new SystemAddress(IPAddress.Any, 0), SystemAddress.Unassigned);
        Assert.Equal(AddressFamily.InterNetwork, SystemAddress.Unassigned.AddressFamily);
        Assert.False(SystemAddress.Unassigned.IsAssigned);
    }

    [Fact]
    public void An_address_with_a_port_is_assigned()
    {
        var address = new SystemAddress(IPAddress.Loopback, 19132);

        Assert.True(address.IsAssigned);
        Assert.Equal(19132, address.Port);
        Assert.Equal(AddressFamily.InterNetwork, address.AddressFamily);
    }

    [Fact]
    public void Addresses_are_equal_when_family_address_and_port_match()
    {
        var a = new SystemAddress(IPAddress.Parse("192.0.2.1"), 1234);
        var b = new SystemAddress(IPAddress.Parse("192.0.2.1"), 1234);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Addresses_differ_by_port()
    {
        var a = new SystemAddress(IPAddress.Parse("192.0.2.1"), 1234);
        var b = new SystemAddress(IPAddress.Parse("192.0.2.1"), 1235);

        Assert.NotEqual(a, b);
        Assert.True(a != b);
        Assert.True(a.EqualsIgnoringPort(b));
    }

    [Fact]
    public void EqualsIgnoringPort_still_compares_the_address()
    {
        var a = new SystemAddress(IPAddress.Parse("192.0.2.1"), 1234);
        var b = new SystemAddress(IPAddress.Parse("192.0.2.2"), 1234);

        Assert.False(a.EqualsIgnoringPort(b));
    }

    [Fact]
    public void IPv4_and_IPv6_addresses_are_never_equal()
    {
        var v4 = new SystemAddress(IPAddress.Any, 1234);
        var v6 = new SystemAddress(IPAddress.IPv6Any, 1234);

        Assert.NotEqual(v4, v6);
        Assert.False(v4.EqualsIgnoringPort(v6));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::1", true)]
    [InlineData("192.0.2.1", false)]
    [InlineData("127.0.0.2", false)]
    public void IsLoopback_follows_SystemAddress_IsLoopback(string address, bool expected)
    {
        // RakNetTypes.cpp:242-259 - 127.0.0.1 and 0.0.0.0 for IPv4, ::1 for IPv6
        Assert.Equal(expected, new SystemAddress(IPAddress.Parse(address), 1).IsLoopback);
    }

    [Fact]
    public void Converts_to_and_from_IPEndPoint()
    {
        var endPoint = new IPEndPoint(IPAddress.Parse("2001:db8::1"), 61234);

        SystemAddress address = endPoint;

        Assert.Equal(AddressFamily.InterNetworkV6, address.AddressFamily);
        Assert.Equal(endPoint, address.ToIPEndPoint());
    }

    [Fact]
    public void TryWriteAddressBytes_writes_the_address_in_network_order()
    {
        Span<byte> buffer = stackalloc byte[16];

        Assert.True(new SystemAddress(IPAddress.Parse("192.0.2.1"), 1).TryWriteAddressBytes(buffer, out var written));

        Assert.Equal([192, 0, 2, 1], buffer[..written].ToArray());
    }

    [Fact]
    public void TryWriteAddressBytes_fails_when_the_destination_is_too_short()
    {
        Span<byte> buffer = stackalloc byte[4];

        Assert.False(new SystemAddress(IPAddress.IPv6Loopback, 1).TryWriteAddressBytes(buffer, out _));
    }

    [Fact]
    public void ToString_of_IPv4_is_address_colon_port()
    {
        Assert.Equal("127.0.0.1:61234", new SystemAddress(IPAddress.Loopback, 61234).ToString());
    }

    [Fact]
    public void ToString_of_IPv6_brackets_the_address()
    {
        Assert.Equal("[2001:db8::1]:61234", new SystemAddress(IPAddress.Parse("2001:db8::1"), 61234).ToString());
    }

    [Fact]
    public void ToString_of_unassigned_is_RakNet_text()
    {
        // RakNetTypes.cpp:264-267
        Assert.Equal("UNASSIGNED_SYSTEM_ADDRESS", SystemAddress.Unassigned.ToString());
    }

    [Fact]
    public void TryFormat_fails_when_the_destination_is_too_short()
    {
        Span<char> buffer = stackalloc char[5];

        Assert.False(new SystemAddress(IPAddress.Loopback, 61234).TryFormat(buffer, out _, default, null));
    }

    [Theory]
    [InlineData("127.0.0.1:61234")]
    [InlineData("[2001:db8::1]:61234")]
    [InlineData("UNASSIGNED_SYSTEM_ADDRESS")]
    public void Parse_reads_what_ToString_writes(string text)
    {
        Assert.Equal(text, SystemAddress.Parse(text, null).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("127.0.0.1:65536")]
    public void TryParse_rejects_malformed_text(string text)
    {
        Assert.False(SystemAddress.TryParse(text, null, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_rejects_a_port_out_of_range(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SystemAddress(IPAddress.Loopback, port));
    }
}
