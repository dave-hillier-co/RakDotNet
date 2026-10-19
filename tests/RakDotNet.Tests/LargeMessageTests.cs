using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// Messages larger than one datagram are split and reassembled by the reliability layer (ReliabilityLayer.cpp:2928-3255),
// with ID_DOWNLOAD_PROGRESS while they arrive when asked for (RakPeer.cpp:2697-2704); and a message's bit length is
// kept end to end (RakPeer.cpp:1408, 6231).
public class LargeMessageTests
{
    [Fact]
    public void A_message_larger_than_the_MTU_arrives_whole()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        var message = new byte[100_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        new Random(7).NextBytes(message.AsSpan(1));

        var receipt = server.Send(message, Reliability.ReliableOrderedWithAckReceipt).Receipt;
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Data.Length == message.Length), "the message");
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ReceiptAcked && e.Receipt == receipt), "its receipt");

        Assert.Equal(message, peers.ServerEvents.Single(e => e.Data.Length == message.Length).Data);
    }

    [Fact]
    public void A_progress_interval_reports_DownloadProgress_while_a_split_message_arrives()
    {
        using var peers = new ManualPeers(server: o => o with { SplitMessageProgressInterval = 10 });
        var server = peers.Connect();
        var message = new byte[50_000];
        message[0] = (byte)MessageId.UserPacketEnum;

        server.Send(message, Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Data.Length == message.Length), "the message");

        var progress = peers.ServerEvents.Where(e => e.Kind == PeerEventKind.DownloadProgress).ToList();
        Assert.NotEmpty(progress);
        Assert.All(progress, p => Assert.Equal(MessageId.DownloadProgress, p.Id));
    }

    [Fact]
    public void A_message_whose_length_ends_mid_byte_arrives_with_its_bit_length()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        var writer = new BitStreamWriter(new byte[4]);
        writer.WriteByte((byte)MessageId.UserPacketEnum);
        writer.WriteBits(0b101, 3);

        server.Send(new OutgoingMessage(writer.Written, writer.BitsWritten), Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Id == MessageId.UserPacketEnum), "the message");

        Assert.Equal(11, peers.ServerEvents.Single(e => e.Id == MessageId.UserPacketEnum).BitLength);
    }
}
