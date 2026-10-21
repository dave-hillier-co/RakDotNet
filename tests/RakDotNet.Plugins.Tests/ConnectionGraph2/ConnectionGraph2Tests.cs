using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.ConnectionGraph2;

public sealed class ConnectionGraph2Tests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly Plugins.ConnectionGraph2 _serverGraph = new();
    private readonly Plugins.ConnectionGraph2 _firstGraph = new();
    private readonly Plugins.ConnectionGraph2 _secondGraph = new();
    private readonly RakPeer _server;
    private readonly RakPeer _first;
    private readonly RakPeer _second;

    public ConnectionGraph2Tests()
    {
        _server = _mesh.Add(maxIncoming: 4, _serverGraph);
        _first = _mesh.Add(maxIncoming: 4, _firstGraph);
        _second = _mesh.Add(maxIncoming: 4, _secondGraph);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_new_connection_is_announced_to_the_others_and_they_are_sent_to_it()
    {
        _mesh.Connect(_first, _server);
        _mesh.Settle();
        // ConnectionGraph2.cpp:222-236: nobody else is connected, so the first client is sent no list.
        Assert.Empty(RemoteMessages(_first));

        _mesh.Connect(_second, _server);
        _mesh.Settle();

        var announcement = Assert.Single(RemoteMessages(_first));
        Assert.Equal(_server.RakNetGuid, announcement.RakNetGuid);
        AssertList(announcement.Data, (AddressAtServer(_second), _second.RakNetGuid));
        var list = Assert.Single(RemoteMessages(_second));
        AssertList(list.Data, (AddressAtServer(_first), _first.RakNetGuid));
    }

    [Fact]
    public void An_announcement_is_the_count_then_address_guid_and_the_senders_ping()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();

        // ConnectionGraph2.cpp:200-205: ID, uint32 count, SystemAddress (version 4, inverted address, port), GUID, then
        // WriteCasted<uint16_t>(GetAveragePing(guid)).
        var data = Assert.Single(RemoteMessages(_first)).Data;
        Assert.Equal(1 + 4 + 7 + 8 + 2, data.Length);
        Assert.Equal((byte)MessageId.RemoteNewIncomingConnection, data[0]);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(1)));
        Assert.Equal(4, data[5]);
        Assert.Equal([0x80, 0xFF, 0xFF, 0xFE], data[6..10]);
        Assert.Equal(AddressAtServer(_second).Port, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10)));
        Assert.Equal(_second.RakNetGuid.Value, BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(12)));
        Assert.Equal(PingAtServer(_second), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20)));
    }

    [Fact]
    public void Each_system_learns_who_its_participants_are_connected_to()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();

        Assert.Equal([_server.RakNetGuid], _firstGraph.GetParticipantList());
        Assert.Equal(new[] { _first.RakNetGuid, _second.RakNetGuid }.Order(), _serverGraph.GetParticipantList());
        Assert.True(_firstGraph.TryGetConnectionList(_server.RakNetGuid, out var connections));
        var connection = Assert.Single(connections);
        Assert.Equal(_second.RakNetGuid, connection.RakNetGuid);
        Assert.Equal(AddressAtServer(_second), connection.Address);
        Assert.True(_firstGraph.ConnectionExists(_server.RakNetGuid, _second.RakNetGuid));
        Assert.False(_firstGraph.ConnectionExists(_second.RakNetGuid, _server.RakNetGuid));
        Assert.False(_firstGraph.ConnectionExists(_server.RakNetGuid, _server.RakNetGuid));
        // The server's own graph lists what the clients reported: nothing, since neither knows anyone else.
        Assert.True(_serverGraph.TryGetConnectionList(_first.RakNetGuid, out var reported));
        Assert.Empty(reported);
        Assert.False(_firstGraph.TryGetConnectionList(_second.RakNetGuid, out _));
        Assert.False(_firstGraph.TryGetConnectionList(RakNetGuid.Unassigned, out _));
    }

    [Fact]
    public void A_graceful_disconnect_is_announced_with_its_address_and_guid()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();
        var secondAddress = AddressAtServer(_second);

        _second.Close(_server.RakNetGuid, notify: true);
        _mesh.Settle();

        // ConnectionGraph2.cpp:162-169: ID_REMOTE_DISCONNECTION_NOTIFICATION, SystemAddress, RakNetGUID.
        var notice = Assert.Single(_mesh.Seen(_first), e => e.Id == (byte)MessageId.RemoteDisconnectionNotification);
        Assert.Equal(_server.RakNetGuid, notice.RakNetGuid);
        Assert.Equal(AddressAndGuid(MessageId.RemoteDisconnectionNotification, secondAddress, _second.RakNetGuid), notice.Data);
        Assert.False(_firstGraph.ConnectionExists(_server.RakNetGuid, _second.RakNetGuid));
        Assert.Equal([_first.RakNetGuid], _serverGraph.GetParticipantList());
    }

    [Fact]
    public void A_lost_connection_is_announced_as_lost()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();
        var secondAddress = AddressAtServer(_second);

        // A silent close is ID_CONNECTION_LOST, so the plugins see LCR_CONNECTION_LOST (RakPeer.cpp:1667-1678).
        _server.Close(_second.RakNetGuid, notify: false);
        _mesh.Settle();

        var notice = Assert.Single(_mesh.Seen(_first), e => e.Id == (byte)MessageId.RemoteConnectionLost);
        Assert.Equal(AddressAndGuid(MessageId.RemoteConnectionLost, secondAddress, _second.RakNetGuid), notice.Data);
        Assert.False(_firstGraph.ConnectionExists(_server.RakNetGuid, _second.RakNetGuid));
    }

    [Fact]
    public void Without_auto_processing_nothing_is_announced_until_a_participant_is_added()
    {
        _serverGraph.AutoProcessNewConnections = false;
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();
        Assert.Empty(RemoteMessages(_first));
        Assert.Empty(_serverGraph.GetParticipantList());

        _serverGraph.AddParticipant(AddressAtServer(_second), _second.RakNetGuid);
        _mesh.Settle();

        AssertList(Assert.Single(RemoteMessages(_first)).Data, (AddressAtServer(_second), _second.RakNetGuid));
        AssertList(Assert.Single(RemoteMessages(_second)).Data, (AddressAtServer(_first), _first.RakNetGuid));
        Assert.Equal([_second.RakNetGuid], _serverGraph.GetParticipantList());
        // The first client is not a participant of the server, so what it reports is ignored there.
        Assert.False(_serverGraph.TryGetConnectionList(_first.RakNetGuid, out _));
    }

    [Fact]
    public void Pings_between_systems_come_from_the_peer_or_from_what_was_reported()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();

        Assert.Equal(0, _firstGraph.GetPingBetweenSystems(_server.RakNetGuid, _server.RakNetGuid));
        Assert.Equal(PingAt(_first, _server), _firstGraph.GetPingBetweenSystems(_first.RakNetGuid, _server.RakNetGuid));
        Assert.Equal(PingAt(_first, _server), _firstGraph.GetPingBetweenSystems(_server.RakNetGuid, _first.RakNetGuid));
        Assert.Equal(PingAtServer(_second), _firstGraph.GetPingBetweenSystems(_server.RakNetGuid, _second.RakNetGuid));
        // ConnectionGraph2.cpp:91-102: (uint16_t)-1 when nothing is known.
        Assert.Equal(ushort.MaxValue, _firstGraph.GetPingBetweenSystems(_second.RakNetGuid, _server.RakNetGuid));
    }

    [Fact]
    public void The_lowest_average_ping_system_is_this_one_unless_a_participant_averages_strictly_lower()
    {
        Assert.Equal(_server.RakNetGuid, _serverGraph.GetLowestAveragePingSystem());

        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();

        // The server reported the second client to the first with the same ping the first has to the server, so the
        // server does not average strictly lower (ConnectionGraph2.cpp:150).
        Assert.Equal(PingAt(_first, _server), PingAtServer(_second));
        Assert.Equal(_first.RakNetGuid, _firstGraph.GetLowestAveragePingSystem());
    }

    [Fact]
    public void A_truncated_message_from_a_participant_reaches_the_application_and_changes_nothing()
    {
        _mesh.Connect(_first, _server);
        _mesh.Connect(_second, _server);
        _mesh.Settle();
        Assert.True(_firstGraph.TryGetConnectionList(_server.RakNetGuid, out var before));
        var stranger = new RakNetGuid(0x77);

        // Three systems listed, one complete and the next cut in its GUID.
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
        writer.WriteUInt32(3);
        writer.WriteAddress(AddressAtServer(_second));
        writer.WriteRakNetGuid(stranger);
        writer.WriteUInt16(5);
        writer.WriteAddress(AddressAtServer(_second));
        writer.WriteByte(0);
        var removal = AddressAndGuid(MessageId.RemoteDisconnectionNotification, AddressAtServer(_second), _second.RakNetGuid);
        var lost = AddressAndGuid(MessageId.RemoteConnectionLost, AddressAtServer(_second), _second.RakNetGuid);
        byte[][] truncated =
        [
            [(byte)MessageId.RemoteNewIncomingConnection],
            [(byte)MessageId.RemoteNewIncomingConnection, 0, 0],
            writer.Written.ToArray(),
            [(byte)MessageId.RemoteDisconnectionNotification],
            removal[..^1],
            lost[..5],
        ];
        foreach (var message in truncated)
            _server.Send(message, _first.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.True(_firstGraph.TryGetConnectionList(_server.RakNetGuid, out var after));
        Assert.Equal(before, after);
        Assert.False(_firstGraph.ConnectionExists(_server.RakNetGuid, stranger));
        // ConnectionGraph2.cpp:258-306: every message continues to the application.
        Assert.Equal(1 + 3, RemoteMessages(_first).Count);
        Assert.Equal(2, _mesh.Seen(_first).Count(e => e.Id == (byte)MessageId.RemoteDisconnectionNotification));
        Assert.Single(_mesh.Seen(_first), e => e.Id == (byte)MessageId.RemoteConnectionLost);
    }

    private List<SeenEvent> RemoteMessages(RakPeer peer) =>
        [.. _mesh.Seen(peer).Where(e => e.Id == (byte)MessageId.RemoteNewIncomingConnection)];

    // The address the server sees the client at, which is the address it announces.
    private SystemAddress AddressAtServer(RakPeer client) =>
        _server.TryGetConnection(client.RakNetGuid, out var connection) ? connection.Address : SystemAddress.Unassigned;

    private ushort PingAtServer(RakPeer client) => PingAt(_server, client);

    private static ushort PingAt(RakPeer peer, RakPeer remote) =>
        peer.TryGetConnection(remote.RakNetGuid, out var connection) && connection.AveragePing is { } ping
            ? (ushort)ping.TotalMilliseconds
            : ushort.MaxValue;

    private static byte[] AddressAndGuid(MessageId id, SystemAddress address, RakNetGuid guid)
    {
        var buffer = new byte[1 + 7 + 8];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)id);
        writer.WriteAddress(address);
        writer.WriteRakNetGuid(guid);
        return buffer;
    }

    private static void AssertList(byte[] data, params (SystemAddress Address, RakNetGuid Guid)[] expected)
    {
        var reader = new BitStreamReader(data);
        Assert.True(reader.TryReadByte(out var id));
        Assert.Equal((byte)MessageId.RemoteNewIncomingConnection, id);
        Assert.True(reader.TryReadUInt32(out var count));
        Assert.Equal((uint)expected.Length, count);
        foreach (var (address, guid) in expected)
        {
            Assert.True(reader.TryReadSystemAddress(out var readAddress));
            Assert.True(reader.TryReadRakNetGuid(out var readGuid));
            Assert.True(reader.TryReadUInt16(out _));
            Assert.Equal(address, readAddress);
            Assert.Equal(guid, readGuid);
        }
        Assert.Equal(0, reader.BitsRemaining);
    }
}
