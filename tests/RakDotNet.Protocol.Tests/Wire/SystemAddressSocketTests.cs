using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

public class SystemAddressSocketTests
{
    [Fact]
    public void An_IPv4_socket_address_converts_to_the_same_address_and_port()
    {
        var socketAddress = new IPEndPoint(IPAddress.Parse("192.0.2.17"), 61234).Serialize();

        var address = SystemAddress.FromSocketAddress(socketAddress);

        Assert.Equal(new SystemAddress(IPAddress.Parse("192.0.2.17"), 61234), address);
    }

    [Fact]
    public void An_IPv6_socket_address_converts_with_its_scope()
    {
        var endPoint = new IPEndPoint(IPAddress.Parse("fe80::1%3"), 19132);

        var address = SystemAddress.FromSocketAddress(endPoint.Serialize());

        Assert.Equal(AddressFamily.InterNetworkV6, address.AddressFamily);
        Assert.Equal(endPoint, address.ToIPEndPoint());
    }

    [Fact]
    public void Copying_into_a_socket_address_gives_what_the_runtime_would_serialise()
    {
        var address = new SystemAddress(IPAddress.Parse("198.51.100.4"), 7777);
        var destination = new SocketAddress(AddressFamily.InterNetwork);

        address.CopyTo(destination);

        Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.4"), 7777).Serialize(), destination);
    }

    [Fact]
    public void Copying_an_IPv6_address_round_trips_through_the_socket_address()
    {
        var address = (SystemAddress)new IPEndPoint(IPAddress.Parse("2001:db8::5"), 4000);
        var destination = new SocketAddress(AddressFamily.InterNetworkV6);

        address.CopyTo(destination);

        Assert.Equal(address, SystemAddress.FromSocketAddress(destination));
    }

    [Fact]
    public void Copying_into_a_socket_address_of_the_other_family_throws()
    {
        var address = new SystemAddress(IPAddress.Loopback, 1);

        Assert.Throws<ArgumentException>(() => address.CopyTo(new SocketAddress(AddressFamily.InterNetworkV6)));
    }

    [Fact]
    public void An_IPv4_address_is_written_as_version_then_inverted_bytes_then_port()
    {
        // BitStream::Write(SystemAddress), BitStream.h: version 4, ~s_addr in network order, then the port big-endian.
        Span<byte> buffer = stackalloc byte[7];
        var writer = new BitStreamWriter(buffer);

        writer.WriteAddress(new SystemAddress(IPAddress.Parse("127.0.0.1"), 0x1234));

        Assert.Equal("0480fffffe1234", Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void A_written_IPv4_address_reads_back()
    {
        Span<byte> buffer = stackalloc byte[7];
        var writer = new BitStreamWriter(buffer);
        var address = new SystemAddress(IPAddress.Parse("10.1.2.3"), 19132);
        writer.WriteAddress(address);

        var reader = new BitStreamReader(writer.Written);

        Assert.True(reader.TryReadSystemAddress(out var read));
        Assert.Equal(address, read);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void A_truncated_or_unsupported_address_does_not_read_and_does_not_move()
    {
        var truncated = new BitStreamReader([0x04, 0x80, 0xff]);
        Assert.False(truncated.TryReadSystemAddress(out _));
        Assert.Equal(0, truncated.BitPosition);

        var ipv6 = new BitStreamReader([0x06, 0, 0, 0, 0, 0, 0]);
        Assert.False(ipv6.TryReadSystemAddress(out _));
        Assert.Equal(0, ipv6.BitPosition);
    }
}
