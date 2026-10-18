using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Peer;

// Replays tests/vectors against a PeerCore in each role. The core is given the captured peer's GUID, address list and
// clock readings, so what it sends must equal what the native peer sent, byte for byte.
public class PeerGoldenVectorTests
{
    private const string Connect = "connect-send-disconnect";
    private const string UnconnectedPing = "unconnected-ping";

    // Endpoints and address lists as they appear inside the captured payloads (datagrams 2, 3, 6 and 8).
    private static readonly IPEndPoint ClientEndPoint = new(IPAddress.Loopback, 0xc16a);
    private static readonly IPEndPoint ServerEndPoint = new(IPAddress.Loopback, 0xf6b1);
    private static readonly IPAddress[] HostAddresses = [IPAddress.Parse("192.168.86.51"), IPAddress.Loopback];

    [Fact]
    public void As_server_the_offline_handshake_replies_match_the_native_server()
    {
        var captured = GoldenVectors.Load(Connect);
        var server = Server(Connect);

        server.Receive(ClientEndPoint, captured[0].Bytes, 60_000);
        AssertSent(server, ClientEndPoint, captured[1]);

        server.Receive(ClientEndPoint, captured[2].Bytes, 60_000);
        AssertSent(server, ClientEndPoint, captured[3]);
    }

    [Fact]
    public void As_server_the_connected_handshake_and_disconnect_match_the_native_server()
    {
        var captured = GoldenVectors.Load(Connect);
        var server = Server(Connect);
        server.Receive(ClientEndPoint, captured[0].Bytes, 60_000);
        server.Receive(ClientEndPoint, captured[2].Bytes, 60_000);
        Drain(server);

        // The connection request: acknowledged at once while the RTT is unknown, then answered. The native server
        // stamped ID_CONNECTION_REQUEST_ACCEPTED with 61 ms.
        server.Receive(ClientEndPoint, captured[4].Bytes, 60_500);
        server.Update(61_000);
        AssertSent(server, ClientEndPoint, captured[5]);
        server.Update(62_000);
        AssertSent(server, ClientEndPoint, captured[6]);

        // ID_NEW_INCOMING_CONNECTION and the client's ping, handled at 83 ms: the server pings and pongs in one
        // datagram. Its acknowledgement waits for the SYN interval now that the RTT is known.
        server.Receive(ClientEndPoint, captured[7].Bytes, 82_500);
        server.Receive(ClientEndPoint, captured[8].Bytes, 82_500);
        server.Update(83_000);
        Assert.Empty(Drain(server));
        server.Update(84_000);
        AssertSent(server, ClientEndPoint, captured[9]);
        var incoming = Assert.IsType<IncomingConnectionCompleted>(Assert.Single(Events(server)));
        Assert.Equal(GoldenVectors.PeerGuids(Connect).Client, incoming.Guid);
        Assert.Equal(Payloads(captured[8].Bytes)[0], incoming.Packet.ToArray());

        // The client's pong, its ack and the eight user messages.
        server.Receive(ClientEndPoint, captured[10].Bytes, 85_000);
        server.Receive(ClientEndPoint, captured[11].Bytes, 95_000);
        server.Receive(ClientEndPoint, captured[12].Bytes, 95_000);
        server.Update(96_000);
        Assert.Equal(Payloads(captured[12].Bytes), Events(server).Select(e => Assert.IsType<MessageReceived>(e).Packet.ToArray()));
        server.Update(106_000);
        Assert.Equal([new AckRange(new UInt24(1), new UInt24(3))], AckRanges(Assert.Single(Drain(server)).Data));

        // The disconnection notification: acknowledged, then the connection is closed and reported.
        server.Receive(ClientEndPoint, captured[15].Bytes, 120_000);
        server.Update(121_000);
        server.Update(131_000);
        AssertSent(server, ClientEndPoint, captured[16]);
        server.Update(132_000);
        Assert.IsType<Disconnected>(Assert.Single(Events(server)));
        Assert.Equal(ConnectionState.IsDisconnected, server.GetConnectionState(ClientEndPoint));
    }

    [Fact]
    public void As_client_every_datagram_matches_the_native_client()
    {
        var captured = GoldenVectors.Load(Connect);
        var client = Client(Connect);

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect(ServerEndPoint, default, 12, 500, 0, 20_000));
        client.Update(21_000);
        AssertSent(client, ServerEndPoint, captured[0]);

        client.Receive(ServerEndPoint, captured[1].Bytes, 21_500);
        AssertSent(client, ServerEndPoint, captured[2]);

        // ID_CONNECTION_REQUEST, stamped 22 ms.
        client.Receive(ServerEndPoint, captured[3].Bytes, 22_000);
        client.Update(22_500);
        AssertSent(client, ServerEndPoint, captured[4]);

        // The server's ack, then ID_CONNECTION_REQUEST_ACCEPTED handled at 34 ms: ID_NEW_INCOMING_CONNECTION and a
        // ping leave together, the ack once the SYN interval has passed.
        client.Receive(ServerEndPoint, captured[5].Bytes, 23_000);
        client.Receive(ServerEndPoint, captured[6].Bytes, 33_500);
        client.Update(34_000);
        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(Events(client)));
        client.Update(35_000);
        AssertSent(client, ServerEndPoint, captured[8]);
        client.Update(43_500);
        AssertSent(client, ServerEndPoint, captured[7]);

        // The server's ping, answered at 45 ms.
        client.Receive(ServerEndPoint, captured[9].Bytes, 44_000);
        client.Update(45_000);
        client.Update(46_000);
        AssertSent(client, ServerEndPoint, captured[10]);

        // The scenario's eight messages pack into one datagram.
        Reliability[] reliabilities = [Reliability.ReliableOrdered, Reliability.Unreliable, Reliability.ReliableSequenced, Reliability.ReliableOrderedWithAckReceipt];
        var receipts = new List<uint>();
        foreach (var channel in new byte[] { 0, 3 })
        foreach (var reliability in reliabilities)
        {
            byte[] data = [(byte)MessageId.UserPacketEnum, channel, (byte)reliability, .. System.Text.Encoding.ASCII.GetBytes($"{reliability} on {channel}")];
            var receipt = client.Send(ServerEndPoint, data, reliability, Priority.High, channel, 46_500);
            if (reliability == Reliability.ReliableOrderedWithAckReceipt)
                receipts.Add(receipt);
        }
        client.Update(47_000);
        AssertSent(client, ServerEndPoint, captured[12]);

        // The server's acks complete both receipts.
        client.Receive(ServerEndPoint, captured[13].Bytes, 48_000);
        client.Receive(ServerEndPoint, captured[14].Bytes, 59_000);
        client.Update(60_000);
        Assert.Equal(receipts, Events(client).Select(e => Assert.IsType<SendReceiptAcked>(e).Receipt));

        // ID_DISCONNECTION_NOTIFICATION, then the server's ack closes the connection.
        Drain(client);
        client.CloseConnection(ServerEndPoint, sendDisconnectionNotification: true, channel: 0, Priority.Low, 61_000);
        client.Update(62_000);
        AssertSent(client, ServerEndPoint, captured[15]);
        client.Receive(ServerEndPoint, captured[16].Bytes, 63_000);
        client.Update(64_000);
        Assert.IsType<Disconnected>(Assert.Single(Events(client)));
    }

    [Fact]
    public void As_server_the_unconnected_pong_matches_the_native_server()
    {
        var captured = GoldenVectors.Load(UnconnectedPing);
        var server = Server(UnconnectedPing);
        server.SetOfflinePingResponse("hello"u8);

        server.Receive(ClientEndPoint, captured[0].Bytes, 1_000);

        AssertSent(server, ClientEndPoint, captured[1]);
        var ping = Assert.IsType<UnconnectedPingReceived>(Assert.Single(Events(server)));
        Assert.Equal(GoldenVectors.PeerGuids(UnconnectedPing).Client, ping.Guid);
    }

    [Fact]
    public void As_client_the_unconnected_ping_matches_the_native_client_and_the_pong_is_reported()
    {
        // The native client pinged at 12 ms.
        var captured = GoldenVectors.Load(UnconnectedPing);
        var client = Client(UnconnectedPing);

        client.Ping(ServerEndPoint, onlyIfAccepting: false, 12_000);
        AssertSent(client, ServerEndPoint, captured[0]);

        client.Receive(ServerEndPoint, captured[1].Bytes, 13_000);
        var pong = Assert.IsType<UnconnectedPongReceived>(Assert.Single(Events(client)));
        Assert.Equal(GoldenVectors.PeerGuids(UnconnectedPing).Server, pong.Guid);
        Assert.Equal(12ul, pong.SendPingTime);
        Assert.Equal("hello"u8.ToArray(), pong.ResponseData.ToArray());
    }

    private static PeerCore Server(string scenario) =>
        new(
            new PeerOptions
            {
                Guid = GoldenVectors.PeerGuids(scenario).Server,
                MaxIncomingConnections = 8,
                LocalAddresses = LocalAddressList.Build(HostAddresses, 0xd346),
            }
        );

    private static PeerCore Client(string scenario) =>
        new(new PeerOptions { Guid = GoldenVectors.PeerGuids(scenario).Client, LocalAddresses = LocalAddressList.Build(HostAddresses, 0xc192) });

    private static void AssertSent(PeerCore peer, IPEndPoint to, CapturedDatagram expected)
    {
        var sent = Drain(peer);
        var datagram = Assert.Single(sent);
        Assert.Equal(to, datagram.Remote);
        Assert.True(
            expected.Bytes.AsSpan().SequenceEqual(datagram.Data),
            $"Datagram {expected.Index} differs.\ncaptured: {Convert.ToHexStringLower(expected.Bytes)}\nsent:     {Convert.ToHexStringLower(datagram.Data)}"
        );
    }

    private static List<OutgoingDatagram> Drain(PeerCore peer)
    {
        var datagrams = new List<OutgoingDatagram>();
        while (peer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }

    private static List<PeerEvent> Events(PeerCore peer)
    {
        var events = new List<PeerEvent>();
        while (peer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }

    private static List<byte[]> Payloads(byte[] datagram)
    {
        Assert.True(EncapsulatedMessageReader.TryOpenDatagram(datagram, out _, out var reader));
        var payloads = new List<byte[]>();
        while (reader.TryReadNext(out var message))
        {
            if (message.Payload.Span[0] is not ((byte)MessageId.ConnectedPing or (byte)MessageId.ConnectedPong))
                payloads.Add(message.Payload.ToArray());
        }
        return payloads;
    }

    private static IReadOnlyList<AckRange> AckRanges(byte[] datagram)
    {
        var reader = new BitStreamReader(datagram);
        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Ack, header.Kind);
        Assert.True(AckRangeList.TryRead(ref reader, out var ranges));
        return ranges.Ranges;
    }
}
