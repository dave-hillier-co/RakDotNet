using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire;

// Field checks for the vectors captured from the sample-derived interop scenarios: offline discovery, out-of-band
// messages, refusals, every ordering channel and loss. GoldenVectorConformanceTests round-trips them byte for byte;
// these prove the fields come from the right place.
public class ScenarioGoldenVectorTests
{
    private const byte UserPacket = (byte)MessageId.UserPacketEnum;

    [Fact]
    public void A_ping_for_open_connections_carries_the_clients_guid_and_is_answered_with_the_servers()
    {
        var (client, server) = GoldenVectors.PeerGuids("unconnected-ping-open-connections");
        var datagrams = GoldenVectors.Load("unconnected-ping-open-connections");

        var ping = Decode<UnconnectedPing>(Single(datagrams, MessageId.UnconnectedPingOpenConnections, from: "a"));
        Assert.True(ping.OpenConnectionsOnly);
        Assert.Equal(client, ping.ClientGuid);
        var pong = Decode<UnconnectedPong>(Single(datagrams, MessageId.UnconnectedPong, from: "b"));
        Assert.Equal(server, pong.ServerGuid);
        Assert.Equal(ping.SendPingTime, pong.SendPingTime);
        Assert.True(pong.ResponseData.IsEmpty);
    }

    [Fact]
    public void Advertise_system_is_sent_out_of_band_with_the_senders_guid_and_the_data()
    {
        var (client, _) = GoldenVectors.PeerGuids("advertise-system");

        var advertise = Decode<AdvertiseSystem>(Single(GoldenVectors.Load("advertise-system"), MessageId.OutOfBandInternal, from: "a"));

        Assert.Equal(client, advertise.SenderGuid);
        Assert.Equal("hello world\0"u8.ToArray(), advertise.Data.ToArray());
    }

    [Fact]
    public void A_server_with_no_free_incoming_connections_refuses_the_second_request_with_its_guid()
    {
        var (client, server) = GoldenVectors.PeerGuids("no-free-incoming-connections");
        var datagrams = GoldenVectors.Load("no-free-incoming-connections");

        // RakPeer.cpp:5338-5357: the refusal answers ID_OPEN_CONNECTION_REQUEST_2, so the MTU exchange happens first.
        Assert.Equal(
            [MessageId.OpenConnectionRequest1, MessageId.OpenConnectionReply1, MessageId.OpenConnectionRequest2, MessageId.NoFreeIncomingConnections],
            datagrams.Select(d => (MessageId)d.Bytes[0]));
        Assert.Equal(client, Decode<OpenConnectionRequest2>(Single(datagrams, MessageId.OpenConnectionRequest2, from: "a")).ClientGuid);
        Assert.Equal(server, Decode<NoFreeIncomingConnections>(Single(datagrams, MessageId.NoFreeIncomingConnections, from: "b")).ServerGuid);
    }

    [Fact]
    public void A_banned_address_is_refused_at_its_first_offline_message_with_the_servers_guid()
    {
        var (_, server) = GoldenVectors.PeerGuids("connection-banned");
        var datagrams = GoldenVectors.Load("connection-banned");

        // RakPeer.cpp:4540-4565: the ban is checked before the message is even parsed.
        Assert.Equal([MessageId.OpenConnectionRequest1, MessageId.ConnectionBanned], datagrams.Select(d => (MessageId)d.Bytes[0]));
        Assert.Equal(server, Decode<ConnectionBanned>(Single(datagrams, MessageId.ConnectionBanned, from: "b")).ServerGuid);
    }

    [Fact]
    public void Messages_on_every_ordering_channel_carry_their_channel_and_ordering_and_sequencing_indexes()
    {
        var messages = Messages(GoldenVectors.Load("all-channels"), from: "a").Where(m => m.Split is null && m.Payload.Span[0] == UserPacket + 1).ToList();

        Assert.Equal(Enumerable.Range(0, 32).Select(c => (byte)c), messages.Select(m => m.OrderingChannel).Distinct().Order());
        foreach (var channel in Enumerable.Range(0, 32).Select(c => (byte)c))
        {
            // Each channel sent RELIABLE_ORDERED, RELIABLE_SEQUENCED, RELIABLE_ORDERED. The sequenced message shares the
            // ordering index of the next ordered one and starts its own sequence (ReliabilityLayer.cpp:1631-1642).
            // On channel 0 the client's ID_NEW_INCOMING_CONNECTION (RakPeer.cpp:6194-6201, RELIABLE_ORDERED) came first.
            var first = channel == 0 ? 1u : 0u;
            var onChannel = messages.Where(m => m.OrderingChannel == channel).ToList();
            Assert.Equal(
                [(Reliability.ReliableOrdered, first, 0u), (Reliability.ReliableSequenced, first + 1, 0u), (Reliability.ReliableOrdered, first + 1, 0u)],
                onChannel.Select(m => (m.Reliability, m.OrderingIndex.Value, m.Reliability.IsSequenced() ? m.SequencingIndex.Value : 0u)));
            Assert.All(onChannel, m => Assert.Equal(channel, m.Payload.Span[1]));
        }
    }

    [Fact]
    public void Every_datagram_the_server_naks_was_sent_and_its_reliable_messages_are_sent_again()
    {
        var datagrams = GoldenVectors.Load("reliable-under-loss");
        var sent = new Dictionary<uint, List<EncapsulatedMessage>>();
        var naked = new List<(int Index, uint Number)>();
        foreach (var datagram in datagrams)
        {
            if ((datagram.Bytes[0] & 0x80) == 0)
                continue;
            var reader = new BitStreamReader(datagram.Bytes);
            Assert.True(DatagramHeader.TryRead(ref reader, out var header));
            if (header.Kind == DatagramKind.Nak && datagram.From == "b")
            {
                Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
                naked.AddRange(ranges.Ranges.SelectMany(r => Enumerable.Range(0, (int)r.Count).Select(i => (datagram.Index, r.Min.Value + (uint)i))));
            }
            else if (header.Kind == DatagramKind.Data && datagram.From == "a")
            {
                var messages = new List<EncapsulatedMessage>();
                Assert.True(EncapsulatedMessageReader.TryReadAll(datagram.Bytes.AsMemory(reader.BitPosition >> 3), messages));
                sent[header.DatagramNumber.Value] = messages;
            }
        }

        Assert.NotEmpty(naked);
        foreach (var (index, number) in naked)
        {
            Assert.True(sent.TryGetValue(number, out var lost), $"NAK at {index} names datagram {number}, which the client never sent.");
            foreach (var message in lost.Where(m => m.Reliability.IsReliable()))
            {
                // A resend keeps the reliable message number (ReliabilityLayer.cpp:1908-1935) in a new datagram.
                Assert.Contains(sent, s => s.Key > number && s.Value.Exists(m => m.ReliableMessageNumber == message.ReliableMessageNumber));
            }
        }
    }

    [Fact]
    public void Captures_cover_naks_refusals_and_out_of_band_messages()
    {
        var offline = new HashSet<MessageId>();
        var kinds = new HashSet<DatagramKind>();
        foreach (var path in Directory.EnumerateFiles(GoldenVectors.Directory, "*.json"))
        foreach (var datagram in GoldenVectors.Load(Path.GetFileNameWithoutExtension(path)))
        {
            if ((datagram.Bytes[0] & 0x80) == 0)
            {
                offline.Add((MessageId)datagram.Bytes[0]);
                continue;
            }
            var reader = new BitStreamReader(datagram.Bytes);
            Assert.True(DatagramHeader.TryRead(ref reader, out var header));
            kinds.Add(header.Kind);
        }

        Assert.Superset(
            new HashSet<MessageId>
            {
                MessageId.UnconnectedPingOpenConnections,
                MessageId.OutOfBandInternal,
                MessageId.NoFreeIncomingConnections,
                MessageId.ConnectionBanned,
            },
            offline);
        Assert.Contains(DatagramKind.Nak, kinds);
    }

    private static byte[] Single(IReadOnlyList<CapturedDatagram> datagrams, MessageId id, string from) =>
        Assert.Single(datagrams, d => d.From == from && d.Bytes[0] == (byte)id).Bytes;

    private static IEnumerable<EncapsulatedMessage> Messages(IReadOnlyList<CapturedDatagram> datagrams, string from)
    {
        foreach (var datagram in datagrams.Where(d => d.From == from && (d.Bytes[0] & 0x80) != 0))
        {
            var reader = new BitStreamReader(datagram.Bytes);
            Assert.True(DatagramHeader.TryRead(ref reader, out var header));
            if (header.Kind != DatagramKind.Data)
                continue;
            var messages = new List<EncapsulatedMessage>();
            Assert.True(EncapsulatedMessageReader.TryReadAll(datagram.Bytes.AsMemory(reader.BitPosition >> 3), messages));
            foreach (var message in messages)
                yield return message;
        }
    }

    private static T Decode<T>(byte[] bytes)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name}.TryRead");
        return value;
    }
}
