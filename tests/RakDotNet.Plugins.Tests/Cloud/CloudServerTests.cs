using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Cloud;

/// <summary>
/// CloudServer.cpp with real CloudClients: posts, queries, subscriptions and releases on one server, and two servers
/// sharing them as Samples/CloudTest does.
/// </summary>
public sealed class CloudServerTests : IDisposable
{
    private static readonly CloudKey Key1 = new("ApplicationName", 1);
    private static readonly CloudKey Key2 = new("ApplicationName", 2);

    private readonly PluginMesh _mesh = new();

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_get_returns_what_a_client_posted_with_the_server_holding_it_and_the_client_that_posted_it()
    {
        var server = AddServer();
        var client = AddClient(server);

        client.Cloud.Post(Key1, "DS1C1S1\0"u8, server.Guid);
        var result = Get(client, server, new CloudQuery { Keys = { Key1 } });

        var row = Assert.Single(result.Rows);
        Assert.Equal(Key1, row.Key);
        Assert.Equal("DS1C1S1\0"u8.ToArray(), row.Data.ToArray());
        Assert.Equal((server.Guid, client.Guid), (row.ServerGuid, row.ClientGuid));
        Assert.Equal(server.AddressOf(client), row.ClientSystemAddress);
        // GetExternalID is the server's loopback address, so ipList[0] takes its place (CloudServer.cpp:343-347).
        Assert.Equal(server.Peer.InternalAddresses[0].WithPort(server.Port), row.ServerSystemAddress);
        Assert.Equal([Key1], result.Query.Keys);
        Assert.False(result.SubscribeToResults);
    }

    [Fact]
    public void Posting_under_a_key_again_replaces_the_data()
    {
        var server = AddServer();
        var client = AddClient(server);

        client.Cloud.Post(Key1, "first"u8, server.Guid);
        client.Cloud.Post(Key1, "second"u8, server.Guid);

        Assert.Equal("second"u8.ToArray(), Assert.Single(Get(client, server, new CloudQuery { Keys = { Key1 } }).Rows).Data.ToArray());
    }

    [Fact]
    public void Rows_come_in_the_order_of_the_query_keys_then_by_client_GUID()
    {
        var server = AddServer();
        var first = AddClient(server);
        var second = AddClient(server);
        first.Cloud.Post(Key1, "1a"u8, server.Guid);
        second.Cloud.Post(Key1, "1b"u8, server.Guid);
        first.Cloud.Post(Key2, "2a"u8, server.Guid);
        second.Cloud.Post(Key2, "2b"u8, server.Guid);
        _mesh.Settle();

        var result = Get(first, server, new CloudQuery { Keys = { Key2, Key1 } });

        var byGuid = new[] { first, second }.OrderBy(c => c.Guid.Value).ToArray();
        Assert.Equal(
            [(Key2, byGuid[0].Guid), (Key2, byGuid[1].Guid), (Key1, byGuid[0].Guid), (Key1, byGuid[1].Guid)],
            result.Rows.Select(r => (r.Key, r.ClientGuid)));
    }

    [Fact]
    public void The_starting_row_and_the_row_limit_page_through_the_rows()
    {
        var server = AddServer();
        var first = AddClient(server);
        var second = AddClient(server);
        first.Cloud.Post(Key1, "0"u8, server.Guid);
        second.Cloud.Post(Key1, "0"u8, server.Guid);
        first.Cloud.Post(Key2, "0"u8, server.Guid);
        second.Cloud.Post(Key2, "0"u8, server.Guid);
        _mesh.Settle();
        var all = Get(first, server, new CloudQuery { Keys = { Key1, Key2 } }).Rows;

        // ProcessAndTransmitGetRequest (CloudServer.cpp:1179-1205); the starting row applies with no limit too.
        Assert.Equal(all.Skip(1).Take(2), Get(first, server, Query(1, 2)).Rows, RowIdentity);
        Assert.Equal(all.Skip(3), Get(first, server, Query(3, 5)).Rows, RowIdentity);
        Assert.Empty(Get(first, server, Query(4, 1)).Rows);
        Assert.Equal(all.Skip(2), Get(first, server, Query(2, 0)).Rows, RowIdentity);

        static CloudQuery Query(uint start, uint max) => new() { Keys = { Key1, Key2 }, StartingRowIndex = start, MaxRowsToReturn = max };
    }

    [Fact]
    public void A_subscriber_is_told_of_each_post_and_of_the_release_with_the_last_value()
    {
        var server = AddServer();
        var poster = AddClient(server);
        var subscriber = AddClient(server);
        var subscribed = Get(subscriber, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true });
        Assert.Empty(subscribed.Rows);
        Assert.True(subscribed.SubscribeToResults);

        poster.Cloud.Post(Key1, "a"u8, server.Guid);
        poster.Cloud.Post(Key1, "b"u8, server.Guid);
        poster.Cloud.Release([Key1], server.Guid);
        _mesh.Settle();

        Assert.Equal([("a", true), ("b", true), ("b", false)], subscriber.Notifications.Select(n => (Text(n.Row), n.WasUpdated)));
        Assert.All(subscriber.Notifications, n => Assert.Equal((Key1, poster.Guid, server.Guid), (n.Row.Key, n.Row.ClientGuid, n.Row.ServerGuid)));
        Assert.Empty(Get(subscriber, server, new CloudQuery { Keys = { Key1 } }).Rows);
    }

    [Fact]
    public void A_subscription_to_specific_systems_is_told_only_of_their_posts()
    {
        var server = AddServer();
        var wanted = AddClient(server);
        var other = AddClient(server);
        var subscriber = AddClient(server);
        Get(subscriber, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true }, [wanted.Guid]);

        other.Cloud.Post(Key1, "other"u8, server.Guid);
        wanted.Cloud.Post(Key1, "wanted"u8, server.Guid);
        _mesh.Settle();

        Assert.Equal([("wanted", true)], subscriber.Notifications.Select(n => (Text(n.Row), n.WasUpdated)));
    }

    [Fact]
    public void A_specific_subscription_adds_an_empty_row_that_later_queries_return()
    {
        var server = AddServer();
        var absent = AddClient(server);
        var subscriber = AddClient(server);
        var poster = AddClient(server);
        Get(subscriber, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true }, [absent.Guid]);

        poster.Cloud.Post(Key1, "posted"u8, server.Guid);
        var rows = Get(poster, server, new CloudQuery { Keys = { Key1 } }).Rows;

        // OnGetRequest allocates CloudData for each specific system (CloudServer.cpp:646-676), and
        // ProcessCloudQueryWithAddresses returns every row once the key has an uploader (CloudServer.cpp:1269-1293).
        Assert.Equal(new[] { absent.Guid, poster.Guid }.Order(), rows.Select(r => r.ClientGuid));
        var empty = rows.Single(r => r.ClientGuid == absent.Guid);
        Assert.Equal((0, SystemAddress.Unassigned, SystemAddress.Unassigned, server.Guid),
            (empty.Data.Length, empty.ServerSystemAddress, empty.ClientSystemAddress, empty.ServerGuid));
    }

    [Fact]
    public void Unsubscribing_stops_the_notifications_but_not_for_specific_systems_of_a_subscription_to_all()
    {
        var server = AddServer();
        var poster = AddClient(server);
        var specific = AddClient(server);
        var everyone = AddClient(server);
        Get(specific, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true }, [poster.Guid]);
        Get(everyone, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true });

        specific.Cloud.Unsubscribe([Key1], [poster.Guid], server.Guid);
        // UnsubscribeFromKey refuses to remove specific systems from a subscription to every system (CloudServer.cpp:1586-1588).
        everyone.Cloud.Unsubscribe([Key1], [poster.Guid], server.Guid);
        _mesh.Settle();
        poster.Cloud.Post(Key1, "x"u8, server.Guid);
        _mesh.Settle();

        Assert.Empty(specific.Notifications);
        Assert.Single(everyone.Notifications);

        everyone.Cloud.Unsubscribe([Key1], server.Guid);
        _mesh.Settle();
        poster.Cloud.Post(Key1, "y"u8, server.Guid);
        _mesh.Settle();
        Assert.Single(everyone.Notifications);
    }

    [Fact]
    public void A_client_that_disconnects_has_its_rows_released()
    {
        var server = AddServer();
        var leaving = AddClient(server);
        var subscriber = AddClient(server);
        Get(subscriber, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true });
        leaving.Cloud.Post(Key1, "bye"u8, server.Guid);
        _mesh.Settle();

        leaving.Peer.Close(server.Guid, notify: true);
        _mesh.RunUntil(() => subscriber.Notifications.Count == 2, "the release notification");

        // OnClosedConnection (CloudServer.cpp:921-932).
        Assert.Equal([("bye", true), ("bye", false)], subscriber.Notifications.Select(n => (Text(n.Row), n.WasUpdated)));
        Assert.Empty(Get(subscriber, server, new CloudQuery { Keys = { Key1 } }).Rows);
    }

    [Fact]
    public void A_specific_subscriber_that_disconnects_takes_its_empty_rows_with_it()
    {
        var server = AddServer();
        var absent = AddClient(server);
        var subscriber = AddClient(server);
        var poster = AddClient(server);
        Get(subscriber, server, new CloudQuery { Keys = { Key1, Key2 }, SubscribeToResults = true }, [absent.Guid]);

        subscriber.Peer.Close(server.Guid, notify: true);
        _mesh.RunUntil(() => !server.Peer.TryGetConnection(subscriber.Guid, out _), "the subscriber disconnected");
        poster.Cloud.Post(Key1, "posted"u8, server.Guid);

        // RakNet leaves the rows it allocated for the subscription (CloudServer.cpp:957-991); they are released here.
        var rows = Get(poster, server, new CloudQuery { Keys = { Key1, Key2 } }).Rows;
        Assert.Equal([poster.Guid], rows.Select(r => r.ClientGuid));
    }

    [Fact]
    public void Subscriptions_are_limited_per_client_by_default()
    {
        Assert.Equal(CloudServer.DefaultMaxSubscriptionsPerClient, new CloudServer().MaxSubscriptionsPerClient);
        Assert.True(CloudServer.DefaultMaxSubscriptionsPerClient > 0);
    }

    [Fact]
    public void A_get_that_would_take_a_client_past_its_subscription_limit_is_dropped()
    {
        var server = AddServer();
        server.Cloud.MaxSubscriptionsPerClient = 4;
        var client = AddClient(server);
        var (a, b, c) = (new RakNetGuid(1), new RakNetGuid(2), new RakNetGuid(3));

        // Each key subscribed to specific systems counts once per system; two keys of three systems is six.
        client.Cloud.Get(new CloudQuery { Keys = { Key1, Key2 }, SubscribeToResults = true }, [a, b, c], server.Guid);
        _mesh.Settle();
        Assert.Empty(client.Gets);

        Get(client, server, new CloudQuery { Keys = { Key1, Key2 }, SubscribeToResults = true }, [a, b]);
        // Subscribing to a key again replaces its subscription, so its count is taken out first.
        Get(client, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true }, [a, b]);
        Get(client, server, new CloudQuery { Keys = { Key1 }, SubscribeToResults = true });
        // A subscription to every system counts once: 2 + 1 + 1 is four, and one more is over.
        Get(client, server, new CloudQuery { Keys = { new CloudKey("ApplicationName", 3) }, SubscribeToResults = true });
        var count = client.Gets.Count;
        client.Cloud.Get(new CloudQuery { Keys = { new CloudKey("ApplicationName", 4) }, SubscribeToResults = true }, ReadOnlySpan<RakNetGuid>.Empty, server.Guid);
        _mesh.Settle();
        Assert.Equal(count, client.Gets.Count);

        // A get that does not subscribe is not limited, and 0 lifts the limit.
        Get(client, server, new CloudQuery { Keys = { new CloudKey("ApplicationName", 4) } }, [a, b, c]);
        server.Cloud.MaxSubscriptionsPerClient = 0;
        Get(client, server, new CloudQuery { Keys = { new CloudKey("ApplicationName", 4) }, SubscribeToResults = true }, [a, b, c]);
    }

    [Fact]
    public void Posts_beyond_the_upload_limit_per_client_are_dropped()
    {
        var server = AddServer();
        server.Cloud.MaxUploadBytesPerClient = 8;
        var client = AddClient(server);

        client.Cloud.Post(Key1, "123456789"u8, server.Guid);
        Assert.Empty(Get(client, server, new CloudQuery { Keys = { Key1 } }).Rows);
        client.Cloud.Post(Key1, "12345678"u8, server.Guid);
        // Replacing counts the bytes being replaced out first (CloudServer.cpp:382-396).
        client.Cloud.Post(Key1, "87654321"u8, server.Guid);
        client.Cloud.Post(Key2, "1"u8, server.Guid);

        var rows = Get(client, server, new CloudQuery { Keys = { Key1, Key2 } }).Rows;
        Assert.Equal(["87654321"], rows.Select(Text));
    }

    [Fact]
    public void A_new_post_over_the_limit_drops_the_rows_others_posted_under_the_key_as_RakNet_does()
    {
        var server = AddServer();
        server.Cloud.MaxUploadBytesPerClient = 4;
        var full = AddClient(server);
        var other = AddClient(server);
        full.Cloud.Post(Key1, "1234"u8, server.Guid);
        other.Cloud.Post(Key2, "x"u8, server.Guid);

        _mesh.Settle();
        full.Cloud.Post(Key2, "y"u8, server.Guid);
        _mesh.Settle();

        // The undo removes the key's whole list (CloudServer.cpp:315-320).
        Assert.Empty(Get(other, server, new CloudQuery { Keys = { Key2 } }).Rows);
        Assert.Single(Get(other, server, new CloudQuery { Keys = { Key1 } }).Rows);
    }

    [Fact]
    public void Filters_reject_requests_and_see_what_each_client_sent()
    {
        var server = AddServer();
        var filter = new RecordingFilter { RejectPrimaryKey = "Protected" };
        server.Cloud.AddQueryFilter(filter);
        server.Cloud.AddQueryFilter(filter);
        var client = AddClient(server);
        var protectedKey = new CloudKey("Protected", 0);

        client.Cloud.Post(protectedKey, "no"u8, server.Guid);
        client.Cloud.Post(Key1, "yes"u8, server.Guid);
        var rows = Get(client, server, new CloudQuery { Keys = { protectedKey, Key1 } }).Rows;
        client.Cloud.Get(new CloudQuery { Keys = { protectedKey } }, server.Guid);
        client.Cloud.Release([protectedKey], server.Guid);
        client.Cloud.Unsubscribe([protectedKey], server.Guid);
        _mesh.Settle();

        Assert.Equal(["yes"], rows.Select(Text));
        // A rejected get has no response; a filter added twice is asked once.
        Assert.Single(client.Gets);
        Assert.Equal(
            [$"post {client.Guid} Protected,0 6e6f", $"post {client.Guid} ApplicationName,1 796573", $"get {client.Guid} Protected,0;ApplicationName,1", $"get {client.Guid} Protected,0", $"release {client.Guid} Protected,0", $"unsubscribe {client.Guid} Protected,0"],
            filter.Calls);
    }

    [Fact]
    public void A_filter_may_change_the_query_the_server_answers()
    {
        var server = AddServer();
        server.Cloud.AddQueryFilter(new RecordingFilter { OnGet = q => q.Keys.Remove(Key2) });
        var client = AddClient(server);
        client.Cloud.Post(Key1, "1"u8, server.Guid);
        client.Cloud.Post(Key2, "2"u8, server.Guid);

        var result = Get(client, server, new CloudQuery { Keys = { Key1, Key2 } });

        Assert.Equal([Key1], result.Query.Keys);
        Assert.Equal(["1"], result.Rows.Select(Text));
    }

    [Fact]
    public void A_server_posting_to_itself_reports_its_own_address()
    {
        // CloudServerHelper::OnConnectionCountChange posts through a CloudClient on the server to its own GUID.
        var selfClient = new CloudClient();
        var server = AddServer(selfClient);
        var client = AddClient(server);

        selfClient.Post(new CloudKey("CloudConnCount", 0), [0, 1], server.Guid);
        var row = Assert.Single(Get(client, server, new CloudQuery { Keys = { new CloudKey("CloudConnCount", 0) } }).Rows);

        // Loopback arrives from ipList[0]; with no connection to it GetExternalID is unassigned, which is a loopback
        // address, so the server is ipList[0] with the bound port.
        Assert.Equal((server.Guid, server.Guid), (row.ServerGuid, row.ClientGuid));
        Assert.Equal(server.Peer.InternalAddresses[0], row.ClientSystemAddress);
        Assert.Equal(server.Peer.InternalAddresses[0].WithPort(server.Port), row.ServerSystemAddress);
    }

    [Fact]
    public void A_forced_external_address_takes_the_port_the_client_sees()
    {
        var server = AddServer();
        var forced = new SystemAddress(System.Net.IPAddress.Parse("203.0.113.9"), 1);
        server.Cloud.ForcedExternalAddress = forced;
        var client = AddClient(server);

        client.Cloud.Post(Key1, "x"u8, server.Guid);

        Assert.Equal(forced.WithPort(server.Port), Assert.Single(Get(client, server, new CloudQuery { Keys = { Key1 } }).Rows).ServerSystemAddress);
    }

    [Fact]
    public void A_get_on_one_server_returns_the_rows_of_another_after_its_own()
    {
        // Samples/CloudTest: 'A', 'B', 'C', then 'D'.
        var (server1, server2) = AddServerPair();
        var client1 = AddClient(server1);
        var client2 = AddClient(server2);

        client1.Cloud.Post(Key1, "DS1C1S1\0"u8, server1.Guid);
        client2.Cloud.Post(Key1, "DS1C2S2\0"u8, server2.Guid);
        client1.Cloud.Post(Key2, "DS2C2S1\0"u8, server1.Guid);
        _mesh.Settle();
        var rows = Get(client1, server1, new CloudQuery { Keys = { Key1, Key2 } }).Rows;

        Assert.Equal(
            [(Key1, client1.Guid, server1.Guid, "DS1C1S1\0"), (Key2, client1.Guid, server1.Guid, "DS2C2S1\0"), (Key1, client2.Guid, server2.Guid, "DS1C2S2\0")],
            rows.Select(r => (r.Key, r.ClientGuid, r.ServerGuid, Text(r))));
        Assert.Equal(server2.AddressOf(client2), rows[2].ClientSystemAddress);
    }

    [Fact]
    public void A_subscriber_on_one_server_is_told_of_posts_and_releases_on_another()
    {
        // Samples/CloudTest: 'F' subscribes client 2 to client 1's data, then 'A' and 'E'.
        var (server1, server2) = AddServerPair();
        var client1 = AddClient(server1);
        var client2 = AddClient(server2);
        client1.Cloud.Post(Key1, "DS1C1S1\0"u8, server1.Guid);
        client2.Cloud.Post(Key1, "DS1C2S2\0"u8, server2.Guid);
        client1.Cloud.Post(Key2, "DS2C2S1\0"u8, server1.Guid);
        _mesh.Settle();

        var subscribed = Get(client2, server2, new CloudQuery { Keys = { Key1, Key2 }, SubscribeToResults = true }, [client1.Guid]);
        // Server 2's empty row for client 1 under key 1, then server 1's two rows.
        Assert.Equal(
            [(Key1, client1.Guid, server2.Guid, ""), (Key1, client1.Guid, server1.Guid, "DS1C1S1\0"), (Key2, client1.Guid, server1.Guid, "DS2C2S1\0")],
            subscribed.Rows.Select(r => (r.Key, r.ClientGuid, r.ServerGuid, Text(r))));

        client1.Cloud.Post(Key1, "v2"u8, server1.Guid);
        client1.Cloud.Release([Key1, Key2], server1.Guid);
        _mesh.RunUntil(() => client2.Notifications.Count == 3, "three notifications");

        Assert.Equal(
            [(Key1, "v2", true), (Key1, "v2", false), (Key2, "DS2C2S1\0", false)],
            client2.Notifications.Select(n => (n.Row.Key, Text(n.Row), n.WasUpdated)));
        Assert.All(client2.Notifications, n => Assert.Equal((client1.Guid, server1.Guid), (n.Row.ClientGuid, n.Row.ServerGuid)));
    }

    [Fact]
    public void Paging_continues_into_the_rows_of_other_servers()
    {
        var serverClient = new CloudClient();
        var (server1, server2) = AddServerPair(server2Client: serverClient);
        var client1 = AddClient(server1);
        var client2 = AddClient(server2);
        client1.Cloud.Post(Key1, "local"u8, server1.Guid);
        client2.Cloud.Post(Key1, "remote"u8, server2.Guid);
        serverClient.Post(Key1, "remote"u8, server2.Guid);
        _mesh.Settle();
        var remote = new[] { client2.Guid, server2.Guid }.Order().ToArray();

        Assert.Equal([client1.Guid, .. remote], Get(client1, server1, Query(0, 0)).Rows.Select(r => r.ClientGuid));
        Assert.Equal([remote[0]], Get(client1, server1, Query(1, 1)).Rows.Select(r => r.ClientGuid));
        Assert.Equal([remote[1]], Get(client1, server1, Query(2, 5)).Rows.Select(r => r.ClientGuid));
        Assert.Equal([client1.Guid, remote[0]], Get(client1, server1, Query(0, 2)).Rows.Select(r => r.ClientGuid));

        static CloudQuery Query(uint start, uint max) => new() { Keys = { Key1 }, StartingRowIndex = start, MaxRowsToReturn = max };
    }

    [Fact]
    public void A_server_that_sent_its_keys_is_asked_only_for_the_keys_it_holds()
    {
        var recorder = new ServerCommandRecorder();
        var server1 = AddServer();
        var server2 = AddServer(recorder);
        var client1 = AddClient(server1);
        var client2 = AddClient(server2);
        client2.Cloud.Post(Key2, "held"u8, server2.Guid);
        _mesh.Settle();
        ConnectServers(server1, server2);
        // Server 1 adds server 2 first, so it accepts the keys server 2 sends when it adds server 1 (CloudServer.cpp:1300-1328).
        server1.Cloud.AddServer(server2.Guid);
        _mesh.Settle();
        server2.Cloud.AddServer(server1.Guid);
        _mesh.Settle();

        Get(client1, server1, new CloudQuery { Keys = { Key1 } });
        var held = Get(client1, server1, new CloudQuery { Keys = { Key2 } });

        Assert.Equal(["held"], held.Rows.Select(Text));
        Assert.Single(recorder.Commands, c => c == 0);
    }

    [Fact]
    public void A_query_waiting_for_a_server_that_disconnects_is_answered_with_the_rows_at_hand()
    {
        var (server1, server2) = AddServerPair();
        var client1 = AddClient(server1);
        client1.Cloud.Post(Key1, "local"u8, server1.Guid);
        _mesh.Settle();
        server2.Peer.Plugins.Detach(server2.Cloud);
        _mesh.Settle();

        client1.Cloud.Get(new CloudQuery { Keys = { Key1 } }, server1.Guid);
        _mesh.Settle();
        Assert.Empty(client1.Gets);
        server2.Peer.Close(server1.Guid, notify: true);
        _mesh.RunUntil(() => client1.Gets.Count == 1, "the get response");

        Assert.Equal(["local"], client1.Gets[0].Rows.Select(Text));
    }

    [Fact]
    public void With_three_queries_waiting_the_oldest_response_misses_its_query_as_RakNet_does()
    {
        var (server1, server2) = AddServerPair();
        var client1 = AddClient(server1);
        _mesh.Settle();

        client1.Cloud.Get(new CloudQuery { Keys = { Key1 }, StartingRowIndex = 1 }, server1.Guid);
        client1.Cloud.Get(new CloudQuery { Keys = { Key1 }, StartingRowIndex = 2 }, server1.Guid);
        client1.Cloud.Get(new CloudQuery { Keys = { Key1 }, StartingRowIndex = 3 }, server1.Guid);
        _mesh.Settle();

        // GetRequestComp never says a key is greater (CloudServer.cpp:82-89), so request 0 is not found among three.
        Assert.Equal([2u, 3u], client1.Gets.Select(g => g.Query.StartingRowIndex));
        server2.Peer.Close(server1.Guid, notify: true);
        _mesh.RunUntil(() => client1.Gets.Count == 3, "the last get response");
        Assert.Equal(1u, client1.Gets[2].Query.StartingRowIndex);
    }

    [Fact]
    public void Servers_are_added_only_while_connected_and_can_be_removed()
    {
        var (server1, server2) = AddServerPair();

        server1.Cloud.AddServer(new RakNetGuid(12345));
        Assert.Equal([server2.Guid], server1.Cloud.RemoteServers);

        server1.Cloud.RemoveServer(server2.Guid);
        Assert.Empty(server1.Cloud.RemoteServers);
    }

    [Fact]
    public void Clear_forgets_every_row()
    {
        var server = AddServer();
        var client = AddClient(server);
        client.Cloud.Post(Key1, "x"u8, server.Guid);
        _mesh.Settle();

        server.Cloud.Clear();

        Assert.Empty(Get(client, server, new CloudQuery { Keys = { Key1 } }).Rows);
    }

    [Fact]
    public void Malformed_requests_and_server_commands_from_clients_are_consumed_and_ignored()
    {
        var server = AddServer();
        var client = AddClient(server);
        var raw = _mesh.Add(0);
        ConnectTo(raw, server.Peer);
        Send(raw, server, "7300016b00000001000000ff6162");               // A post of 255 bytes holding 2.
        Send(raw, server, "75dfffe0");                                   // A get claiming 65535 keys.
        Send(raw, server, "7800c0002000206b000000010000" + "00000000"); // A forwarded get from a system that is no server.
        Send(raw, server, "7807" + "80");                                 // A data change cut short.
        Send(raw, server, "78");                                         // A server command with no command byte.
        _mesh.Settle();

        // Nothing was stored, nothing was answered, and the server still serves its clients.
        Assert.DoesNotContain(_mesh.Seen(raw), e => e.Kind == PeerEventKind.Message);
        Assert.DoesNotContain(_mesh.Seen(server.Peer), e => e.Id is >= (byte)MessageId.CloudPostRequest and <= (byte)MessageId.CloudSubscriptionNotification);
        Assert.Empty(Get(client, server, new CloudQuery { Keys = { new CloudKey("k", 1) } }).Rows);
    }

    private static void Send(RakPeer from, TestServer to, string hex) =>
        from.Send(Convert.FromHexString(hex), to.Guid, Reliability.ReliableOrdered, Priority.High, 0);

    private static string Text(CloudQueryRow row) => System.Text.Encoding.ASCII.GetString(row.Data.Span);

    private static readonly IEqualityComparer<CloudQueryRow> RowIdentity =
        EqualityComparer<CloudQueryRow>.Create((a, b) => a!.Key == b!.Key && a.ClientGuid == b.ClientGuid, r => r.ClientGuid.GetHashCode());

    private CloudQueryResult Get(TestClient client, TestServer server, CloudQuery query, RakNetGuid[]? specificSystems = null)
    {
        var count = client.Gets.Count;
        client.Cloud.Get(query, specificSystems ?? [], server.Guid);
        _mesh.RunUntil(() => client.Gets.Count > count, "the get response");
        return client.Gets[count];
    }

    private TestServer AddServer(params IRakPlugin[] extra)
    {
        var cloud = new CloudServer();
        var peer = _mesh.Add(4, [.. extra, cloud]);
        return new TestServer(peer, cloud);
    }

    private (TestServer, TestServer) AddServerPair(CloudClient? server2Client = null)
    {
        var server1 = AddServer();
        var server2 = server2Client is null ? AddServer() : AddServer(server2Client);
        ConnectServers(server1, server2);
        // Samples/CloudTest tells each server about the other.
        server1.Cloud.AddServer(server2.Guid);
        server2.Cloud.AddServer(server1.Guid);
        _mesh.Settle();
        return (server1, server2);
    }

    private void ConnectServers(TestServer server1, TestServer server2) => ConnectTo(server2.Peer, server1.Peer);

    private TestClient AddClient(TestServer server)
    {
        var cloud = new CloudClient();
        var client = new TestClient(_mesh.Add(0, cloud), cloud, _mesh);
        ConnectTo(client.Peer, server.Peer);
        return client;
    }

    private void ConnectTo(RakPeer client, RakPeer server)
    {
        int Connected(RakPeer peer) => _mesh.Seen(peer).Count(e => e.Kind == PeerEventKind.Connected);
        var (clientBefore, serverBefore) = (Connected(client), Connected(server));
        Assert.Equal(ConnectAttemptResult.Started, client.Connect(server.Sockets[0].LocalAddress));
        _mesh.RunUntil(() => Connected(client) > clientBefore && Connected(server) > serverBefore, "connected");
    }

    private sealed record TestServer(RakPeer Peer, CloudServer Cloud)
    {
        public RakNetGuid Guid => Peer.RakNetGuid;

        public ushort Port => Peer.Sockets[0].LocalAddress.Port;

        public SystemAddress AddressOf(TestClient client) => Peer.GetAddressFromGuid(client.Guid);
    }

    private sealed class TestClient(RakPeer peer, CloudClient cloud, PluginMesh mesh)
    {
        public RakPeer Peer { get; } = peer;

        public CloudClient Cloud { get; } = cloud;

        public RakNetGuid Guid => Peer.RakNetGuid;

        public List<CloudQueryResult> Gets =>
            [.. Messages(MessageId.CloudGetResponse).Select(d => CloudClient.TryReadGetResponse(d, out var r) ? r : throw new InvalidOperationException("Malformed get response"))];

        public List<(CloudQueryRow Row, bool WasUpdated)> Notifications =>
            [.. Messages(MessageId.CloudSubscriptionNotification).Select(d => CloudClient.TryReadSubscriptionNotification(d, out var u, out var r) ? (r, u) : throw new InvalidOperationException("Malformed notification"))];

        private IEnumerable<byte[]> Messages(MessageId id) => mesh.Seen(Peer).Where(e => e.Id == (byte)id).Select(e => e.Data);
    }

    /// <summary>A filter that records each call and rejects requests touching one primary key.</summary>
    private sealed class RecordingFilter : ICloudServerQueryFilter
    {
        public string? RejectPrimaryKey { get; init; }

        public Action<CloudQuery>? OnGet { get; init; }

        public List<string> Calls { get; } = [];

        public bool OnPostRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudKey key, ReadOnlySpan<byte> data)
        {
            Calls.Add($"post {clientGuid} {key} {Convert.ToHexStringLower(data)}");
            return Allowed([key]);
        }

        public bool OnReleaseRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys)
        {
            Calls.Add($"release {clientGuid} {string.Join(';', keys)}");
            return Allowed(keys);
        }

        public bool OnGetRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudQuery query, List<RakNetGuid> specificSystems)
        {
            Calls.Add($"get {clientGuid} {string.Join(';', query.Keys)}");
            OnGet?.Invoke(query);
            return query.Keys.Count != 1 || Allowed(query.Keys);
        }

        public bool OnUnsubscribeRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys, List<RakNetGuid> specificSystems)
        {
            Calls.Add($"unsubscribe {clientGuid} {string.Join(';', keys)}");
            return Allowed(keys);
        }

        private bool Allowed(List<CloudKey> keys) => !keys.Any(k => k.PrimaryKey == RejectPrimaryKey);
    }

    /// <summary>Records the command byte of each ID_CLOUD_SERVER_TO_SERVER_COMMAND before the server consumes it.</summary>
    private sealed class ServerCommandRecorder : IRakPlugin
    {
        public List<byte> Commands { get; } = [];

        public PluginResult OnEvent(PeerEvent e)
        {
            if (e.Data is [(byte)MessageId.CloudServerToServerCommand, var command, ..])
                Commands.Add(command);
            return PluginResult.Continue;
        }
    }
}
