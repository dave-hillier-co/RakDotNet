using System.Net;
using System.Threading.Channels;

namespace RakDotNet.Interop.Tests.Harness;

public sealed class PacketInboxTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly Channel<ReceivedPacket> _channel = Channel.CreateUnbounded<ReceivedPacket>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ReceivedPacket Packet(byte id, string name, params byte[] rest) =>
        new(id, name, 1, new IPEndPoint(IPAddress.Loopback, 1), (byte[])[id, .. rest], 0, null);

    [Fact]
    public async Task Waiting_returns_the_first_matching_packet_and_keeps_the_others_for_later_waits()
    {
        using var inbox = new PacketInbox(_channel.Reader, "server");
        _channel.Writer.TryWrite(Packet(19, "ID_NEW_INCOMING_CONNECTION"));
        _channel.Writer.TryWrite(Packet(134, "ID_USER_PACKET_ENUM", 1));

        var user = await inbox.WaitForPacketAsync(p => p.MessageId == 134, "a user packet", Timeout, Token);
        var connection = await inbox.WaitForPacketAsync(p => p.MessageId == 19, "a new connection", Timeout, Token);

        Assert.Equal(134, user.MessageId);
        Assert.Equal(19, connection.MessageId);
        Assert.Empty(inbox.Unconsumed);
    }

    [Fact]
    public async Task Waiting_sees_packets_that_arrive_after_it_starts()
    {
        using var inbox = new PacketInbox(_channel.Reader, "client");

        var waiting = inbox.WaitForPacketAsync(p => p.MessageId == 16, "accepted", Timeout, Token);
        _channel.Writer.TryWrite(Packet(16, "ID_CONNECTION_REQUEST_ACCEPTED"));

        Assert.Equal(16, (await waiting).MessageId);
    }

    [Fact]
    public async Task A_timeout_names_the_expectation_and_the_packets_seen_instead()
    {
        using var inbox = new PacketInbox(_channel.Reader, "client");
        _channel.Writer.TryWrite(Packet(17, "ID_CONNECTION_ATTEMPT_FAILED"));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => inbox.WaitForPacketAsync(p => p.MessageId == 16, "ID_CONNECTION_REQUEST_ACCEPTED", TimeSpan.FromMilliseconds(100), Token));

        Assert.Contains("client", error.Message, StringComparison.Ordinal);
        Assert.Contains("ID_CONNECTION_REQUEST_ACCEPTED", error.Message, StringComparison.Ordinal);
        Assert.Contains("ID_CONNECTION_ATTEMPT_FAILED", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_completed_channel_fails_the_wait_without_waiting_for_the_timeout()
    {
        using var inbox = new PacketInbox(_channel.Reader, "server");
        _channel.Writer.Complete();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => inbox.WaitForPacketAsync(_ => true, "anything", Timeout, Token));

        Assert.Contains("anything", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Waiting_for_a_count_collects_matching_packets_in_arrival_order()
    {
        using var inbox = new PacketInbox(_channel.Reader, "server");
        for (byte i = 0; i < 5; i++)
        {
            _channel.Writer.TryWrite(Packet(134, "ID_USER_PACKET_ENUM", i));
            _channel.Writer.TryWrite(Packet(3, "ID_CONNECTED_PONG"));
        }

        var packets = await inbox.WaitForPacketsAsync(p => p.MessageId == 134, 5, "user packets", Timeout, Token);

        Assert.Equal(new byte[] { 0, 1, 2, 3, 4 }, packets.Select(p => p.Data.Span[1]));
        var pongs = await inbox.WaitForPacketsAsync(p => p.MessageId == 3, 5, "pongs skipped over earlier", Timeout, Token);
        Assert.Equal(5, pongs.Count);
    }

    [Fact]
    public async Task Waiting_until_a_condition_collects_matching_packets_until_the_condition_holds()
    {
        using var inbox = new PacketInbox(_channel.Reader, "server");
        for (byte i = 0; i < 6; i++)
        {
            _channel.Writer.TryWrite(Packet(134, "ID_USER_PACKET_ENUM", i));
            _channel.Writer.TryWrite(Packet(3, "ID_CONNECTED_PONG"));
        }

        var packets = await inbox.WaitForPacketsAsync(
            p => p.MessageId == 134, found => found[^1].Data.Span[1] == 3, "user packets up to 3", Timeout, Token);

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, packets.Select(p => p.Data.Span[1]));
        Assert.Equal(4, (await inbox.WaitForPacketAsync(p => p.MessageId == 134, "the next user packet", Timeout, Token)).Data.Span[1]);
    }

    [Fact]
    public async Task A_count_timeout_says_how_many_arrived()
    {
        using var inbox = new PacketInbox(_channel.Reader, "server");
        _channel.Writer.TryWrite(Packet(134, "ID_USER_PACKET_ENUM", 0));

        var error = await Assert.ThrowsAsync<TimeoutException>(
            () => inbox.WaitForPacketsAsync(p => p.MessageId == 134, 3, "user packets", TimeSpan.FromMilliseconds(100), Token));

        Assert.Contains("1 of 3", error.Message, StringComparison.Ordinal);
    }
}
