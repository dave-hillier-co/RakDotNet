using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins.Tests.NatPunchthrough;

/// <summary>
/// NatPunchthroughClient on two systems behind routers and NatPunchthroughServer on a facilitator, from
/// Samples/NATCompleteClient's NatPunchthoughClientFramework and NATCompleteServer's NatPunchthroughServerFramework: the
/// sender opens the NAT to the recipient's GUID, and on success connects to the address the success came from.
/// </summary>
public sealed class NatPunchthroughTests : IDisposable
{
    private const string FacilitatorAddress = "203.0.113.1";

    private readonly NatMesh _mesh = new();
    private readonly NatPunchthroughServer _server = new();
    private readonly NatPunchthroughClient _senderPlugin = new();
    private readonly NatPunchthroughClient _recipientPlugin = new();
    private readonly RakPeer _facilitator;

    public NatPunchthroughTests()
    {
        _facilitator = _mesh.Add(FacilitatorAddress, plugins: _server);
    }

    public void Dispose() => _mesh.Dispose();

    public static TheoryData<NatBehaviour, NatBehaviour, bool> RouterPairs => new()
    {
        // NatTypeDetectionCommon.cpp:21-37: symmetric needs address restricted or less, port restricted needs port
        // restricted or less.
        { NatBehaviour.FullCone, NatBehaviour.FullCone, true },
        { NatBehaviour.AddressRestricted, NatBehaviour.PortRestricted, true },
        { NatBehaviour.PortRestricted, NatBehaviour.PortRestricted, true },
        { NatBehaviour.Symmetric, NatBehaviour.FullCone, true },
        { NatBehaviour.AddressRestricted, NatBehaviour.Symmetric, true },
        { NatBehaviour.Symmetric, NatBehaviour.PortRestricted, false },
        { NatBehaviour.PortRestricted, NatBehaviour.Symmetric, false },
        { NatBehaviour.Symmetric, NatBehaviour.Symmetric, false },
    };

    [Theory]
    [MemberData(nameof(RouterPairs))]
    public void Punchthrough_succeeds_where_the_routers_allow_it(NatBehaviour senderRouter, NatBehaviour recipientRouter, bool succeeds)
    {
        var (sender, recipient) = AddClients(senderRouter, recipientRouter);

        Assert.True(_senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress));
        var (atSender, atRecipient) = WaitForResults(sender, recipient);

        var expected = (byte)(succeeds ? MessageId.NatPunchthroughSucceeded : MessageId.NatPunchthroughFailed);
        // PushSuccess and PushFailure (NatPunchthroughClient.cpp:335-348, 1090-1103): 1 for the sender, 0 for the
        // recipient, from the other system, generated locally.
        Assert.Equal([expected, 1], atSender.Data);
        Assert.Equal([expected, 0], atRecipient.Data);
        Assert.True(atSender.IsLocal);
        Assert.True(atRecipient.IsLocal);
        Assert.Equal(recipient.RakNetGuid, atSender.RakNetGuid);
        Assert.Equal(sender.RakNetGuid, atRecipient.RakNetGuid);
        Assert.DoesNotContain(_mesh.Seen(sender).Concat(_mesh.Seen(recipient)), e => e.Id is (byte)MessageId.OutOfBandInternal
            or (byte)MessageId.NatGetMostRecentPort or (byte)MessageId.Timestamp);
        if (succeeds)
        {
            Assert.Equal("198.51.100.2", atSender.Address.ToString(writePort: false));
            Assert.Equal("198.51.100.1", atRecipient.Address.ToString(writePort: false));
        }
    }

    [Theory]
    [InlineData(NatBehaviour.PortRestricted, NatBehaviour.PortRestricted)]
    [InlineData(NatBehaviour.Symmetric, NatBehaviour.FullCone)]
    public void After_success_the_sender_connects_through_both_routers(NatBehaviour senderRouter, NatBehaviour recipientRouter)
    {
        var (sender, recipient) = AddClients(senderRouter, recipientRouter);
        _senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress);
        var (atSender, _) = WaitForResults(sender, recipient);
        Assert.Equal((byte)MessageId.NatPunchthroughSucceeded, atSender.Id);

        // NATCompleteClient: "We can now communicate with the other system, including connecting."
        var connection = _mesh.Connect(sender, atSender.Address, recipient);
        connection.Send(new byte[] { (byte)MessageId.UserPacketEnum, 42 }, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(recipient).Any(e => e.Id == (byte)MessageId.UserPacketEnum), "the message");
        Assert.Equal([(byte)MessageId.UserPacketEnum, 42], _mesh.Seen(recipient).Single(e => e.Id == (byte)MessageId.UserPacketEnum).Data);
    }

    [Fact]
    public void A_recipient_the_facilitator_does_not_know_is_reported_not_connected()
    {
        var (sender, _) = AddClients(NatBehaviour.FullCone, NatBehaviour.FullCone);
        var stranger = new RakNetGuid(12345);

        Assert.True(_senderPlugin.OpenNat(stranger, _facilitator.Sockets[0].LocalAddress));
        _mesh.RunUntil(() => _mesh.Seen(sender).Any(e => e.Id == (byte)MessageId.NatTargetNotConnected), "ID_NAT_TARGET_NOT_CONNECTED");

        // NatPunchthroughServer.cpp:396-407: the ID and the recipient's GUID, which reach the application.
        var notConnected = _mesh.Seen(sender).Single(e => e.Id == (byte)MessageId.NatTargetNotConnected);
        Assert.Equal([(byte)MessageId.NatTargetNotConnected, 0, 0, 0, 0, 0, 0, 0x30, 0x39], notConnected.Data);
        Assert.Equal(_facilitator.RakNetGuid, notConnected.RakNetGuid);
    }

    [Fact]
    public void OpenNat_needs_a_connection_to_the_facilitator()
    {
        var (sender, recipient) = AddClients(NatBehaviour.FullCone, NatBehaviour.FullCone);

        Assert.False(_senderPlugin.OpenNat(recipient.RakNetGuid, new SystemAddress(System.Net.IPAddress.Parse("203.0.113.99"), 1234)));
        Assert.False(_senderPlugin.OpenNat(recipient.RakNetGuid, SystemAddress.Unassigned));
        _ = sender;
    }

    [Fact]
    public void A_second_request_for_a_pair_in_progress_is_refused()
    {
        var (sender, recipient) = AddClients(NatBehaviour.Symmetric, NatBehaviour.Symmetric);
        var facilitator = _facilitator.Sockets[0].LocalAddress;

        _senderPlugin.OpenNat(recipient.RakNetGuid, facilitator);
        _recipientPlugin.OpenNat(sender.RakNetGuid, facilitator);
        _mesh.RunUntil(() => _mesh.Seen(recipient).Any(e => e.Id == (byte)MessageId.NatAlreadyInProgress), "ID_NAT_ALREADY_IN_PROGRESS");

        var refused = _mesh.Seen(recipient).Single(e => e.Id == (byte)MessageId.NatAlreadyInProgress);
        Assert.Equal(1 + sizeof(ulong), refused.Data.Length);
        Assert.Equal(sender.RakNetGuid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(refused.Data.AsSpan(1)));
    }

    [Fact]
    public void A_recipient_without_the_plugin_is_reported_unresponsive_to_both_after_ten_seconds()
    {
        var senderNat = _mesh.Nat("198.51.100.1", NatBehaviour.FullCone);
        var sender = _mesh.Add("10.0.0.2", senderNat, plugins: _senderPlugin);
        var recipient = _mesh.Add("10.0.1.2", _mesh.Nat("198.51.100.2", NatBehaviour.FullCone));
        _mesh.Connect(sender, _facilitator);
        _mesh.Connect(recipient, _facilitator);
        _mesh.Settle();

        _senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress);
        _mesh.RunUntil(() => _mesh.Seen(recipient).Any(e => e.Id == (byte)MessageId.NatGetMostRecentPort), "ID_NAT_GET_MOST_RECENT_PORT");
        var asked = _mesh.Time.GetUtcNow();
        _mesh.RunUntil(() => _mesh.Seen(sender).Any(e => e.Id == (byte)MessageId.NatTargetUnresponsive), "ID_NAT_TARGET_UNRESPONSIVE");

        // NatPunchthroughServer.cpp:170-189: more than 10 s after the attempt started, checked every 250 ms; the ID,
        // the other's GUID and the session ID.
        var waited = _mesh.Time.GetUtcNow() - asked;
        Assert.InRange(waited, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10.5));
        var atSender = _mesh.Seen(sender).Single(e => e.Id == (byte)MessageId.NatTargetUnresponsive).Data;
        Assert.Equal(1 + sizeof(ulong) + sizeof(ushort), atSender.Length);
        Assert.Equal(recipient.RakNetGuid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(atSender.AsSpan(1)));
        _mesh.Settle();
        var atRecipient = _mesh.Seen(recipient).Single(e => e.Id == (byte)MessageId.NatTargetUnresponsive).Data;
        Assert.Equal(sender.RakNetGuid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(atRecipient.AsSpan(1)));
    }

    [Fact]
    public void A_recipient_that_leaves_the_facilitator_is_reported_lost()
    {
        var sender = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", NatBehaviour.FullCone), plugins: _senderPlugin);
        var recipient = _mesh.Add("10.0.1.2", _mesh.Nat("198.51.100.2", NatBehaviour.FullCone));
        _mesh.Connect(sender, _facilitator);
        _mesh.Connect(recipient, _facilitator);
        _mesh.Settle();
        _senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress);
        _mesh.RunUntil(() => _mesh.Seen(recipient).Any(e => e.Id == (byte)MessageId.NatGetMostRecentPort), "ID_NAT_GET_MOST_RECENT_PORT");

        recipient.Close(_facilitator.RakNetGuid);
        _mesh.RunUntil(() => _mesh.Seen(sender).Any(e => e.Id == (byte)MessageId.NatConnectionToTargetLost), "ID_NAT_CONNECTION_TO_TARGET_LOST");

        // NatPunchthroughServer.cpp:315-321: the lost system's GUID and the session ID.
        var lost = _mesh.Seen(sender).Single(e => e.Id == (byte)MessageId.NatConnectionToTargetLost).Data;
        Assert.Equal(recipient.RakNetGuid.Value, System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(lost.AsSpan(1)));
        Assert.Equal(1 + sizeof(ulong) + sizeof(ushort), lost.Length);
    }

    [Fact]
    public void The_facilitator_sends_each_side_the_others_address_internal_addresses_and_guid_at_one_time()
    {
        // Plain peers stand in for the clients, so the facilitator's messages reach the test as they are sent.
        var first = _mesh.Add("192.0.2.10");
        var second = _mesh.Add("192.0.2.11");
        _mesh.Connect(first, _facilitator);
        _mesh.Connect(second, _facilitator);
        var facilitator = _facilitator.Sockets[0].LocalAddress;

        first.Send(Request(MessageId.NatPunchthroughRequest, second.RakNetGuid), _facilitator.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(first).Any(e => e.Id == (byte)MessageId.NatGetMostRecentPort) && _mesh.Seen(second).Any(e => e.Id == (byte)MessageId.NatGetMostRecentPort), "ID_NAT_GET_MOST_RECENT_PORT");
        // NatPunchthroughServer.cpp:611-616: the ID and the session ID, 0 for the first attempt.
        Assert.Equal([(byte)MessageId.NatGetMostRecentPort, 0, 0], _mesh.Seen(first).Single(e => e.Id == (byte)MessageId.NatGetMostRecentPort).Data);

        first.Send(new byte[] { (byte)MessageId.NatGetMostRecentPort, 0, 0, 0x11, 0x11 }, _facilitator.RakNetGuid, Reliability.ReliableOrdered);
        second.Send(new byte[] { (byte)MessageId.NatGetMostRecentPort, 0, 0, 0x22, 0x22 }, _facilitator.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(first).Any(e => e.Id == (byte)MessageId.Timestamp) && _mesh.Seen(second).Any(e => e.Id == (byte)MessageId.Timestamp), "ID_NAT_CONNECT_AT_TIME");

        var atFirst = _mesh.Seen(first).Single(e => e.Id == (byte)MessageId.Timestamp).Data;
        var atSecond = _mesh.Seen(second).Single(e => e.Id == (byte)MessageId.Timestamp).Data;
        var reader = new BitStreamReader(atFirst);
        AssertConnectAtTime(ref reader, sessionId: 0, new SystemAddress(System.Net.IPAddress.Parse("192.0.2.11"), 0x2222), second, weAreSender: true);
        reader = new BitStreamReader(atSecond);
        AssertConnectAtTime(ref reader, sessionId: 0, new SystemAddress(System.Net.IPAddress.Parse("192.0.2.10"), 0x1111), first, weAreSender: false);
        // The same start time for both: four times the larger ping, at least 100 ms ahead.
        Assert.Equal(atFirst[1..9], atSecond[1..9]);
        Assert.Equal(98, atFirst.Length);
        _ = facilitator;
    }

    [Fact]
    public void Bound_addresses_and_pings_answer_as_the_facilitator_saw_them()
    {
        var facilitator = _mesh.Add(["203.0.113.5", "203.0.113.6"], plugins: new NatPunchthroughServer());
        var client = _mesh.Add("192.0.2.10");
        _mesh.Connect(client, facilitator);

        client.Send(new byte[] { (byte)MessageId.NatRequestBoundAddresses }, facilitator.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(client).Any(e => e.Id == (byte)MessageId.NatRespondBoundAddresses), "ID_NAT_RESPOND_BOUND_ADDRESSES");

        // NatPunchthroughServer.cpp:237-261: a count, then each socket's address.
        var response = _mesh.Seen(client).Single(e => e.Id == (byte)MessageId.NatRespondBoundAddresses).Data;
        var reader = new BitStreamReader(response);
        reader.IgnoreBytes(1);
        Assert.True(reader.TryReadByte(out var count));
        Assert.Equal(2, count);
        Assert.True(reader.TryReadSystemAddress(out var firstBound));
        Assert.True(reader.TryReadSystemAddress(out var secondBound));
        Assert.Equal(facilitator.Sockets[0].LocalAddress, firstBound);
        Assert.Equal(facilitator.Sockets[1].LocalAddress, secondBound);

        // NatPunchthroughServer.cpp:266-282: the port in the ping, then the port it came from, back out of band.
        Assert.True(client.LowLevel.SendOutOfBand(secondBound, [(byte)OutOfBandId.NatPing, 0x12, 0x34]));
        _mesh.RunUntil(() => _mesh.Seen(client).Any(e => e.Kind == PeerEventKind.OutOfBand), "ID_NAT_PONG");
        var pong = _mesh.Seen(client).Single(e => e.Kind == PeerEventKind.OutOfBand);
        var clientPort = client.Sockets[0].LocalAddress.Port;
        Assert.Equal([(byte)MessageId.OutOfBandInternal, (byte)OutOfBandId.NatPong, 0x12, 0x34, (byte)(clientPort >> 8), (byte)clientPort], pong.Data);
        Assert.Equal(facilitator.Sockets[0].LocalAddress, pong.Address);
    }

    [Fact]
    public void A_client_behind_a_cone_learns_its_port_stride_from_a_facilitator_on_two_addresses()
    {
        var facilitator = _mesh.Add(["203.0.113.5", "203.0.113.6"], plugins: new NatPunchthroughServer());
        var nat = _mesh.Nat("198.51.100.1", NatBehaviour.FullCone);
        var messages = new List<string>();
        _senderPlugin.DebugMessage = messages.Add;
        var client = _mesh.Add("10.0.0.2", nat, plugins: _senderPlugin);
        _mesh.Connect(client, facilitator);

        _senderPlugin.FindRouterPortStride(facilitator.Sockets[0].LocalAddress);
        _mesh.RunUntil(() => messages.Any(m => m.StartsWith("HAS_PORT_STRIDE", StringComparison.Ordinal)), "HAS_PORT_STRIDE");

        // One mapping for both of the facilitator's addresses: a stride of 0.
        var mapped = Assert.Single(nat.MappedAddresses);
        Assert.Equal($"HAS_PORT_STRIDE 0. First external port {mapped.Port}. Second external port {mapped.Port}.", messages.Single(m => m.StartsWith("HAS_PORT_STRIDE", StringComparison.Ordinal)));
        Assert.DoesNotContain(_mesh.Seen(client), e => e.Kind == PeerEventKind.OutOfBand);
    }

    [Fact]
    public void With_retry_a_failure_is_tried_once_more_and_reported_once()
    {
        _senderPlugin.Configuration.RetryOnFailure = true;
        _recipientPlugin.Configuration.RetryOnFailure = true;
        var serverMessages = new List<string>();
        _server.DebugMessage = serverMessages.Add;
        var (sender, recipient) = AddClients(NatBehaviour.Symmetric, NatBehaviour.Symmetric);

        _senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress);
        WaitForResults(sender, recipient);
        _mesh.Run(TimeSpan.FromSeconds(1));

        Assert.Single(_mesh.Seen(sender), e => e.Id == (byte)MessageId.NatPunchthroughFailed);
        Assert.Single(_mesh.Seen(recipient), e => e.Id == (byte)MessageId.NatPunchthroughFailed);
        // Two attempts, sessions 0 and 1, each sent to both systems.
        Assert.Equal(4, serverMessages.Count(m => m.StartsWith("Sending ID_NAT_CONNECT_AT_TIME", StringComparison.Ordinal)));
    }

    [Fact]
    public void Without_internal_addresses_the_attempt_goes_straight_to_the_external_ports()
    {
        _senderPlugin.Configuration.MaximumNumberOfInternalIdsToCheck = 0;
        _recipientPlugin.Configuration.MaximumNumberOfInternalIdsToCheck = 0;
        var messages = new List<string>();
        _senderPlugin.DebugMessage = messages.Add;
        var (sender, recipient) = AddClients(NatBehaviour.FullCone, NatBehaviour.FullCone);

        _senderPlugin.OpenNat(recipient.RakNetGuid, _facilitator.Sockets[0].LocalAddress);
        var (atSender, _) = WaitForResults(sender, recipient);

        Assert.Equal((byte)MessageId.NatPunchthroughSucceeded, atSender.Id);
        Assert.DoesNotContain(messages, m => m.Contains("TESTING_INTERNAL_IPS", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Send with TTL 2 to", StringComparison.Ordinal));
    }

    [Fact]
    public void Ports_are_reported_for_upnp()
    {
        var nat = _mesh.Nat("198.51.100.1", NatBehaviour.PortRestricted);
        var client = _mesh.Add("10.0.0.2", nat, plugins: _senderPlugin);
        _mesh.Connect(client, _facilitator);

        var (external, @internal) = _senderPlugin.GetUpnpPortMappings(_facilitator.Sockets[0].LocalAddress);

        Assert.Equal(Assert.Single(nat.MappedAddresses).Port, external);
        Assert.Equal(client.Sockets[0].LocalAddress.Port, @internal);
    }

    private (RakPeer Sender, RakPeer Recipient) AddClients(NatBehaviour senderRouter, NatBehaviour recipientRouter)
    {
        var sender = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", senderRouter), plugins: _senderPlugin);
        var recipient = _mesh.Add("10.0.1.2", _mesh.Nat("198.51.100.2", recipientRouter), plugins: _recipientPlugin);
        _mesh.Connect(sender, _facilitator);
        _mesh.Connect(recipient, _facilitator);
        _mesh.Settle();
        return (sender, recipient);
    }

    private (SeenEvent AtSender, SeenEvent AtRecipient) WaitForResults(RakPeer sender, RakPeer recipient)
    {
        static bool IsResult(SeenEvent e) => e.Id is (byte)MessageId.NatPunchthroughSucceeded or (byte)MessageId.NatPunchthroughFailed;
        _mesh.RunUntil(() => _mesh.Seen(sender).Any(IsResult) && _mesh.Seen(recipient).Any(IsResult), "a punchthrough result on both systems");
        return (_mesh.Seen(sender).First(IsResult), _mesh.Seen(recipient).First(IsResult));
    }

    private static byte[] Request(MessageId id, RakNetGuid guid)
    {
        var buffer = new byte[1 + sizeof(ulong)];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)id);
        writer.WriteRakNetGuid(guid);
        return buffer;
    }

    // NatPunchthroughServer.cpp:506-542: ID_TIMESTAMP, the time, ID_NAT_CONNECT_AT_TIME, the session ID, the other's
    // public address with its reported port, its ten internal addresses, its GUID and whether this one is the sender.
    private void AssertConnectAtTime(ref BitStreamReader reader, ushort sessionId, SystemAddress target, RakPeer other, bool weAreSender)
    {
        Assert.True(reader.TryReadByte(out var timestamp));
        Assert.Equal((byte)MessageId.Timestamp, timestamp);
        Assert.True(reader.TryReadUInt64(out var time));
        Assert.InRange(time, _facilitator.GetTime(), _facilitator.GetTime() + 1500);
        Assert.True(reader.TryReadByte(out var id));
        Assert.Equal((byte)MessageId.NatConnectAtTime, id);
        Assert.True(reader.TryReadUInt16(out var session));
        Assert.Equal(sessionId, session);
        Assert.True(reader.TryReadSystemAddress(out var address));
        Assert.Equal(target, address);
        Assert.True(_facilitator.TryGetConnection(other.RakNetGuid, out var connection));
        for (var i = 0; i < RakNetConstants.MaximumNumberOfInternalIds; i++)
        {
            Assert.True(reader.TryReadSystemAddress(out var internalId));
            Assert.Equal(connection.RemoteInternalAddresses[i], internalId);
        }
        Assert.True(reader.TryReadRakNetGuid(out var guid));
        Assert.Equal(other.RakNetGuid, guid);
        Assert.True(reader.TryReadBool(out var sender));
        Assert.Equal(weAreSender, sender);
    }
}
