using System.Net;

namespace RakDotNet.Tests;

// RakPeer's ban list (RakPeer.cpp:1861-2049): patterns with * per octet, expiry on the peer's clock, and
// ID_CONNECTION_BANNED for every datagram from a banned address (RakPeer.cpp:4550-4580).
public class BanListTests
{
    [Fact]
    public void A_banned_address_is_refused_with_ID_CONNECTION_BANNED()
    {
        using var peers = new ManualPeers();
        peers.Server.BanList.Add("127.0.0.*");

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ConnectFailed), "the refusal");

        Assert.Equal(ConnectFailure.ConnectionBanned, peers.ClientEvents.Single(e => e.Kind == PeerEventKind.ConnectFailed).ConnectFailure);
    }

    [Fact]
    public void Bans_can_be_queried_removed_and_cleared()
    {
        using var peers = new ManualPeers();
        var bans = peers.Server.BanList;

        bans.Add("10.0.*");
        bans.Add("192.168.1.7");

        Assert.True(bans.IsBanned("10.0.3.4"));
        Assert.True(bans.IsBanned(IPAddress.Parse("192.168.1.7")));
        Assert.False(bans.IsBanned("10.1.3.4"));
        Assert.True(bans.Remove("10.0.*"));
        Assert.False(bans.Remove("10.0.*"));
        Assert.False(bans.IsBanned("10.0.3.4"));
        bans.Clear();
        Assert.False(bans.IsBanned("192.168.1.7"));
    }

    [Fact]
    public void A_timed_ban_expires_on_the_peers_clock()
    {
        using var peers = new ManualPeers();
        peers.Server.BanList.Add("10.0.0.1", TimeSpan.FromSeconds(1));

        peers.Time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.True(peers.Server.BanList.IsBanned("10.0.0.1"));
        peers.Time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(peers.Server.BanList.IsBanned("10.0.0.1"));
    }

    [Fact]
    public void A_ban_with_a_negative_duration_or_a_null_pattern_throws()
    {
        using var peers = new ManualPeers();

        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Server.BanList.Add("10.0.0.1", TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentNullException>(() => peers.Server.BanList.Add(null!));
    }
}
