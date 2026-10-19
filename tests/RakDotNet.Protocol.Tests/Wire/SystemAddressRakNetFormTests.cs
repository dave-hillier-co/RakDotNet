using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected text and orderings are what native RakNet 1a169895 (RAKNET_SUPPORT_IPV6 0, so SetBinaryAddress and
// ToString_Old) printed on arm64 macOS with tools/bitstream-vectors.
public class SystemAddressRakNetFormTests
{
    private static SystemAddress Address(string ip, int port) => new(IPAddress.Parse(ip), port);

    [Fact]
    public void ToString_with_a_port_uses_RakNet_s_delimiter()
    {
        // RakNetTypes.cpp:262-302: inet_ntoa, then the delimiter and the port, '|' by default.
        var address = Address("5.6.7.8", 77);
        Assert.Equal("5.6.7.8|77", address.ToString(writePort: true));
        Assert.Equal("5.6.7.8", address.ToString(writePort: false));
        Assert.Equal("5.6.7.8:77", address.ToString(writePort: true, portDelimiter: ':'));
        Assert.Equal("UNASSIGNED_SYSTEM_ADDRESS", SystemAddress.Unassigned.ToString(writePort: false));
    }

    [Fact]
    public void The_modern_text_form_is_unchanged()
    {
        Assert.Equal("5.6.7.8:77", Address("5.6.7.8", 77).ToString());
    }

    [Theory]
    [InlineData("1.2.3.4|5678", "1.2.3.4|5678")]
    [InlineData("1.2.3.4:5678", "1.2.3.4|5678")]
    [InlineData("1.2.3.4", "1.2.3.4|4321")]
    [InlineData("localhost", "127.0.0.1|4321")]
    [InlineData("LocalHost9000", "127.0.0.1|9000")]
    [InlineData("localhost|9000", "127.0.0.1|0")]
    [InlineData("1.2", "1.0.0.2|4321")]
    [InlineData("010.0.0.1", "8.0.0.1|4321")]
    [InlineData("300.1.1.1", "255.255.255.255|4321")]
    [InlineData("|80", "9.9.9.9|80")]
    [InlineData("1.2.3.4|99999", "1.2.3.4|34463")]
    [InlineData("1.2.3.4 80", "1.2.3.4|80")]
    [InlineData("", "9.9.9.9|4321")]
    public void TryFromString_reads_RakNet_s_forms_and_keeps_the_parts_the_text_leaves_out(string text, string expected)
    {
        // SystemAddress::SetBinaryAddress (RakNetTypes.cpp:477-590) on an address that was 9.9.9.9|4321.
        var previous = Address("9.9.9.9", 4321);
        Assert.True(previous.TryFromString(text, out var result));
        Assert.Equal(expected, result.ToString(writePort: true));
    }

    [Fact]
    public void TryParse_with_a_delimiter_starts_from_an_unassigned_address()
    {
        Assert.True(SystemAddress.TryParse("1.2.3.4|5678", '|', out var withPort));
        Assert.Equal(Address("1.2.3.4", 5678), withPort);
        Assert.True(SystemAddress.TryParse("1.2.3.4", '|', out var noPort));
        Assert.Equal(Address("1.2.3.4", 0), noPort);
        Assert.True(SystemAddress.TryParse("1.2.3.4#99", '#', out var hash));
        Assert.Equal(99, hash.Port);
    }

    [Fact]
    public void A_host_name_other_than_localhost_is_not_resolved()
    {
        // SetBinaryAddress resolves other names with DNS (RakNetTypes.cpp:500-518); the wire layer does no I/O, so it
        // fails as RakNet does when resolution fails: false and UNASSIGNED_SYSTEM_ADDRESS.
        Assert.False(Address("9.9.9.9", 1).TryFromString("example.com", out var result));
        Assert.Equal(SystemAddress.Unassigned, result);
    }

    [Fact]
    public void TryFromStringExplicitPort_ignores_any_port_in_the_text()
    {
        // RakNetTypes.cpp:734-745: FromString with no delimiter, then the given port.
        Assert.True(SystemAddress.TryFromStringExplicitPort("5.6.7.8:12", 77, out var result));
        Assert.Equal("5.6.7.8|77", result.ToString(writePort: true));
        Assert.False(SystemAddress.TryFromStringExplicitPort("example.com", 77, out var failed));
        Assert.Equal(SystemAddress.Unassigned, failed);
    }

    [Fact]
    public void WithPort_changes_only_the_port()
    {
        // SetPortHostOrder and CopyPort (RakNetTypes.cpp:142-146, 746-750).
        var address = Address("1.2.3.4", 1);
        Assert.Equal(Address("1.2.3.4", 9), address.WithPort(9));
        Assert.Equal(Address("1.2.3.4", 5), address.WithPort(Address("8.8.8.8", 5).Port));
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("8.8.8.10", false)]
    public void IsLanAddress_checks_the_first_octet_for_10_or_192(string ip, bool expected)
    {
        // RakNetTypes.cpp:468-476, the Windows form; the POSIX build shifts the network-order value and so tests the last
        // octet instead (native arm64 macOS says 8.8.8.10 is a LAN address).
        Assert.Equal(expected, Address(ip, 1).IsLanAddress);
    }

    private static readonly string[] Ordered = ["1.2.3.4|1", "1.2.3.4|256", "4.3.2.1|256", "1.2.3.5|256", "2.0.0.0|256", "1.2.3.4|2"];

    // Native operator< for every pair of Ordered, row by row: 1 when Ordered[row] < Ordered[column].
    private static readonly string[] NativeLessThan = ["000001", "100101", "110101", "100001", "111101", "000000"];

    [Fact]
    public void Ordering_matches_RakNet_s_operators()
    {
        // RakNetTypes.cpp:162-191: the port then the address, each compared as the host-order integer of its
        // network-order bytes, so on little-endian hosts the low byte of the port and the last octet weigh most.
        for (var row = 0; row < Ordered.Length; row++)
        {
            for (var column = 0; column < Ordered.Length; column++)
            {
                Assert.True(SystemAddress.TryParse(Ordered[row], '|', out var left));
                Assert.True(SystemAddress.TryParse(Ordered[column], '|', out var right));
                var lessThan = NativeLessThan[row][column] == '1';
                var greaterThan = NativeLessThan[column][row] == '1';
                Assert.Equal(lessThan, left < right);
                Assert.Equal(greaterThan, left > right);
                Assert.Equal(!greaterThan, left <= right);
                Assert.Equal(!lessThan, left >= right);
                Assert.Equal(lessThan ? -1 : greaterThan ? 1 : 0, Math.Sign(left.CompareTo(right)));
            }
        }
    }

    [Fact]
    public void IPv4_addresses_sort_before_IPv6_ones_on_the_same_port()
    {
        var v4 = Address("255.255.255.255", 1);
        var v6 = Address("::1", 1);
        Assert.True(v4 < v6);
        Assert.True(Address("::1", 1) < Address("::2", 1));
    }

    [Fact]
    public void RakNetGuid_orders_by_its_value()
    {
        // RakNetTypes.cpp:764-771.
        var low = new RakNetGuid(1);
        var high = new RakNetGuid(2);
        Assert.True(low < high);
        Assert.True(high > low);
        Assert.True(low <= new RakNetGuid(1));
        Assert.True(high >= low);
        Assert.Equal(-1, Math.Sign(low.CompareTo(high)));
    }
}
