using System.Text.Json;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire;

// Every datagram captured between native RakNet peers (docs/golden-vectors.md) must decode with our codecs
// and re-encode to exactly the captured bytes.
public class GoldenVectorConformanceTests
{
    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(VectorDirectory, "*.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileNameWithoutExtension(path));
        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every_captured_datagram_round_trips_byte_for_byte(string scenario)
    {
        var datagrams = LoadDatagrams(scenario);
        Assert.NotEmpty(datagrams);

        foreach (var (index, bytes) in datagrams)
        {
            var reencoded = (bytes[0] & 0x80) == 0 ? RoundTripOffline(bytes) : RoundTripConnected(bytes);
            Assert.True(
                bytes.AsSpan().SequenceEqual(reencoded),
                $"{scenario}[{index}] differs.\ncaptured:  {Convert.ToHexStringLower(bytes)}\nreencoded: {Convert.ToHexStringLower(reencoded)}"
            );
        }
    }

    [Fact]
    public void Captures_cover_the_offline_and_connected_handshakes_and_split_messages()
    {
        var offline = new HashSet<MessageId>();
        var connected = new HashSet<MessageId>();
        var kinds = new HashSet<DatagramKind>();
        var sawSplit = false;

        foreach (var path in Directory.EnumerateFiles(VectorDirectory, "*.json"))
        foreach (var (_, bytes) in LoadDatagrams(Path.GetFileNameWithoutExtension(path)))
        {
            if ((bytes[0] & 0x80) == 0)
            {
                offline.Add((MessageId)bytes[0]);
                continue;
            }
            var reader = new BitStreamReader(bytes);
            Assert.True(DatagramHeader.TryRead(ref reader, out var header));
            kinds.Add(header.Kind);
            if (header.Kind != DatagramKind.Data)
                continue;
            var messages = new List<EncapsulatedMessage>();
            Assert.True(EncapsulatedMessageReader.TryReadAll(bytes.AsMemory(reader.BitPosition >> 3), messages));
            foreach (var message in messages)
            {
                sawSplit |= message.Split is not null;
                if (message.Split is null)
                    connected.Add((MessageId)message.Payload.Span[0]);
            }
        }

        Assert.Superset(
            new HashSet<MessageId>
            {
                MessageId.UnconnectedPing,
                MessageId.UnconnectedPong,
                MessageId.OpenConnectionRequest1,
                MessageId.OpenConnectionReply1,
                MessageId.OpenConnectionRequest2,
                MessageId.OpenConnectionReply2,
            },
            offline
        );
        Assert.Superset(
            new HashSet<MessageId>
            {
                MessageId.ConnectionRequest,
                MessageId.ConnectionRequestAccepted,
                MessageId.NewIncomingConnection,
                MessageId.DisconnectionNotification,
            },
            connected
        );
        Assert.Superset(new HashSet<DatagramKind> { DatagramKind.Data, DatagramKind.Ack }, kinds);
        Assert.True(sawSplit);
    }

    // Round trips prove nothing is lost; these prove fields are read from the right place.
    [Fact]
    public void Handshake_fields_decode_to_the_captured_peers_values()
    {
        var (client, server) = LoadPeerGuids("connect-send-disconnect");
        var datagrams = LoadDatagrams("connect-send-disconnect").Select(d => d.Bytes).ToList();

        var request1 = Decode<OpenConnectionRequest1>(datagrams.First(d => d[0] == (byte)MessageId.OpenConnectionRequest1));
        Assert.Equal(6, request1.ProtocolVersion);
        Assert.Equal(1492, request1.MtuSize);

        var reply1 = Decode<OpenConnectionReply1>(datagrams.First(d => d[0] == (byte)MessageId.OpenConnectionReply1));
        Assert.Equal(server, reply1.ServerGuid);
        Assert.Equal(1492, reply1.MtuSize);
        Assert.False(reply1.HasSecurity);

        var request2 = Decode<OpenConnectionRequest2>(datagrams.First(d => d[0] == (byte)MessageId.OpenConnectionRequest2));
        Assert.Equal(client, request2.ClientGuid);
        Assert.Equal(1492, request2.MtuSize);
        Assert.Equal(System.Net.IPAddress.Loopback, request2.ServerAddress.Address);

        var reply2 = Decode<OpenConnectionReply2>(datagrams.First(d => d[0] == (byte)MessageId.OpenConnectionReply2));
        Assert.Equal(server, reply2.ServerGuid);
        Assert.Equal(1492, reply2.MtuSize);
        Assert.Equal(System.Net.IPAddress.Loopback, reply2.ClientAddress.Address);

        var connectionRequest = ConnectedPayloads(datagrams).First(p => p[0] == (byte)MessageId.ConnectionRequest);
        Assert.Equal(client, Decode<ConnectionRequest>(connectionRequest).ClientGuid);
    }

    [Fact]
    public void Pong_carries_the_server_guid_and_offline_ping_response()
    {
        var (_, server) = LoadPeerGuids("unconnected-ping");
        var pong = Decode<UnconnectedPong>(LoadDatagrams("unconnected-ping").Select(d => d.Bytes).First(d => d[0] == (byte)MessageId.UnconnectedPong));

        Assert.Equal(server, pong.ServerGuid);
        Assert.Equal("hello"u8.ToArray(), pong.ResponseData.ToArray());
    }

    private static T Decode<T>(byte[] bytes)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name}.TryRead");
        return value;
    }

    private static IEnumerable<byte[]> ConnectedPayloads(IEnumerable<byte[]> datagrams)
    {
        foreach (var bytes in datagrams.Where(d => (d[0] & 0x80) != 0))
        {
            var reader = new BitStreamReader(bytes);
            if (!DatagramHeader.TryRead(ref reader, out var header) || header.Kind != DatagramKind.Data)
                continue;
            var messages = new List<EncapsulatedMessage>();
            Assert.True(EncapsulatedMessageReader.TryReadAll(bytes.AsMemory(reader.BitPosition >> 3), messages));
            foreach (var message in messages.Where(m => m.Split is null))
                yield return message.Payload.ToArray();
        }
    }

    private static (ulong Client, ulong Server) LoadPeerGuids(string scenario)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorDirectory, scenario + ".json")));
        var peers = document.RootElement.GetProperty("peers");
        return (ulong.Parse(peers.GetProperty("a").GetProperty("guid").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            ulong.Parse(peers.GetProperty("b").GetProperty("guid").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static byte[] RoundTripOffline(byte[] bytes) =>
        (MessageId)bytes[0] switch
        {
            MessageId.UnconnectedPing or MessageId.UnconnectedPingOpenConnections => RoundTrip<UnconnectedPing>(bytes),
            MessageId.UnconnectedPong => RoundTrip<UnconnectedPong>(bytes),
            MessageId.OpenConnectionRequest1 => RoundTrip<OpenConnectionRequest1>(bytes),
            MessageId.OpenConnectionReply1 => RoundTrip<OpenConnectionReply1>(bytes),
            MessageId.OpenConnectionRequest2 => RoundTrip<OpenConnectionRequest2>(bytes),
            MessageId.OpenConnectionReply2 => RoundTrip<OpenConnectionReply2>(bytes),
            var id => throw new Xunit.Sdk.XunitException($"No conformance mapping for offline message {id}."),
        };

    private static byte[] RoundTripConnected(byte[] bytes)
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(DatagramHeader.TryRead(ref reader, out var header), "datagram header");
        var buffer = new byte[bytes.Length + 64];
        var writer = new BitStreamWriter(buffer);
        header.Write(ref writer);

        if (header.Kind is DatagramKind.Ack or DatagramKind.Nak)
        {
            Assert.True(AckRangeList.TryRead(ref reader, out var ranges), "ack ranges");
            ranges.Write(ref writer);
            return writer.Written.ToArray();
        }

        var messages = new List<EncapsulatedMessage>();
        Assert.True(EncapsulatedMessageReader.TryReadAll(bytes.AsMemory(reader.BitPosition >> 3), messages), "encapsulated messages");
        foreach (var message in messages)
        {
            if (message.Split is null)
                AssertControlMessageRoundTrips(message.Payload.ToArray());
            message.Write(ref writer);
        }
        return writer.Written.ToArray();
    }

    private static void AssertControlMessageRoundTrips(byte[] payload)
    {
        var reencoded = (MessageId)payload[0] switch
        {
            MessageId.ConnectedPing => RoundTrip<ConnectedPing>(payload),
            MessageId.ConnectedPong => RoundTrip<ConnectedPong>(payload),
            MessageId.ConnectionRequest => RoundTrip<ConnectionRequest>(payload),
            MessageId.ConnectionRequestAccepted => RoundTrip<ConnectionRequestAccepted>(payload),
            MessageId.NewIncomingConnection => RoundTrip<NewIncomingConnection>(payload),
            MessageId.DisconnectionNotification => RoundTrip<DisconnectionNotification>(payload),
            _ => payload,
        };
        Assert.True(
            payload.AsSpan().SequenceEqual(reencoded),
            $"{(MessageId)payload[0]} differs.\ncaptured:  {Convert.ToHexStringLower(payload)}\nreencoded: {Convert.ToHexStringLower(reencoded)}"
        );
    }

    private static byte[] RoundTrip<T>(byte[] bytes)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name}.TryRead");
        var writer = new BitStreamWriter(new byte[bytes.Length + 64]);
        value.Write(ref writer);
        return writer.Written.ToArray();
    }

    private static List<(int Index, byte[] Bytes)> LoadDatagrams(string scenario)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorDirectory, scenario + ".json")));
        return document
            .RootElement.GetProperty("datagrams")
            .EnumerateArray()
            .Select(d => (d.GetProperty("index").GetInt32(), Convert.FromHexString(d.GetProperty("hex").GetString()!)))
            .ToList();
    }

    private static string VectorDirectory => Path.Combine(AppContext.BaseDirectory, "vectors");
}
