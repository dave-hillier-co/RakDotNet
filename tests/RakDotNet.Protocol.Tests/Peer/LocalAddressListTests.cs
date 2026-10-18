using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Peer;

// ipList: RakPeer::FillIPList (RakPeer.cpp:6463-6493) sorts the host's IPv4 addresses, then Startup gives them the
// bound port (RakPeer.cpp:585-598). It is written into ID_CONNECTION_REQUEST_ACCEPTED and ID_NEW_INCOMING_CONNECTION.
public class LocalAddressListTests
{
    [Fact]
    public void The_list_matches_the_native_peers_in_the_golden_vector()
    {
        // tests/vectors/connect-send-disconnect.json datagram 6: 127.0.0.1 then 192.168.86.51, both on the bound
        // port 0xd346, then unassigned entries.
        var list = LocalAddressList.Build([IPAddress.Parse("192.168.86.51"), IPAddress.Loopback], 0xd346);

        Assert.Equal(RakNetConstants.MaximumNumberOfInternalIds, list.Count);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 0xd346), list[0]);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("192.168.86.51"), 0xd346), list[1]);
        Assert.All(list.Skip(2), e => Assert.Equal(InternalAddressList.Unassigned, e));
    }

    [Fact]
    public void Addresses_are_compared_as_little_endian_integers_of_their_network_bytes()
    {
        // s_addr compared as a host-order integer on a little-endian machine: the last octet is most significant.
        var list = LocalAddressList.Build([IPAddress.Parse("9.0.0.3"), IPAddress.Parse("10.0.0.2")], 1);

        Assert.Equal([IPAddress.Parse("10.0.0.2"), IPAddress.Parse("9.0.0.3")], list.Take(2).Select(e => e.Address));
    }

    [Fact]
    public void The_sort_keeps_the_native_quirk_of_comparing_against_the_starting_entry()
    {
        // FillIPList compares each candidate with ipList[startingIdx] rather than the lowest so far, so it swaps in
        // the last smaller entry, not the smallest.
        var list = LocalAddressList.Build(
            [IPAddress.Parse("10.0.0.3"), IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.2")],
            1
        );

        Assert.Equal(
            [IPAddress.Parse("10.0.0.2"), IPAddress.Parse("10.0.0.1"), IPAddress.Parse("10.0.0.3")],
            list.Take(3).Select(e => e.Address)
        );
    }

    [Fact]
    public void At_most_ten_ipv4_addresses_are_kept_and_ipv6_ones_are_skipped()
    {
        // GetMyIP_Windows_Linux_IPV4 (RakNetSocket2_Windows_Linux.cpp:70-103), since RAKNET_SUPPORT_IPV6 is 0.
        IEnumerable<IPAddress> addresses =
        [
            IPAddress.IPv6Loopback,
            .. Enumerable.Range(1, 12).Select(i => new IPAddress([10, 0, 0, (byte)i])),
        ];

        var list = LocalAddressList.Build(addresses, 1);

        Assert.Equal(10, list.Count);
        Assert.All(list, e => Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, e.AddressFamily));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (byte)i).Order(), list.Select(e => e.Address.GetAddressBytes()[3]).Order());
    }

    [Fact]
    public void No_host_addresses_gives_an_unassigned_list()
    {
        var list = LocalAddressList.Build([], 1234);

        Assert.All(list, e => Assert.Equal(InternalAddressList.Unassigned, e));
    }
}
