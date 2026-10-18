using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OpenConnectionRequest2Tests
{
    // RakPeer.cpp:4758-4853 (client) and 5250-5253 (server): ID, aligned magic, [no security fields when the
    // server reported hasSecurity=0], the server's address as the client addressed it, MTU, client GUID.
    // 127.0.0.1:19132 serialises as 04, ~7f ~00 ~00 ~01, 4abc.
    private const string RequestHex = "07" + Codec.Magic + "0480fffffe4abc" + "05d4" + Codec.GuidHex;
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 19132);

    [Fact]
    public void Request_encodes_as_id_magic_address_mtu_guid()
    {
        Assert.Equal(RequestHex, Codec.EncodeHex(new OpenConnectionRequest2(Server, 1492, Codec.Guid)));
    }

    [Fact]
    public void Request_decodes_every_field()
    {
        var request = Codec.Decode<OpenConnectionRequest2>(RequestHex);

        Assert.Equal(Server, request.ServerAddress);
        Assert.Equal(1492, request.MtuSize);
        Assert.Equal(Codec.Guid, request.ClientGuid);
    }

    [Fact]
    public void Request_round_trips()
    {
        var request = new OpenConnectionRequest2(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 65535), 576, 3);

        Assert.True(Codec.TryDecode<OpenConnectionRequest2>(Codec.Encode(request), out var decoded));
        Assert.Equal(request, decoded);
    }

    [Fact]
    public void Truncated_request_fails()
    {
        Codec.AssertAllTruncationsFail<OpenConnectionRequest2>(Convert.FromHexString(RequestHex));
    }

    [Fact]
    public void Request_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<OpenConnectionRequest2>(Convert.FromHexString(RequestHex), MessageId.OpenConnectionRequest2);
    }

    [Fact]
    public void Request_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<OpenConnectionRequest2>(Convert.FromHexString(RequestHex), 1);
    }

    [Fact]
    public void Request_with_an_ipv6_address_fails()
    {
        var bytes = Convert.FromHexString(RequestHex);
        bytes[17] = 6;

        Assert.False(Codec.TryDecode<OpenConnectionRequest2>(bytes, out _));
    }
}
