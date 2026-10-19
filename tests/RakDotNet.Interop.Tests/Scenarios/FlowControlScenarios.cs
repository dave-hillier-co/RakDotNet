using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Bursts, congestion control and the bandwidth limit, from Samples/BurstTest/BurstTest.cpp (a burst of equal messages
/// carrying their size, index and count) and Samples/Flow Control Test/FlowControlTest.cpp (sending at a rate and
/// watching RakNetStatistics).
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class FlowControlScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_burst_of_messages_larger_than_the_mtu_arrives_complete_and_in_order(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        // BurstTest.cpp:98-110: the defaults, 128 messages of 4096 bytes.
        await BurstAsync(session, size: 4096, count: 128);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_burst_of_messages_below_the_mtu_arrives_complete_and_in_order(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await BurstAsync(session, size: 512, count: 1000);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_outgoing_bandwidth_limit_caps_the_send_rate(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);
        const uint bitsPerSecond = 400_000;
        const int bytesPerSecond = (int)bitsPerSecond / 8;
        const int messageSize = 1000;
        const int count = 150;
        await session.Client.SetOutgoingBandwidthLimitAsync(bitsPerSecond, Token);

        for (var index = 0; index < count; index++)
        {
            await session.Client.SendAsync(new SendOptions(serverGuid, BurstMessage(messageSize, index, count), Reliability.ReliableOrdered, Priority.Medium), Token);
        }
        // ReliabilityLayer.cpp:1995-1999: datagrams are filled only while fewer bytes than the limit were sent in the
        // last second, and the statistics say so.
        var limited = false;
        PeerStatistics stats;
        do
        {
            stats = await session.Client.GetStatisticsAsync(serverGuid, Token);
            limited |= stats.IsLimitedByOutgoingBandwidthLimit;
        }
        // Sends reach the reliability layer on the peer's update thread (RakPeer::SendBuffered), so the bytes pushed
        // grow after the sends return; the loop waits for all of them before trusting pushed minus sent.
        while (!limited
            && (stats.RunningTotal[PerSecondMetric.UserMessageBytesPushed] < (ulong)(count * messageSize)
                || stats.MessagesInResendBuffer + (int)(stats.RunningTotal[PerSecondMetric.UserMessageBytesPushed] - stats.RunningTotal[PerSecondMetric.UserMessageBytesSent]) > 0));
        var received = await session.ServerInbox.WaitForPacketsAsync(
            p => p.MessageId == ScenarioFlows.UserPacket, count, $"{count} messages", TimeSpan.FromSeconds(20), Token);

        Assert.True(limited, "The sender never reported being limited by its outgoing bandwidth limit.");
        Assert.Equal((ulong)bytesPerSecond, stats.BpsLimitByOutgoingBandwidthLimit);
        Assert.Equal(Enumerable.Range(0, count), received.Select(p => (int)BinaryPrimitives.ReadUInt32BigEndian(p.Data.Span[5..])));
        // At most one second's worth goes out in any second, so the last message cannot arrive sooner than this after
        // the first, however fast the link.
        var span = TimeSpan.FromMilliseconds(received[^1].TimeMs - received[0].TimeMs);
        var floor = TimeSpan.FromSeconds((count * messageSize / (double)bytesPerSecond) - 1.5);
        TestContext.Current.TestOutputHelper?.WriteLine($"{count * messageSize} bytes at {bytesPerSecond} B/s arrived over {span.TotalMilliseconds} ms.");
        Assert.True(span >= floor, $"{count * messageSize} bytes arrived over {span.TotalMilliseconds} ms, faster than a {bytesPerSecond} B/s limit allows.");
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Statistics_account_for_bytes_pushed_sent_resent_and_received_under_loss(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var before = await session.Client.GetStatisticsAsync(serverGuid, Token);
        var receivedBefore = await session.Server.GetStatisticsAsync(clientGuid, Token);
        session.Proxy.Faults = new FaultOptions(Seed: 20261021, LossProbability: 0.15);
        const int messageSize = 1000;
        const int count = 100;

        for (var index = 0; index < count; index++)
        {
            await session.Client.SendAsync(new SendOptions(serverGuid, BurstMessage(messageSize, index, count), Reliability.ReliableOrdered), Token);
        }
        await session.ServerInbox.WaitForPacketsAsync(p => p.MessageId == ScenarioFlows.UserPacket, count, $"{count} messages", TimeSpan.FromSeconds(30), Token);
        var after = await WaitForEmptyResendBufferAsync(session.Client, serverGuid);
        var receivedAfter = await session.Server.GetStatisticsAsync(clientGuid, Token);

        ulong Delta(PeerStatistics from, PeerStatistics to, PerSecondMetric metric) => to.RunningTotal[metric] - from.RunningTotal[metric];
        const ulong userBytes = count * messageSize;
        // Internal messages (connected pings) count too, so every total is at least the user bytes.
        Assert.InRange(Delta(before, after, PerSecondMetric.UserMessageBytesPushed), userBytes, userBytes + 1000);
        Assert.InRange(Delta(before, after, PerSecondMetric.UserMessageBytesSent), userBytes, userBytes + 1000);
        Assert.True(Delta(before, after, PerSecondMetric.UserMessageBytesResent) > 0, "Nothing was resent under 15% loss.");
        Assert.True(Delta(before, after, PerSecondMetric.ActualBytesSent) > Delta(before, after, PerSecondMetric.UserMessageBytesSent), "Datagrams carry headers, so more bytes go out than user bytes.");
        Assert.InRange(Delta(receivedBefore, receivedAfter, PerSecondMetric.UserMessageBytesReceivedProcessed), userBytes, userBytes + 1000);
        Assert.True(Delta(receivedBefore, receivedAfter, PerSecondMetric.UserMessageBytesReceivedIgnored) > 0, "Resends of datagrams that had arrived are ignored as duplicates.");
        // ReliabilityLayer.cpp:3398-3406 tests SENT / (SENT + RESENT) > 0 in integer arithmetic, which fails whenever
        // anything was resent, so the total loss reported stays 0.
        Assert.Equal(0, after.PacketLossTotal);
    }

    /// <summary>
    /// BurstTest.cpp:100-110: each message is the ID, then its size, index and the count as BitStream (big-endian)
    /// u32s, padded with zeros to the size.
    /// </summary>
    private static byte[] BurstMessage(int size, int index, int count)
    {
        var data = new byte[size];
        data[0] = ScenarioFlows.UserPacket;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(1), (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(5), (uint)index);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(9), (uint)count);
        return data;
    }

    // BurstTest.cpp:98-110 sends at MEDIUM_PRIORITY, RELIABLE_ORDERED on channel 0; BurstTest.cpp:135-148 checks each
    // message's length against its size field.
    private static async Task BurstAsync(InteropSession session, int size, int count)
    {
        var (serverGuid, _) = await session.ConnectAsync(Token);
        for (var index = 0; index < count; index++)
        {
            await session.Client.SendAsync(new SendOptions(serverGuid, BurstMessage(size, index, count), Reliability.ReliableOrdered, Priority.Medium), Token);
        }

        var received = await session.ServerInbox.WaitForPacketsAsync(
            p => p.MessageId == ScenarioFlows.UserPacket, count, $"{count} messages of {size} bytes", TimeSpan.FromSeconds(30), Token);
        Assert.All(received, p =>
        {
            Assert.Equal(size, p.Data.Length);
            Assert.Equal((uint)size, BinaryPrimitives.ReadUInt32BigEndian(p.Data.Span[1..]));
            Assert.Equal((uint)count, BinaryPrimitives.ReadUInt32BigEndian(p.Data.Span[9..]));
        });
        Assert.Equal(Enumerable.Range(0, count), received.Select(p => (int)BinaryPrimitives.ReadUInt32BigEndian(p.Data.Span[5..])));
    }

    private static async Task<PeerStatistics> WaitForEmptyResendBufferAsync(IPeerDriver driver, ulong target)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(InteropSession.DefaultTimeout);
        while (true)
        {
            var stats = await driver.GetStatisticsAsync(target, Token);
            if (stats.MessagesInResendBuffer == 0)
            {
                return stats;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }
}
