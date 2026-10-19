using System.Net;
using System.Text.RegularExpressions;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class RakDotNetPeerDriverTests
{
    private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 61234);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Message_names_match_the_native_peer_table()
    {
        var source = File.ReadAllText(Path.Combine(RepoPaths.FindRepoRoot(), "tools", "interop-peer", "src", "Names.cpp"));
        var native = NativeNamePattern().Matches(source).Select(m => m.Groups[1].Value).ToList();

        Assert.Equal((int)MessageId.UserPacketEnum, native.Count);
        Assert.Equal(native, Enumerable.Range(0, native.Count).Select(id => MessageNames.Of((byte)id)));
        Assert.Equal("ID_USER_PACKET_ENUM+0", MessageNames.Of((byte)MessageId.UserPacketEnum));
        Assert.Equal("ID_USER_PACKET_ENUM+121", MessageNames.Of(255));
    }

    [Fact]
    public void A_pong_is_reported_with_RakNets_rewrite_and_the_servers_guid()
    {
        var pong = new UnconnectedPongReceived(Remote, 42, SendPingTime: 0x1_0000_0102, "hello"u8.ToArray());

        var packet = RakDotNetPeerDriver.ToReceivedPacket(pong, timeMs: 7);

        Assert.Equal((byte)MessageId.UnconnectedPong, packet.MessageId);
        Assert.Equal("ID_UNCONNECTED_PONG", packet.MessageName);
        Assert.Equal(42UL, packet.PeerGuid);
        Assert.Equal(Remote, packet.Address);
        // RakPeer.cpp:4693-4699: the ID, the time truncated to a 4-byte big-endian TimeMS, then the response.
        Assert.Equal("1c00000102" + Convert.ToHexStringLower("hello"u8), Convert.ToHexStringLower(packet.Data.Span));
        Assert.Equal(7, packet.TimeMs);
        Assert.Null(packet.Timestamp);
    }

    [Fact]
    public void A_timestamped_message_is_reported_by_the_id_after_the_timestamp()
    {
        byte[] data = [(byte)MessageId.Timestamp, 0, 0, 0, 0, 0, 0, 0x30, 0x39, (byte)MessageId.UserPacketEnum + 2, 0xff];

        var packet = RakDotNetPeerDriver.ToReceivedPacket(new MessageReceived(Remote, 1, data), timeMs: 0);

        Assert.Equal((byte)MessageId.UserPacketEnum + 2, packet.MessageId);
        Assert.Equal("ID_USER_PACKET_ENUM+2", packet.MessageName);
        Assert.Equal(12345UL, packet.Timestamp);
        Assert.Equal(data, packet.Data.ToArray());
    }

    [Fact]
    public void A_send_receipt_is_reported_as_ID_SND_RECEIPT_ACKED_with_the_serial_in_host_order()
    {
        var packet = RakDotNetPeerDriver.ToReceivedPacket(new SendReceiptAcked(Remote, 1, Receipt: 0x01020304), timeMs: 0);

        Assert.Equal("ID_SND_RECEIPT_ACKED", packet.MessageName);
        Assert.Equal("0e04030201", Convert.ToHexStringLower(packet.Data.Span));
    }

    [Fact]
    public async Task Startup_reports_the_bound_address_and_guid_and_refuses_a_second_startup()
    {
        await using var driver = RakDotNetPeerDriver.Launch();

        var started = await driver.StartupAsync(new StartupOptions(), Token);

        Assert.Equal("rakdotnet", driver.Kind);
        Assert.Equal(IPAddress.Loopback, started.BoundAddress.Address);
        Assert.NotEqual(0, started.BoundAddress.Port);
        var error = await Assert.ThrowsAsync<PeerCommandException>(() => driver.StartupAsync(new StartupOptions(), Token));
        Assert.Equal("startup", error.Command);
    }

    [Fact]
    public async Task Commands_fail_like_interop_peer_on_bad_input()
    {
        await using var driver = RakDotNetPeerDriver.Launch();
        await driver.StartupAsync(new StartupOptions(), Token);

        await Assert.ThrowsAsync<PeerCommandException>(() => driver.SendAsync(new SendOptions(1, ReadOnlyMemory<byte>.Empty), Token));
        await Assert.ThrowsAsync<PeerCommandException>(() => driver.SetOfflinePingResponseAsync(new byte[400], Token));
    }

    [Fact]
    public async Task Sends_to_an_unknown_guid_still_use_a_receipt_and_the_state_is_not_connected()
    {
        await using var driver = RakDotNetPeerDriver.Launch();
        await driver.StartupAsync(new StartupOptions(), Token);

        Assert.Equal(1u, await driver.SendAsync(new SendOptions(1234, new byte[] { 0x86 }), Token));
        Assert.Equal(2u, await driver.SendAsync(new SendOptions(1234, new byte[] { 0x86 }), Token));
        Assert.Equal("IS_NOT_CONNECTED", await driver.GetConnectionStateAsync(1234, Token));
    }

    [Fact]
    public async Task Shutdown_ends_the_packet_stream()
    {
        await using var driver = RakDotNetPeerDriver.Launch();
        await driver.StartupAsync(new StartupOptions(), Token);

        await driver.ShutdownAsync(cancellationToken: Token);

        await driver.Packets.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    [Fact]
    public async Task Commands_the_peer_core_cannot_reach_yet_throw_not_supported_so_the_matrix_skips_them()
    {
        await using var driver = RakDotNetPeerDriver.Launch();
        await driver.StartupAsync(new StartupOptions(), Token);

        Func<Task>[] commands =
        [
            () => driver.SendListAsync(new SendListOptions(1, [new byte[] { 0x86 }]), Token),
            () => driver.CancelConnectionAttemptAsync(Remote, Token),
            () => driver.PingConnectedAsync(1, Token),
            () => driver.AdvertiseSystemAsync(Remote, ReadOnlyMemory<byte>.Empty, Token),
            () => driver.SetTimeoutAsync(TimeSpan.FromSeconds(1), cancellationToken: Token),
            () => driver.SetMaximumIncomingConnectionsAsync(1, Token),
            () => driver.SetOccasionalPingAsync(true, Token),
            () => driver.SetSplitMessageProgressIntervalAsync(1, Token),
            () => driver.SetOutgoingBandwidthLimitAsync(1, Token),
            () => driver.BanAsync("127.0.0.1", TimeSpan.Zero, Token),
            () => driver.GetStatisticsAsync(1, Token),
            () => driver.GetInfoAsync(Token),
            () => driver.GetTimeAsync(Token),
        ];

        foreach (var command in commands)
        {
            await Assert.ThrowsAsync<NotSupportedException>(command);
        }
    }

    [GeneratedRegex(@"X\((ID_[A-Za-z0-9_]+)\)")]
    private static partial Regex NativeNamePattern();
}
