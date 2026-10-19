using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// ReliabilityLayer::GetStatistics (ReliabilityLayer.cpp:3378-3415), copied into a snapshot the caller reuses so that
// publishing statistics every cycle allocates nothing.
public class ReliabilityLayerStatisticsTests
{
    private const int Mtu = 1492;

    [Fact]
    public void Copying_into_a_reused_snapshot_reports_what_GetStatistics_reports()
    {
        var sender = new ReliabilityLayer(Mtu, 0);
        var receiver = new ReliabilityLayer(Mtu, 0);
        var message = new byte[100];
        message[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 5; i++)
            sender.Send(message, Reliability.ReliableOrdered, Priority.High, 0, 0, 1_000);
        for (var nowUs = 2_000L; nowUs < 200_000; nowUs += 10_000)
        {
            sender.Update(nowUs);
            while (sender.TryDequeueDatagram(out var datagram))
                receiver.Receive(datagram, nowUs);
            receiver.Update(nowUs);
            while (receiver.TryDequeueDatagram(out var datagram))
                sender.Receive(datagram, nowUs);
        }

        var snapshot = new SessionStatistics();
        sender.CopyStatistics(snapshot);
        var expected = sender.GetStatistics(200_000);

        Assert.Equal(expected.RunningTotal, snapshot.RunningTotal);
        Assert.Equal(expected.ValueOverLastSecond, snapshot.ValueOverLastSecond);
        Assert.Equal(expected.MessagesInSendBuffer, snapshot.MessagesInSendBuffer);
        Assert.Equal(expected.MessagesInResendBuffer, snapshot.MessagesInResendBuffer);
        Assert.Equal(500UL, snapshot.RunningTotal[(int)SessionMetric.UserMessageBytesSent]);
        Assert.True(snapshot.RunningTotal[(int)SessionMetric.ActualBytesSent] > 500);

        var arrays = snapshot.RunningTotal;
        receiver.CopyStatistics(snapshot);
        Assert.Same(arrays, snapshot.RunningTotal);
        Assert.Equal(500UL, snapshot.RunningTotal[(int)SessionMetric.UserMessageBytesReceivedProcessed]);
    }
}
