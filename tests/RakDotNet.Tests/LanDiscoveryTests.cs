using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

/// <summary>Samples/LANServerDiscovery/LANServerDiscovery.cpp: a client finds servers by pinging 255.255.255.255.</summary>
public class LanDiscoveryTests
{
    [Fact]
    public void A_ping_to_the_broadcast_address_is_answered_by_a_server_on_that_port()
    {
        // LANServerDiscovery.cpp pings "255.255.255.255" on the server's port; the ping leaves the client because every
        // socket is bound with SO_BROADCAST (RakPeer.cpp:505, RakNetSocket2_Berkley.cpp:176).
        Broadcast.SkipUnlessAvailable();
        using var peers = new ManualPeers(
            server: o => o with { Bindings = [SocketBinding.AnyIPv4] },
            client: o => o with { Bindings = [SocketBinding.AnyIPv4] });
        peers.Server.OfflinePingResponse = "Offline ping response data\0"u8.ToArray();

        Assert.True(peers.Client.PingUnconnected(new SystemAddress(IPAddress.Broadcast, peers.ServerAddress.Port)));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.UnconnectedPong), "a pong");

        var pong = peers.ClientEvents.Single(e => e.Kind == PeerEventKind.UnconnectedPong);
        Assert.Equal(peers.Server.RakNetGuid, pong.RakNetGuid);
        Assert.Equal(peers.ServerAddress.Port, pong.Address.Port);
        Assert.Equal("Offline ping response data\0"u8.ToArray(), pong.Data[5..]);
    }
}
