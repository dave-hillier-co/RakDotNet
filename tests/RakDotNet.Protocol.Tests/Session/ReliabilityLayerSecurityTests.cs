using RakDotNet.Protocol.Security;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// ReliabilityLayer with useSecurity (LIBCAT_SECURITY): every datagram is protected by the connection's
// AuthenticatedEncryption, and the datagram budget loses the overhead (ReliabilityLayer.cpp:345-363, 668-677,
// 2304-2315, 3913-3924). Two layers are keyed by a real cat handshake, as RakPeer keys them.
public class ReliabilityLayerSecurityTests
{
    private const long Start = 1_000_000;

    [Fact]
    public void A_secure_layer_encrypts_its_datagrams_and_its_peer_delivers_them()
    {
        var (sender, receiver) = SecurePair(1492);
        var payload = "a secret message"u8.ToArray();

        sender.Send(payload, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
        sender.Update(Start + 1_000);
        var datagram = Assert.Single(Take(sender));

        Assert.False(datagram.AsSpan().IndexOf(payload) >= 0, "The payload went out in the clear.");
        Assert.True(receiver.Receive(datagram, Start + 2_000));
        var delivered = Assert.IsType<MessageDelivered>(Assert.Single(TakeEvents(receiver)));
        Assert.Equal(payload, delivered.Payload.ToArray());
    }

    [Fact]
    public void Each_datagram_is_the_plaintext_plus_eleven_bytes()
    {
        // AuthenticatedEncryption::OVERHEAD_BYTES: an 8-byte MAC and a 3-byte IV are appended.
        var (secure, _) = SecurePair(1492);
        var plain = new ReliabilityLayer(1492, Start);
        foreach (var layer in new[] { secure, plain })
        {
            layer.Send(new byte[100], Reliability.Reliable, Priority.High, 0, 0, Start);
            layer.Update(Start + 1_000);
        }

        Assert.Equal(Assert.Single(Take(plain)).Length + AuthenticatedEncryption.OverheadBytes, Assert.Single(Take(secure)).Length);
    }

    [Fact]
    public void Encrypting_a_datagram_allocates_no_more_than_its_overhead_beyond_a_plain_send()
    {
        // The datagram is encrypted where it is written, so a secure send makes the same arrays as a plain one.
        var (secure, _) = SecurePair(1492);
        var plain = new ReliabilityLayer(1492, Start);
        var allocated = new Dictionary<ReliabilityLayer, long>();
        foreach (var layer in new[] { secure, plain })
        {
            var nowUs = Start;
            for (var i = 0; i < 4; i++)
                SendOne(layer, ref nowUs);
            var before = GC.GetAllocatedBytesForCurrentThread();
            SendOne(layer, ref nowUs);
            allocated[layer] = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.InRange(allocated[secure] - allocated[plain], 0, 2 * AuthenticatedEncryption.OverheadBytes);

        static void SendOne(ReliabilityLayer layer, ref long nowUs)
        {
            nowUs += 1_000;
            layer.Send(new byte[400], Reliability.Unreliable, Priority.High, 0, 0, nowUs);
            layer.Update(nowUs);
            Take(layer);
        }
    }

    [Theory]
    [InlineData(576)]
    [InlineData(1200)]
    [InlineData(1492)]
    public void Split_parts_lose_the_overhead_twice_so_each_datagram_is_eleven_bytes_smaller(int mtu)
    {
        // Reset takes OVERHEAD_BYTES off the MTU given to the congestion manager, and
        // GetMaxDatagramSizeExcludingMessageHeaderBytes takes it off again (ReliabilityLayer.cpp:353-358, 3913-3920),
        // then Encrypt adds it back once.
        var (secure, _) = SecurePair(mtu);
        var plain = new ReliabilityLayer(mtu, Start);
        var largest = new Dictionary<ReliabilityLayer, int>();
        foreach (var layer in new[] { secure, plain })
        {
            layer.Send(new byte[10_000], Reliability.ReliableOrdered, Priority.High, 0, 0, Start);
            var sent = new List<byte[]>();
            for (var t = 1; t <= 200 && sent.Count < 5; t++)
            {
                layer.Update(Start + t * 1_000);
                sent.AddRange(Take(layer));
            }
            largest[layer] = sent.Max(d => d.Length);
        }

        Assert.Equal(largest[plain] - 2 * AuthenticatedEncryption.OverheadBytes + AuthenticatedEncryption.OverheadBytes, largest[secure]);
        Assert.True(largest[secure] <= mtu - 28 - AuthenticatedEncryption.OverheadBytes);
    }

    [Fact]
    public void A_secure_split_message_is_reassembled()
    {
        var (sender, receiver) = SecurePair(576);
        var payload = Enumerable.Range(0, 5_000).Select(i => (byte)i).ToArray();
        sender.Send(payload, Reliability.ReliableOrdered, Priority.High, 0, 0, Start);

        MessageDelivered? delivered = null;
        for (var t = 1; t <= 2_000 && delivered is null; t++)
        {
            var now = Start + t * 1_000;
            sender.Update(now);
            foreach (var datagram in Take(sender))
                receiver.Receive(datagram, now);
            receiver.Update(now);
            foreach (var datagram in Take(receiver))
                sender.Receive(datagram, now);
            delivered = TakeEvents(receiver).OfType<MessageDelivered>().SingleOrDefault();
        }

        Assert.NotNull(delivered);
        Assert.Equal(payload, delivered.Payload.ToArray());
    }

    [Fact]
    public void A_tampered_datagram_is_refused_and_the_genuine_one_still_decrypts()
    {
        // ReliabilityLayer.cpp:668-677: a datagram that fails Decrypt is dropped with HandleSocketReceiveFromConnectedPlayer
        // returning false.
        var (sender, receiver) = SecurePair(1492);
        sender.Send("hello"u8.ToArray(), Reliability.Reliable, Priority.High, 0, 0, Start);
        sender.Update(Start + 1_000);
        var datagram = Assert.Single(Take(sender));
        var tampered = datagram.ToArray();
        tampered[3] ^= 0x01;

        Assert.False(receiver.Receive(tampered, Start + 2_000));
        Assert.Empty(TakeEvents(receiver));
        Assert.True(receiver.Receive(datagram, Start + 2_000));
        Assert.IsType<MessageDelivered>(Assert.Single(TakeEvents(receiver)));
    }

    [Fact]
    public void A_replayed_datagram_is_refused()
    {
        var (sender, receiver) = SecurePair(1492);
        sender.Send("hello"u8.ToArray(), Reliability.Unreliable, Priority.High, 0, 0, Start);
        sender.Update(Start + 1_000);
        var datagram = Assert.Single(Take(sender));

        Assert.True(receiver.Receive(datagram.ToArray(), Start + 2_000));
        Assert.False(receiver.Receive(datagram, Start + 3_000));
        Assert.Single(TakeEvents(receiver));
    }

    [Fact]
    public void Two_bytes_or_fewer_are_ignored_before_decryption()
    {
        // ReliabilityLayer.cpp:653-658 checks the length first and returns true.
        var (_, receiver) = SecurePair(1492);

        Assert.True(receiver.Receive(new byte[] { 0x84, 0x00 }, Start + 1_000));
    }

    [Fact]
    public void Actual_bytes_count_the_encrypted_datagrams()
    {
        // ReliabilityLayer.cpp:2318 counts ACTUAL_BYTES_SENT after Encrypt; 645 counts received bytes before Decrypt.
        var (sender, receiver) = SecurePair(1492);
        sender.Send(new byte[50], Reliability.Reliable, Priority.High, 0, 0, Start);
        sender.Update(Start + 1_000);
        var datagram = Assert.Single(Take(sender));
        receiver.Receive(datagram, Start + 2_000);

        Assert.Equal((ulong)datagram.Length, sender.GetStatistics(Start + 2_000).RunningTotal[(int)SessionMetric.ActualBytesSent]);
        Assert.Equal((ulong)datagram.Length, receiver.GetStatistics(Start + 2_000).RunningTotal[(int)SessionMetric.ActualBytesReceived]);
    }

    [Fact]
    public void A_layer_without_security_is_not_secure()
    {
        Assert.False(new ReliabilityLayer(1492, Start).UsesSecurity);
        Assert.True(SecurePair(1492).Client.UsesSecurity);
    }

    // A client and a server layer keyed by ClientEasyHandshake::ProcessAnswer and ServerEasyHandshake::ProcessChallenge.
    internal static (ReliabilityLayer Client, ReliabilityLayer Server) SecurePair(int mtu)
    {
        var client = new ReliabilityLayer(mtu, Start, useSecurity: true);
        var server = new ReliabilityLayer(mtu, Start, useSecurity: true);
        Span<byte> publicKey = stackalloc byte[EasyHandshake.PublicKeyBytes];
        Span<byte> privateKey = stackalloc byte[EasyHandshake.PrivateKeyBytes];
        Span<byte> challenge = stackalloc byte[EasyHandshake.ChallengeBytes];
        Span<byte> answer = stackalloc byte[EasyHandshake.AnswerBytes];
        using var maker = new EasyHandshake(SystemSecureRandom.Instance);
        Assert.True(maker.GenerateServerKey(publicKey, privateKey));
        using var serverHandshake = new ServerEasyHandshake(SystemSecureRandom.Instance);
        using var clientHandshake = new ClientEasyHandshake(SystemSecureRandom.Instance);
        Assert.True(serverHandshake.Initialize(publicKey, privateKey));
        Assert.True(clientHandshake.Initialize(publicKey));
        Assert.True(clientHandshake.GenerateChallenge(challenge));
        Assert.True(serverHandshake.ProcessChallenge(challenge, answer, server.Encryption));
        Assert.True(clientHandshake.ProcessAnswer(answer, client.Encryption));
        return (client, server);
    }

    private static List<byte[]> Take(ReliabilityLayer layer)
    {
        var datagrams = new List<byte[]>();
        while (layer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }

    private static List<SessionEvent> TakeEvents(ReliabilityLayer layer)
    {
        var events = new List<SessionEvent>();
        while (layer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }
}
