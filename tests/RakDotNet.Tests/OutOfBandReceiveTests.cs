using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// What a peer reports for out-of-band datagrams (RakPeer.cpp:4706-4730), and ChangeSystemAddress (RakPeer.cpp:2846-2856,
// 5646-5654).
public class OutOfBandReceiveTests
{
    [Fact]
    public void An_advertisement_arrives_as_an_AdvertiseSystem_event()
    {
        using var peers = new ManualPeers();

        Assert.True(peers.Client.AdvertiseSystem(peers.ServerAddress, [1, 2, 3]));
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.AdvertiseSystem), "the advertisement");

        var advertised = peers.ServerEvents.Single(e => e.Kind == PeerEventKind.AdvertiseSystem);
        Assert.Equal([(byte)MessageId.AdvertiseSystem, 1, 2, 3], advertised.Data);
        Assert.Equal(peers.Client.RakNetGuid, advertised.RakNetGuid);
        Assert.Equal(peers.Client.Sockets[0].LocalAddress, advertised.Address);
    }

    [Fact]
    public void Out_of_band_data_arrives_after_ID_OUT_OF_BAND_INTERNAL()
    {
        using var peers = new ManualPeers();

        Assert.True(peers.Client.LowLevel.SendOutOfBand(peers.ServerAddress, [(byte)MessageId.UserPacketEnum, 9]));
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.OutOfBand), "the data");

        Assert.Equal([(byte)MessageId.OutOfBandInternal, (byte)MessageId.UserPacketEnum, 9], peers.ServerEvents.Single(e => e.Kind == PeerEventKind.OutOfBand).Data);
    }

    [Fact]
    public void ChangeSystemAddress_moves_a_connection_to_the_new_address()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        var moved = new SystemAddress(IPAddress.Loopback, 9);

        peers.Client.LowLevel.ChangeSystemAddress(server.RakNetGuid, moved);
        peers.Step();

        Assert.Equal(moved, server.Address);
        Assert.True(peers.Client.TryGetConnection(moved, out var found));
        Assert.Same(server, found);
        Assert.False(peers.Client.TryGetConnection(peers.ServerAddress, out _));
        Assert.Equal(moved, peers.Client.GetAddressFromGuid(server.RakNetGuid));
    }
}
