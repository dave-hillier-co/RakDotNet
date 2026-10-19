using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// The RakPeer setters that apply while the peer runs (RakPeer.cpp:2218-2221, 2524-2547, 2666-2670, 2697-2725,
// 2961-2964, and SetIncomingPassword), and the start options that set them.
public class RuntimeSettingsTests
{
    [Fact]
    public void Every_runtime_setting_can_be_given_at_start_and_read_back()
    {
        using var peers = new ManualPeers(server: o => o with
        {
            UnreliableTimeout = TimeSpan.FromMilliseconds(250),
            OccasionalPing = true,
            SplitMessageProgressInterval = 4,
            LimitIpConnectionFrequency = true,
            AllowConnectionResponseIpMigration = true,
            PerConnectionOutgoingBandwidthLimit = 64_000,
            IncomingPassword = "pw"u8.ToArray(),
            DefaultTimeout = TimeSpan.FromSeconds(3),
        });

        Assert.Equal(TimeSpan.FromMilliseconds(250), peers.Server.UnreliableTimeout);
        Assert.True(peers.Server.OccasionalPing);
        Assert.Equal(4, peers.Server.SplitMessageProgressInterval);
        Assert.True(peers.Server.LimitIpConnectionFrequency);
        Assert.True(peers.Server.AllowConnectionResponseIpMigration);
        Assert.Equal(64_000, peers.Server.PerConnectionOutgoingBandwidthLimit);
        Assert.Equal("pw"u8.ToArray(), peers.Server.IncomingPassword.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(3), peers.Server.DefaultTimeout);
    }

    [Fact]
    public void Every_runtime_setting_can_be_changed_while_the_peer_runs()
    {
        using var peers = new ManualPeers();
        var server = peers.Server;

        server.UnreliableTimeout = TimeSpan.FromMilliseconds(300);
        server.OccasionalPing = true;
        server.SplitMessageProgressInterval = 2;
        server.LimitIpConnectionFrequency = true;
        server.AllowConnectionResponseIpMigration = true;
        server.PerConnectionOutgoingBandwidthLimit = 1_000_000;
        server.IncomingPassword = "pw"u8.ToArray();
        server.DefaultTimeout = TimeSpan.FromSeconds(4);

        Assert.Equal(TimeSpan.FromMilliseconds(300), server.UnreliableTimeout);
        Assert.True(server.OccasionalPing);
        Assert.Equal(2, server.SplitMessageProgressInterval);
        Assert.True(server.LimitIpConnectionFrequency);
        Assert.True(server.AllowConnectionResponseIpMigration);
        Assert.Equal(1_000_000, server.PerConnectionOutgoingBandwidthLimit);
        Assert.Equal("pw"u8.ToArray(), server.IncomingPassword.ToArray());
        Assert.Equal(TimeSpan.FromSeconds(4), server.DefaultTimeout);
    }

    [Fact]
    public void Settings_RakNet_rejects_throw()
    {
        using var peers = new ManualPeers();

        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Server.UnreliableTimeout = TimeSpan.FromMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Server.SplitMessageProgressInterval = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Server.PerConnectionOutgoingBandwidthLimit = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => peers.Server.DefaultTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => peers.Server.IncomingPassword = new byte[256]);
    }

    [Fact]
    public void A_password_set_while_running_is_required_of_later_connections()
    {
        using var peers = new ManualPeers();
        peers.Server.IncomingPassword = Encoding.ASCII.GetBytes("secret");
        peers.Step();

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ConnectFailed), "the refusal");

        Assert.Equal(ConnectFailure.InvalidPassword, peers.ClientEvents.Single(e => e.Kind == PeerEventKind.ConnectFailed).ConnectFailure);
    }

    [Fact]
    public void DefaultTimeout_set_while_running_changes_every_live_connection()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        peers.Client.DefaultTimeout = TimeSpan.FromSeconds(3);
        peers.Step();

        // RakPeer.cpp:2526-2538: the default and every connection.
        Assert.Equal(TimeSpan.FromSeconds(3), server.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(3), peers.Client.DefaultTimeout);
    }

    [Fact]
    public void OccasionalPing_keeps_measuring_the_round_trip_every_five_seconds()
    {
        using var peers = new ManualPeers(client: o => o with { DefaultTimeout = TimeSpan.FromSeconds(60) });
        var server = peers.Connect();
        peers.Settle();
        var handshakePing = server.LastPing;

        // Without occasional pings RakNet stops pinging after the first pong (RakPeer.cpp:5924).
        for (var i = 0; i < 70; i++)
            peers.Step(TimeSpan.FromMilliseconds(100));
        Assert.Equal(handshakePing, server.LastPing);

        peers.Client.OccasionalPing = true;
        for (var i = 0; i < 70; i++)
        {
            peers.Step(TimeSpan.FromMilliseconds(100));
            // Loopback datagrams reach the other socket on the wall clock, not the fake one.
            Thread.Sleep(2);
        }

        // Each hop now takes at least one 100 ms step.
        Assert.InRange(server.LastPing!.Value.TotalMilliseconds, 100, 400);
    }

    [Fact]
    public void The_outgoing_bandwidth_limit_holds_back_data_and_shows_in_the_statistics()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();

        peers.Client.PerConnectionOutgoingBandwidthLimit = 8_000;
        var message = new byte[1_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 20; i++)
            server.Send(message, Reliability.ReliableOrdered);
        for (var i = 0; i < 50; i++)
            peers.Step(TimeSpan.FromMilliseconds(10));

        var statistics = server.GetStatistics();
        Assert.True(statistics.IsLimitedByOutgoingBandwidthLimit);
        Assert.Equal(1_000UL, statistics.BpsLimitByOutgoingBandwidthLimit);
        Assert.InRange(peers.ServerEvents.Count(e => e.Data.Length == message.Length), 1, 5);
    }

    [Fact]
    public void An_unreliable_receipt_discarded_by_the_unreliable_timeout_ends_as_abandoned()
    {
        using var peers = new ManualPeers(client: o => o with { UnreliableTimeout = TimeSpan.FromMilliseconds(50) });
        var server = peers.Connect();
        peers.Settle();

        // The bandwidth limit holds the queue back long enough for the unreliable timeout to cull the receipt's message
        // unsent (ReliabilityLayer.cpp:1745-1793); RakNet reports nothing for it, so the receipt is abandoned.
        peers.Client.PerConnectionOutgoingBandwidthLimit = 8_000;
        var message = new byte[1_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 10; i++)
            server.Send(message, Reliability.ReliableOrdered);
        var receipt = server.Send(message, Reliability.UnreliableWithAckReceipt, Priority.Low).Receipt;
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Receipt == receipt), "the receipt's end");

        var ended = peers.ClientEvents.Single(e => e.Receipt == receipt);
        Assert.Equal(PeerEventKind.ReceiptAbandoned, ended.Kind);
        Assert.Empty(ended.Data);
    }
}
