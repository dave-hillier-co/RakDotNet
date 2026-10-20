using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.MessageFilter;

public sealed class MessageFilterTests : IDisposable
{
    private const byte User = (byte)MessageId.UserPacketEnum;

    private readonly PluginMesh _mesh = new();
    private readonly Plugins.MessageFilter _filter = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;

    public MessageFilterTests()
    {
        _server = _mesh.Add(maxIncoming: 4, _filter);
        _client = _mesh.Add(maxIncoming: 0);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void The_sample_filter_passes_only_what_the_senders_filter_set_allows_and_follows_a_change_of_set()
    {
        // Samples/MessageFilter/MessageFilterTest.cpp.
        _filter.AutoAddNewConnectionsToFilter = 0;
        _filter.SetAllowMessageId(true, User, User, 0);
        _filter.SetAllowMessageId(true, User + 1, User + 1, 1);
        var connection = _mesh.Connect(_client, _server);

        connection.Send(new byte[] { User + 1 }, Reliability.ReliableOrdered);
        connection.Send(new byte[] { User }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal([User], UserMessages(_server));
        var sender = _mesh.Seen(_server).Single(e => e.Id == User);
        _filter.SetSystemFilterSet(sender.RakNetGuid, 1);

        connection.Send(new byte[] { User + 1 }, Reliability.ReliableOrdered);
        connection.Send(new byte[] { User }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal([User, User + 1], UserMessages(_server));
    }

    [Fact]
    public void A_system_in_no_filter_set_is_not_filtered()
    {
        var connection = _mesh.Connect(_client, _server);

        connection.Send(new byte[] { User + 5 }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal([User + 5], UserMessages(_server));
        Assert.Equal(-1, _filter.GetSystemFilterSet(connection.Peer.RakNetGuid));
    }

    [Fact]
    public void Connection_notifications_pass_a_filter_set_that_allows_nothing()
    {
        _filter.AutoAddNewConnectionsToFilter = 3;
        var connection = _mesh.Connect(_client, _server);

        connection.Close();
        _mesh.Settle();

        Assert.Contains(_mesh.Seen(_server), e => e.Id == (byte)MessageId.NewIncomingConnection);
        Assert.Contains(_mesh.Seen(_server), e => e.Id == (byte)MessageId.DisconnectionNotification);
    }

    [Fact]
    public void A_new_connection_joins_the_auto_add_filter_set_and_leaves_it_when_it_closes()
    {
        _filter.AutoAddNewConnectionsToFilter = 2;
        var connection = _mesh.Connect(_client, _server);

        Assert.Equal(2, _filter.GetSystemFilterSet(_client.RakNetGuid));
        Assert.Equal(1, _filter.GetSystemCount(2));

        connection.Close();
        _mesh.RunUntil(() => _mesh.Seen(_server).Any(e => e.Id == (byte)MessageId.DisconnectionNotification), "the disconnection");

        Assert.Equal(-1, _filter.GetSystemFilterSet(_client.RakNetGuid));
        Assert.Equal(0, _filter.GetSystemCount());
    }

    [Fact]
    public void A_disallowed_message_is_reported_and_its_sender_kicked_and_banned_as_the_filter_set_says()
    {
        var reported = new List<(FilteredSystem System, int FilterSetId, byte MessageId)>();
        _filter.AutoAddNewConnectionsToFilter = 0;
        _filter.SetActionOnDisallowedMessage(kickOnDisallowed: true, banOnDisallowed: true, TimeSpan.FromMinutes(1), 0);
        _filter.SetDisallowedMessageCallback(0, (system, set, id) => reported.Add((system, set, id)));
        var connection = _mesh.Connect(_client, _server);
        var clientAddress = _server.Connections.Single().Address;

        connection.Send(new byte[] { User + 9 }, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(_client).Any(e => e.Id == (byte)MessageId.DisconnectionNotification), "the kick");

        Assert.Empty(UserMessages(_server));
        Assert.Equal([(new FilteredSystem(_client.RakNetGuid, clientAddress), 0, (byte)(User + 9))], reported);
        // MessageFilter.cpp:245-250: the sender's IP, without its port.
        Assert.True(_server.BanList.IsBanned(clientAddress.ToString(writePort: false)));
    }

    [Fact]
    public void A_timestamped_message_is_filtered_on_the_byte_after_a_four_byte_time()
    {
        // MessageFilter.cpp:327: sizeof(MessageID) + sizeof(RakNet::TimeMS), though RakPeer writes an 8-byte time.
        _filter.AutoAddNewConnectionsToFilter = 0;
        _filter.SetAllowMessageId(true, User, User, 0);
        var connection = _mesh.Connect(_client, _server);
        // The time is shifted by the clock differential before plugins see it (RakPeer.cpp:1549-1555); 0x80 below byte 5
        // keeps that byte as sent.
        var allowedAtFive = new byte[] { (byte)MessageId.Timestamp, 0, 0, 0, 0, User, 0x80, 0, 0, User + 1 };
        var allowedAtNine = new byte[] { (byte)MessageId.Timestamp, 0, 0, 0, 0, 1, 0x80, 0, 0, User };

        connection.Send(allowedAtFive, Reliability.ReliableOrdered);
        connection.Send(allowedAtNine, Reliability.ReliableOrdered);
        _mesh.Settle();

        var timestamped = _mesh.Seen(_server).Where(e => e.Id == (byte)MessageId.Timestamp).ToList();
        var delivered = Assert.Single(timestamped);
        Assert.Equal(User + 1, delivered.Data[9]);
    }

    [Fact]
    public void A_timestamped_message_shorter_than_five_bytes_is_dropped_even_from_an_unfiltered_system()
    {
        var connection = _mesh.Connect(_client, _server);

        connection.Send(new byte[] { (byte)MessageId.Timestamp, 1, 2, 3 }, Reliability.ReliableOrdered);
        connection.Send(new byte[] { User }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == (byte)MessageId.Timestamp);
        Assert.Equal([User], UserMessages(_server));
    }

    [Fact]
    public void An_rpc4_call_passes_only_when_its_function_is_allowed()
    {
        _filter.AutoAddNewConnectionsToFilter = 0;
        _filter.SetAllowRpc4(true, "Allowed", 0);
        var connection = _mesh.Connect(_client, _server);

        connection.Send(Rpc4Call("Allowed"), Reliability.ReliableOrdered);
        connection.Send(Rpc4Call("Other"), Reliability.ReliableOrdered);
        _mesh.Settle();

        var calls = _mesh.Seen(_server).Where(e => e.Id == (byte)MessageId.RpcPlugin).ToList();
        Assert.Equal([Rpc4Call("Allowed")], calls.Select(c => c.Data));
    }

    [Fact]
    public void Disallowing_the_last_rpc4_function_disallows_the_rpc_plugin_id()
    {
        _filter.AutoAddNewConnectionsToFilter = 0;
        _filter.SetAllowRpc4(true, "A", 0);
        _filter.SetAllowRpc4(false, "A", 0);
        var connection = _mesh.Connect(_client, _server);

        connection.Send(new byte[] { (byte)MessageId.RpcPlugin, 0 }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == (byte)MessageId.RpcPlugin);
    }

    [Fact]
    public void A_system_that_outstays_its_filter_set_is_reported_kicked_and_removed()
    {
        var timedOut = new List<(FilteredSystem System, int FilterSetId)>();
        _filter.AutoAddNewConnectionsToFilter = 1;
        _filter.SetFilterMaxTime(TimeSpan.FromSeconds(1), banOnExceed: false, TimeSpan.Zero, 1);
        _filter.SetTimeoutCallback(1, (system, set) => timedOut.Add((system, set)));
        _mesh.Connect(_client, _server);
        var clientAddress = _server.Connections.Single().Address;

        _mesh.RunUntil(() => _mesh.Seen(_client).Any(e => e.Id == (byte)MessageId.DisconnectionNotification), "the kick");

        Assert.Equal([(new FilteredSystem(_client.RakNetGuid, clientAddress), 1)], timedOut);
        Assert.Equal(-1, _filter.GetSystemFilterSet(_client.RakNetGuid));
    }

    [Fact]
    public void The_time_limit_is_checked_two_seconds_apart()
    {
        // MessageFilter.cpp:262, 296: a check runs once the time passes the last check by more than 1000 ms, and the
        // next one is set 1000 ms after the check.
        var timedOutAt = new List<ulong>();
        _filter.AutoAddNewConnectionsToFilter = 1;
        _filter.SetTimeoutCallback(1, (_, _) => timedOutAt.Add(_server.GetTime()));
        _mesh.Connect(_client, _server);
        var joined = _server.GetTime();
        _filter.SetFilterMaxTime(TimeSpan.FromMilliseconds(1), banOnExceed: false, TimeSpan.Zero, 1);

        _mesh.RunUntil(() => timedOutAt.Count > 0, "the timeout");

        Assert.InRange(timedOutAt[0] - joined, 1ul, 2_001ul);
    }

    [Fact]
    public void A_system_added_by_address_is_not_found_from_its_packets()
    {
        // DataStructures::Hash keyed by AddressOrGUID::ToInteger: a packet's key hashes by its GUID.
        _filter.SetAllowMessageId(true, User, User, 0);
        var connection = _mesh.Connect(_client, _server);
        var clientAddress = _server.Connections.Single().Address;
        _filter.SetSystemFilterSet(clientAddress, 0);

        connection.Send(new byte[] { User + 1 }, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal(0, _filter.GetSystemFilterSet(clientAddress));
        Assert.Equal(-1, _filter.GetSystemFilterSet(_client.RakNetGuid));
        Assert.Equal([User + 1], UserMessages(_server));
    }

    [Fact]
    public void Filter_sets_are_listed_in_id_order_and_deleting_one_frees_its_systems()
    {
        _filter.SetAllowMessageId(true, User, User, 7);
        _filter.SetAllowMessageId(true, User, User, 3);
        _filter.AutoAddNewConnectionsToFilter = 7;
        _mesh.Connect(_client, _server);

        Assert.Equal(2, _filter.FilterSetCount);
        Assert.Equal([3, 7], Enumerable.Range(0, _filter.FilterSetCount).Select(_filter.GetFilterSetIdByIndex));
        Assert.Equal(1, _filter.GetSystemCount(7));

        _filter.DeleteFilterSet(7);

        Assert.Equal(1, _filter.FilterSetCount);
        Assert.Equal(0, _filter.GetSystemCount());
        Assert.Equal(-1, _filter.GetSystemFilterSet(_client.RakNetGuid));
    }

    [Fact]
    public void Moving_a_system_to_a_negative_filter_set_removes_it()
    {
        _filter.AutoAddNewConnectionsToFilter = 0;
        _mesh.Connect(_client, _server);

        _filter.SetSystemFilterSet(_client.RakNetGuid, -1);

        Assert.Equal(-1, _filter.GetSystemFilterSet(_client.RakNetGuid));
        Assert.Equal(1, _filter.FilterSetCount);
    }

    private static byte[] Rpc4Call(string function)
    {
        var writer = new BitStreamWriter(new byte[64]);
        writer.WriteByte((byte)MessageId.RpcPlugin);
        writer.WriteByte(0);
        writer.WriteCompressedRakString(function.AsSpan());
        return writer.Written.ToArray();
    }

    private List<byte> UserMessages(RakPeer peer) =>
        [.. _mesh.Seen(peer).Where(e => e.Kind == PeerEventKind.Message && e.Id >= User).Select(e => e.Id)];
}
