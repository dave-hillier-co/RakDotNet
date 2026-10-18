using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// Replays the native client's connected datagrams from tests/vectors/connect-send-disconnect.json into a server-side
// reliability layer.
public class ReliabilityLayerGoldenVectorTests
{
    private const string Scenario = "connect-send-disconnect";
    private const long Start = 1_000_000;

    private readonly IReadOnlyList<CapturedDatagram> _captured = GoldenVectors.Load(Scenario);
    private readonly ReliabilityLayer _server = new(1492, Start);

    [Fact]
    public void The_ack_for_the_connection_request_matches_the_native_server_byte_for_byte()
    {
        Assert.True(_server.Receive(Captured(4), Start + 1_000));

        _server.Update(Start + 2_000);

        Assert.True(_server.TryDequeueDatagram(out var ack));
        Assert.Equal(Convert.ToHexStringLower(Captured(5)), Convert.ToHexStringLower(ack));
    }

    [Fact]
    public void The_clients_messages_are_delivered_in_the_order_it_sent_them()
    {
        var now = Start;
        foreach (var index in new[] { 4, 7, 8, 10, 11, 12, 15 })
            Assert.True(_server.Receive(Captured(index), now += 1_000));

        var delivered = new List<byte[]>();
        while (_server.TryDequeueEvent(out var e))
            delivered.Add(Assert.IsType<MessageDelivered>(e).Payload.ToArray());

        Assert.Equal(
            [
                MessageId.ConnectionRequest,
                MessageId.NewIncomingConnection,
                MessageId.ConnectedPing,
                MessageId.ConnectedPong,
                .. Enumerable.Repeat(MessageId.UserPacketEnum, 8),
                MessageId.DisconnectionNotification,
            ],
            delivered.Select(d => (MessageId)d[0])
        );
        // Each user message is ID, channel, reliability, then text (ScenarioFlows.ConnectSendDisconnectAsync).
        var users = delivered.Where(d => d[0] == (byte)MessageId.UserPacketEnum).ToList();
        Assert.Equal([0, 0, 0, 0, 3, 3, 3, 3], users.Select(u => (int)u[1]));
        Assert.Equal(
            ["ReliableOrdered on 0", "Unreliable on 0", "ReliableSequenced on 0", "ReliableOrderedWithAckReceipt on 0"],
            users.Take(4).Select(u => System.Text.Encoding.ASCII.GetString(u, 3, u.Length - 3))
        );
    }

    [Fact]
    public void Every_reliable_datagram_from_the_client_is_acknowledged()
    {
        var now = Start;
        foreach (var index in new[] { 4, 8, 10, 12, 15 })
            _server.Receive(Captured(index), now += 1_000);

        _server.Update(now + 1_000);

        Assert.True(_server.TryDequeueDatagram(out var ack));
        var reader = new BitStreamReader(ack);
        Assert.True(DatagramHeader.TryRead(ref reader, out _));
        Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
        Assert.Equal([new AckRange(new UInt24(0), new UInt24(4))], ranges.Ranges);
    }

    private byte[] Captured(int index) => _captured.Single(d => d.Index == index).Bytes;
}
