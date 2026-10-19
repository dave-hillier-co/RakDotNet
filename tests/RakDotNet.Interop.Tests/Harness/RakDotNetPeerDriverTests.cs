using System.Net;
using System.Text.RegularExpressions;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class RakDotNetPeerDriverTests
{
    private static readonly SystemAddress Remote = new(IPAddress.Loopback, 61234);

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
    public void A_packet_is_reported_with_its_sender_and_data()
    {
        // RakPeer.cpp:4693-4699: a pong is the ID, the time as a 4-byte big-endian TimeMS, then the response.
        byte[] pong = [(byte)MessageId.UnconnectedPong, 0, 0, 1, 2, .. "hello"u8];

        var packet = RakDotNetPeerDriver.ToReceivedPacket(pong, new RakNetGuid(42), Remote, timeMs: 7);

        Assert.Equal((byte)MessageId.UnconnectedPong, packet.MessageId);
        Assert.Equal("ID_UNCONNECTED_PONG", packet.MessageName);
        Assert.Equal(42UL, packet.PeerGuid);
        Assert.Equal(Remote.ToIPEndPoint(), packet.Address);
        Assert.Equal(pong, packet.Data.ToArray());
        Assert.Equal(7, packet.TimeMs);
        Assert.Null(packet.Timestamp);
    }

    [Fact]
    public void A_timestamped_message_is_reported_by_the_id_after_the_timestamp()
    {
        byte[] data = [(byte)MessageId.Timestamp, 0, 0, 0, 0, 0, 0, 0x30, 0x39, (byte)MessageId.UserPacketEnum + 2, 0xff];

        var packet = RakDotNetPeerDriver.ToReceivedPacket(data, new RakNetGuid(1), Remote, timeMs: 0);

        Assert.Equal((byte)MessageId.UserPacketEnum + 2, packet.MessageId);
        Assert.Equal("ID_USER_PACKET_ENUM+2", packet.MessageName);
        Assert.Equal(12345UL, packet.Timestamp);
        Assert.Equal(data, packet.Data.ToArray());
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
    public async Task Startup_on_a_port_in_use_fails_with_RakNets_result_name()
    {
        await using var first = RakDotNetPeerDriver.Launch();
        var taken = await first.StartupAsync(new StartupOptions(), Token);
        await using var second = RakDotNetPeerDriver.Launch();

        var error = await Assert.ThrowsAsync<PeerCommandException>(() => second.StartupAsync(new StartupOptions(Port: taken.BoundAddress.Port), Token));

        Assert.Contains("SOCKET_PORT_ALREADY_IN_USE", error.Message, StringComparison.Ordinal);
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
        Assert.Equal(0u, await driver.SendAsync(new SendOptions(RakNetGuid.Unassigned.Value, new byte[] { 0x86 }), Token));
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

    [GeneratedRegex(@"X\((ID_[A-Za-z0-9_]+)\)")]
    private static partial Regex NativeNamePattern();
}
