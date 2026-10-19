using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// RakNetStatistics as the reliability layer fills it (ReliabilityLayer.cpp:3378-3415).
public class StatisticsTests
{
    [Fact]
    public void Queued_and_unacknowledged_messages_show_in_the_send_and_resend_buffers()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();

        // Without the server's acknowledgements the client's window fills and later sends wait in the send buffer.
        peers.Client.PerConnectionOutgoingBandwidthLimit = 8_000;
        var message = new byte[1_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 10; i++)
            server.Send(message, Reliability.ReliableOrdered, Priority.Medium);
        peers.Time.Advance(TimeSpan.FromMilliseconds(1));
        peers.Client.Update();

        var statistics = server.GetStatistics();
        Assert.True(statistics.MessagesInSendBuffer[Priority.Medium] > 0);
        Assert.True(statistics.BytesInSendBuffer[Priority.Medium] > 0);
        Assert.True(statistics.MessagesInResendBuffer > 0);
        Assert.True(statistics.BytesInResendBuffer > 0);
        Assert.Equal(0f, statistics.PacketLossTotal);
    }

    [Fact]
    public void Adding_statistics_sums_the_metrics_and_send_buffers_as_RakNet_does()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        var message = new byte[100];
        message[0] = (byte)MessageId.UserPacketEnum;
        server.Send(message, Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Data.Length == 100), "the message");

        var statistics = server.GetStatistics();
        var sum = statistics + statistics;

        Assert.Equal(2 * statistics.RunningTotal[RnsPerSecondMetric.UserMessageBytesSent], sum.RunningTotal[RnsPerSecondMetric.UserMessageBytesSent]);
        // RakNetStatistics::operator+= (RakNetStatistics.h:102-118) leaves every other field as the left operand's.
        Assert.Equal(statistics.MessagesInResendBuffer, sum.MessagesInResendBuffer);
        Assert.Equal(statistics.ConnectionStartTimeUs, sum.ConnectionStartTimeUs);
    }
}
