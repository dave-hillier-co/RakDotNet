using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Peer;

// Sociable tests: a client and a server PeerCore talk through an in-memory link, with time driven explicitly.
public class PeerCoreTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public PeerCoreTests()
    {
        _client = _link.Attach(
            ClientAddress,
            new PeerCore(new PeerOptions { Guid = ClientGuid, LocalAddresses = LocalAddressList.Build([ClientAddress.Address], 50_001) })
        );
        _server = _link.Attach(
            ServerAddress,
            new PeerCore(
                new PeerOptions
                {
                    Guid = ServerGuid,
                    MaxIncomingConnections = 8,
                    LocalAddresses = LocalAddressList.Build([ServerAddress.Address], 19_132),
                }
            )
        );
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    private IReadOnlyList<PeerEvent> ServerEvents => _link.EventsOf(_server);

    [Fact]
    public void An_unconnected_ping_is_answered_with_the_offline_ping_response()
    {
        _server.SetOfflinePingResponse("hello"u8);

        Assert.True(_client.Ping(ServerAddress, onlyIfAccepting: false, _link.NowUs));
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        var pong = Assert.IsType<UnconnectedPongReceived>(Assert.Single(ClientEvents));
        Assert.Equal(ServerAddress, pong.Remote);
        Assert.Equal(ServerGuid, pong.Guid);
        Assert.Equal((ulong)(_link.NowMs - 2), pong.SendPingTime);
        Assert.Equal("hello"u8.ToArray(), pong.ResponseData.ToArray());
        // RakPeer.cpp:4693-4699: ID, the ping time as 4 bytes, then the response.
        Assert.Equal([(byte)MessageId.UnconnectedPong, .. BigEndian((uint)pong.SendPingTime), .. "hello"u8.ToArray()], pong.Packet.ToArray());

        var ping = Assert.IsType<UnconnectedPingReceived>(Assert.Single(ServerEvents));
        Assert.Equal(ClientGuid, ping.Guid);
        Assert.Equal([(byte)MessageId.UnconnectedPing], ping.Packet.ToArray());
    }

    [Fact]
    public void A_ping_for_open_connections_only_is_ignored_by_a_server_not_accepting_connections()
    {
        var closed = _link.Attach(new IPEndPoint(IPAddress.Parse("10.0.0.3"), 1), new PeerCore(new PeerOptions { Guid = 3 }));

        _client.Ping(new IPEndPoint(IPAddress.Parse("10.0.0.3"), 1), onlyIfAccepting: true, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(20));

        Assert.Empty(ClientEvents);
        Assert.Empty(_link.EventsOf(closed));
    }

    [Fact]
    public void Connecting_completes_on_both_sides()
    {
        Connect();

        var accepted = Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(ClientEvents));
        Assert.Equal(ServerAddress, accepted.Remote);
        Assert.Equal(ServerGuid, accepted.Guid);
        Assert.Equal(MessageId.ConnectionRequestAccepted, accepted.Id);
        Assert.Equal(ClientAddress, accepted.ExternalAddress);

        var incoming = Assert.IsType<IncomingConnectionCompleted>(Assert.Single(ServerEvents));
        Assert.Equal(ClientAddress, incoming.Remote);
        Assert.Equal(ClientGuid, incoming.Guid);
        Assert.Equal(MessageId.NewIncomingConnection, incoming.Id);

        Assert.Equal(ConnectionState.IsConnected, _client.GetConnectionState(ServerAddress));
        Assert.Equal(ConnectionState.IsConnected, _server.GetConnectionState(ClientAddress));
        Assert.Equal(ConnectionState.IsConnected, _server.GetConnectionState(ClientGuid));
    }

    [Fact]
    public void The_offline_handshake_probes_at_1492_and_agrees_on_it()
    {
        Connect();

        var request1 = Decode<OpenConnectionRequest1>(DatagramsFrom(ClientAddress).First(d => d[0] == (byte)MessageId.OpenConnectionRequest1));
        Assert.Equal(1492, request1.MtuSize);
        Assert.Equal(RakNetConstants.ProtocolVersion, request1.ProtocolVersion);

        var reply2 = Decode<OpenConnectionReply2>(DatagramsFrom(ServerAddress).First(d => d[0] == (byte)MessageId.OpenConnectionReply2));
        Assert.Equal(1492, reply2.MtuSize);
        Assert.Equal(ClientAddress, reply2.ClientAddress);
        Assert.Equal(ServerGuid, reply2.ServerGuid);
    }

    [Fact]
    public void Connection_request_accepted_carries_the_address_list_and_system_index_as_RakPeer_fills_them()
    {
        Connect();

        var accepted = Decode<ConnectionRequestAccepted>(Assert.IsType<OutgoingConnectionCompleted>(ClientEvents[0]).Packet.ToArray());
        Assert.Equal(ClientAddress, accepted.ClientAddress);
        Assert.Equal(0, accepted.SystemIndex);
        Assert.Equal(LocalAddressList.Build([ServerAddress.Address], 19_132), accepted.InternalAddresses);

        var incoming = Decode<NewIncomingConnection>(Assert.IsType<IncomingConnectionCompleted>(ServerEvents[0]).Packet.ToArray());
        Assert.Equal(ServerAddress, incoming.ServerAddress);
        Assert.Equal(LocalAddressList.Build([ClientAddress.Address], 50_001), incoming.InternalAddresses);
        // RakPeer.cpp:6198: the server's reply time is echoed back.
        Assert.Equal(accepted.ReplyTime, incoming.SendPingTime);
    }

    [Fact]
    public void Every_reliability_of_the_scenario_is_delivered_with_its_content_and_order_on_channels_0_and_3()
    {
        Connect();
        Reliability[] reliabilities = [Reliability.ReliableOrdered, Reliability.Unreliable, Reliability.ReliableSequenced, Reliability.ReliableOrderedWithAckReceipt];
        var sent = new List<byte[]>();
        var receipts = new List<uint>();
        foreach (var channel in new byte[] { 0, 3 })
        {
            foreach (var reliability in reliabilities)
            {
                byte[] data = [(byte)MessageId.UserPacketEnum, channel, (byte)reliability, .. System.Text.Encoding.ASCII.GetBytes($"{reliability} on {channel}")];
                var receipt = _client.Send(ServerAddress, data, reliability, Priority.High, channel, _link.NowUs);
                Assert.NotEqual(0u, receipt);
                sent.Add(data);
                if (reliability == Reliability.ReliableOrderedWithAckReceipt)
                    receipts.Add(receipt);
            }
        }

        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Count() == sent.Count && ClientEvents.OfType<SendReceiptAcked>().Count() == 2, TimeSpan.FromSeconds(1));

        var received = ServerEvents.OfType<MessageReceived>().ToList();
        Assert.All(received, m => Assert.Equal(ClientGuid, m.Guid));
        Assert.Equal(sent.Select(Convert.ToHexStringLower), received.Select(m => Convert.ToHexStringLower(m.Packet.Span)));
        var acked = ClientEvents.OfType<SendReceiptAcked>().ToList();
        Assert.Equal(receipts, acked.Select(a => a.Receipt));
        Assert.All(acked, a => Assert.Equal(MessageId.SndReceiptAcked, a.Id));
        Assert.Equal([(byte)MessageId.SndReceiptAcked, .. BitConverter.GetBytes(receipts[0])], acked[0].Packet.ToArray());
    }

    [Fact]
    public void Messages_from_the_server_reach_the_client_too()
    {
        Connect();

        _server.Send(ClientGuid, [(byte)MessageId.UserPacketEnum, 42], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ClientEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));

        Assert.Equal([(byte)MessageId.UserPacketEnum, 42], ClientEvents.OfType<MessageReceived>().Single().Packet.ToArray());
    }

    [Fact]
    public void Receipts_count_up_from_one_for_every_send()
    {
        // RakPeer::IncrementNextSendReceipt (RakPeer.cpp:1279-1287); sends that want no receipt still use a number.
        Connect();

        Assert.Equal(1u, _client.Send(ServerAddress, [134], Reliability.Reliable, Priority.High, 0, _link.NowUs));
        Assert.Equal(2u, _client.Send(ServerAddress, [134], Reliability.Unreliable, Priority.High, 0, _link.NowUs));
    }

    [Fact]
    public void Sending_to_the_unassigned_guid_returns_receipt_zero_without_using_a_number()
    {
        // RakPeer.cpp:1323-1324: an undefined target returns 0 before IncrementNextSendReceipt.
        Connect();

        Assert.Equal(0u, _client.Send(RakNetConstants.UnassignedGuid, [134], Reliability.Reliable, Priority.High, 0, _link.NowUs));
        Assert.Equal(1u, _client.Send(ServerGuid, [134], Reliability.Reliable, Priority.High, 0, _link.NowUs));
    }

    [Fact]
    public void An_empty_send_uses_a_receipt_number_and_sends_nothing()
    {
        // RakPeer.cpp:1317-1328 rejects only length < 0 before IncrementNextSendReceipt; ReliabilityLayer.cpp:1567-1570
        // then drops the 0-bit message.
        Connect();

        Assert.Equal(1u, _client.Send(ServerAddress, [], Reliability.Reliable, Priority.High, 0, _link.NowUs));
        Assert.Equal(2u, _client.Send(ServerGuid, [], Reliability.Reliable, Priority.High, 0, _link.NowUs));
        Assert.Equal(3u, _client.Send(ServerGuid, [134], Reliability.Reliable, Priority.High, 0, _link.NowUs));

        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));
        Assert.Equal([134], ServerEvents.OfType<MessageReceived>().Single().Packet.ToArray());
    }

    [Fact]
    public void Notifying_for_shutdown_closes_every_connection_once_the_notification_is_delivered()
    {
        // RakPeer::Shutdown (RakPeer.cpp:1050-1082) calls NotifyAndFlagForShutdown for every active system, then waits
        // until none is active.
        Connect();
        Assert.Equal(1, _client.ActiveSystemCount);

        _client.NotifyAllForShutdown(channel: 0, Priority.Low, _link.NowUs);
        _link.RunUntil(() => _client.ActiveSystemCount == 0, TimeSpan.FromSeconds(1));

        Assert.Equal(ClientGuid, ServerEvents.OfType<Disconnected>().Single().Guid);
    }

    [Fact]
    public void Closing_with_notification_disconnects_both_sides_after_the_last_message_arrives()
    {
        Connect();
        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, 1], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);

        _client.CloseConnection(ServerAddress, sendDisconnectionNotification: true, channel: 0, Priority.Low, _link.NowUs);
        Assert.Equal(ConnectionState.IsDisconnecting, _client.GetConnectionState(ServerAddress));
        _link.RunUntil(() => ServerEvents.OfType<Disconnected>().Any() && ClientEvents.OfType<Disconnected>().Any(), TimeSpan.FromSeconds(1));

        // The ordered event stream: the last message comes before the disconnection.
        Assert.IsType<MessageReceived>(ServerEvents[^2]);
        var disconnected = Assert.IsType<Disconnected>(ServerEvents[^1]);
        Assert.Equal(ClientGuid, disconnected.Guid);
        Assert.Equal([(byte)MessageId.DisconnectionNotification], disconnected.Packet.ToArray());
        Assert.Equal(ConnectionState.IsDisconnected, _client.GetConnectionState(ServerAddress));
        Assert.Equal(ConnectionState.IsDisconnected, _server.GetConnectionState(ClientAddress));
        Assert.Equal(ConnectionState.IsNotConnected, _server.GetConnectionState(ClientGuid));
    }

    [Fact]
    public void The_server_can_close_the_connection_too()
    {
        Connect();

        _server.CloseConnection(ClientGuid, sendDisconnectionNotification: true, channel: 0, Priority.Low, _link.NowUs);
        _link.RunUntil(() => ClientEvents.OfType<Disconnected>().Any(), TimeSpan.FromSeconds(1));

        Assert.Equal(ServerGuid, ClientEvents.OfType<Disconnected>().Single().Guid);
    }

    [Fact]
    public void Closing_without_notification_reports_the_connection_lost_locally_and_the_remote_times_out()
    {
        Connect();

        _client.CloseConnection(ServerAddress, sendDisconnectionNotification: false, channel: 0, Priority.Low, _link.NowUs);

        // RakPeer.cpp:1667-1678.
        Assert.IsType<ConnectionLost>(Assert.Single(DrainEvents(_client)));
        // The server's keepalive ping (every timeout / 2) goes unacknowledged, so it gives up after the 10 s timeout.
        _link.RunUntil(() => ServerEvents.OfType<ConnectionLost>().Any(), TimeSpan.FromSeconds(20), stepUs: 10_000);
        Assert.InRange(_link.NowMs - ConnectedAtMs, 10_000, 16_000);
    }

    [Fact]
    public void Sends_after_closing_are_dropped()
    {
        Connect();
        _client.CloseConnection(ServerAddress, sendDisconnectionNotification: true, channel: 0, Priority.Low, _link.NowUs);

        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<Disconnected>().Any(), TimeSpan.FromSeconds(1));

        Assert.DoesNotContain(ServerEvents, e => e is MessageReceived);
    }

    [Fact]
    public void A_connected_ping_is_answered_with_a_pong_echoing_its_time()
    {
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(10));

        // The server pings as soon as the client is connected (RakPeer.cpp:6010); the client answers
        // (RakPeer.cpp:6077-6094).
        var serverPing = ConnectedPayloads(ServerAddress).Select(p => TryDecode<ConnectedPing>(p)).First(p => p is not null)!.Value;
        var clientPong = ConnectedPayloads(ClientAddress).Select(p => TryDecode<ConnectedPong>(p)).First(p => p is not null)!.Value;
        Assert.Equal(serverPing.SendPingTime, clientPong.SendPingTime);
    }

    [Fact]
    public void An_idle_connection_sends_a_reliable_keepalive_ping_after_half_the_timeout()
    {
        Connect();
        var pingsBefore = ConnectedPayloads(ClientAddress).Count(p => p[0] == (byte)MessageId.ConnectedPing);

        _link.Run(TimeSpan.FromMilliseconds(5_100), stepUs: 10_000);

        Assert.Equal(pingsBefore + 1, ConnectedPayloads(ClientAddress).Count(p => p[0] == (byte)MessageId.ConnectedPing));
        Assert.Equal(ConnectionState.IsConnected, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void A_connection_attempt_to_nobody_fails_after_the_attempts_run_out()
    {
        var nobody = new IPEndPoint(IPAddress.Parse("10.0.0.9"), 9);

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(nobody, default, 3, 100, 0, _link.NowUs));
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(nobody));
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
        Assert.Equal(MessageId.ConnectionAttemptFailed, failed.Reason);
        Assert.Equal(nobody, failed.Remote);
        // RakPeer.cpp:5709-5712: requests are sent until attemptCount + 1 have been made, then the next due time fails.
        Assert.Equal(4, _link.Delivered.Count(d => d.To.Equals(nobody)));
        Assert.Equal(ConnectionState.IsNotConnected, _client.GetConnectionState(nobody));
    }

    [Fact]
    public void Connecting_twice_reports_the_attempt_in_progress_then_the_existing_connection()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));

        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        Assert.Equal(ConnectionAttemptResult.AlreadyConnectedToEndpoint, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
    }

    [Fact]
    public void A_server_with_no_free_incoming_connections_rejects_the_attempt()
    {
        var full = new IPEndPoint(IPAddress.Parse("10.0.0.4"), 4);
        _link.Attach(full, new PeerCore(new PeerOptions { Guid = 4, MaxIncomingConnections = 0 }));

        _client.Connect(full, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
        Assert.Equal(MessageId.NoFreeIncomingConnections, failed.Reason);
        Assert.Equal(4ul, failed.Guid);
        Assert.Equal(ConnectionState.IsNotConnected, _client.GetConnectionState(full));
    }

    [Fact]
    public void A_wrong_password_is_rejected_with_invalid_password()
    {
        var guarded = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 5);
        _link.Attach(guarded, new PeerCore(new PeerOptions { Guid = 5, MaxIncomingConnections = 8, IncomingPassword = "secret"u8.ToArray() }));

        _client.Connect(guarded, "guess"u8.ToArray(), 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
        Assert.Equal(MessageId.InvalidPassword, failed.Reason);
        Assert.Equal(5ul, Decode<InvalidPassword>(failed.Packet.ToArray()).ServerGuid);
    }

    [Fact]
    public void The_right_password_connects()
    {
        var guarded = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 5);
        _link.Attach(guarded, new PeerCore(new PeerOptions { Guid = 5, MaxIncomingConnections = 8, IncomingPassword = "secret"u8.ToArray() }));

        _client.Connect(guarded, "secret"u8.ToArray(), 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(ClientEvents));
    }

    [Fact]
    public void A_server_answers_a_foreign_protocol_version_with_incompatible_protocol_version()
    {
        var request = Encode(new OpenConnectionRequest1(ProtocolVersion: 5, MtuSize: 576));

        _server.Receive(ClientAddress, request, _link.NowUs);

        Assert.True(_server.TryDequeueDatagram(out var reply));
        Assert.Equal(ClientAddress, reply.Remote);
        var incompatible = Decode<IncompatibleProtocolVersion>(reply.Data);
        Assert.Equal(RakNetConstants.ProtocolVersion, incompatible.ProtocolVersion);
        Assert.Equal(ServerGuid, incompatible.ServerGuid);
    }

    [Fact]
    public void A_client_told_its_protocol_is_incompatible_abandons_the_attempt()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, Encode(new IncompatibleProtocolVersion(5, ServerGuid)), _link.NowUs);
        _client.Update(_link.NowUs + 1_000);

        Assert.True(_client.TryDequeueEvent(out var e));
        var failed = Assert.IsType<ConnectionAttemptFailed>(e);
        Assert.Equal(MessageId.IncompatibleProtocolVersion, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Equal(ConnectionState.IsNotConnected, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void A_client_told_it_is_already_connected_abandons_the_attempt()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, Encode(new AlreadyConnected(ServerGuid)), _link.NowUs);

        Assert.True(_client.TryDequeueEvent(out var e));
        Assert.Equal(MessageId.AlreadyConnected, Assert.IsType<ConnectionAttemptFailed>(e).Reason);
    }

    [Fact]
    public void Rejections_from_addresses_with_no_attempt_are_ignored()
    {
        _client.Receive(ServerAddress, Encode(new AlreadyConnected(ServerGuid)), _link.NowUs);

        Assert.False(_client.TryDequeueEvent(out _));
    }

    [Fact]
    public void A_second_open_connection_request_from_a_connected_address_with_another_guid_gets_already_connected()
    {
        // RakPeer.cpp:5288-5292 outcome 4, and 5334-5346.
        Connect();
        while (_server.TryDequeueDatagram(out _))
        {
        }

        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid + 1)), _link.NowUs);

        Assert.True(_server.TryDequeueDatagram(out var reply));
        Assert.Equal(ServerGuid, Decode<AlreadyConnected>(reply.Data).ServerGuid);
    }

    [Fact]
    public void A_repeated_open_connection_request_before_the_connection_request_gets_the_same_reply()
    {
        // RakPeer.cpp:5267-5272 outcome 1: a duplicate caused by packet loss.
        var request = Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid));
        _server.Receive(ClientAddress, request, _link.NowUs);
        Assert.True(_server.TryDequeueDatagram(out var first));

        _server.Receive(ClientAddress, request, _link.NowUs);

        Assert.True(_server.TryDequeueDatagram(out var second));
        Assert.Equal(first.Data, second.Data);
    }

    [Fact]
    public void An_unverified_sender_that_sends_anything_but_a_connection_request_is_dropped()
    {
        // RakPeer.cpp:5955-5976.
        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid)), _link.NowUs);
        var layer = new RakDotNet.Protocol.Session.ReliabilityLayer(1492, _link.NowUs);
        layer.Send(new byte[] { (byte)MessageId.UserPacketEnum }, Reliability.Reliable, Priority.High, 0, 0, _link.NowUs);
        layer.Update(_link.NowUs + 1_000);
        Assert.True(layer.TryDequeueDatagram(out var datagram));

        _server.Receive(ClientAddress, datagram, _link.NowUs + 2_000);
        _server.Update(_link.NowUs + 3_000);

        Assert.Equal(ConnectionState.IsDisconnected, _server.GetConnectionState(ClientAddress));
        Assert.False(_server.TryDequeueEvent(out _));
    }

    [Fact]
    public void A_connection_that_never_completes_is_dropped_after_ten_seconds()
    {
        // RakPeer.cpp:5877-5881: REQUESTED_CONNECTION, HANDLING_CONNECTION_REQUEST and UNVERIFIED_SENDER expire.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        // Lose everything the server sends once the connected phase starts, so the handshake stalls.
        _link.Drop = (from, _, data) => from.Equals(ServerAddress) && (data[0] & 0x80) != 0;

        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(12), stepUs: 10_000);

        Assert.Equal(MessageId.ConnectionAttemptFailed, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents)).Reason);
        Assert.Equal(ConnectionState.IsDisconnected, _server.GetConnectionState(ClientAddress));
    }

    private long ConnectedAtMs { get; set; }

    private void Connect()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(
            () => ClientEvents.OfType<OutgoingConnectionCompleted>().Any() && ServerEvents.OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(1)
        );
        ConnectedAtMs = _link.NowMs;
    }

    private static List<PeerEvent> DrainEvents(PeerCore peer)
    {
        var events = new List<PeerEvent>();
        while (peer.TryDequeueEvent(out var e))
            events.Add(e);
        return events;
    }

    private IEnumerable<byte[]> DatagramsFrom(IPEndPoint from) => _link.Delivered.Where(d => d.From.Equals(from)).Select(d => d.Data);

    private IEnumerable<byte[]> ConnectedPayloads(IPEndPoint from)
    {
        foreach (var datagram in DatagramsFrom(from).Where(d => (d[0] & 0x80) != 0))
        {
            if (!EncapsulatedMessageReader.TryOpenDatagram(datagram, out _, out var reader))
                continue;
            while (reader.TryReadNext(out var message))
                yield return message.Payload.ToArray();
        }
    }

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Encode<T>(T message)
        where T : IWireMessage<T>
    {
        var writer = new BitStreamWriter(new byte[2048]);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    private static T Decode<T>(byte[] bytes)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name} failed to decode {Convert.ToHexStringLower(bytes)}");
        return value;
    }

    private static T? TryDecode<T>(byte[] bytes)
        where T : struct, IWireMessage<T>
    {
        var reader = new BitStreamReader(bytes);
        return T.TryRead(ref reader, out var value) ? value : null;
    }
}
