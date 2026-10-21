using System.Net;
using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// CloudClient and CloudServer, from Samples/CloudTest (two servers that know each other, each with a client) and
/// Samples/CloudClient with Samples/CloudServer (a directory server listing its clients and its own load). The rows each
/// client decodes must be what RakNet's servers return: their order, data, addresses and GUIDs.
/// </summary>
/// <remarks>
/// With two servers, the theory's client kind is client 1 and server 2, and its server kind is server 1 and client 2, so
/// in a mixed combination every link, server to server included, joins a native and a RakDotNet peer. Server 2 and
/// client 1 reach server 1 through the proxy; client 2 connects to server 2 directly. Requests on different links are
/// not ordered, so a client's own get after its post shows the post has landed.
/// </remarks>
[Collection(OneScenarioAtATime.Name)]
public sealed class CloudScenarios
{
    private static readonly CloudKey Key1 = new("ApplicationName", 1);
    private static readonly CloudKey Key2 = new("ApplicationName", 2);
    private static readonly CloudKey ConnectionCount = new("CloudConnCount", 0);
    private static readonly CloudKey ClientList = new("CC_Sample_PK", 0);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_get_on_one_server_returns_its_own_rows_then_the_other_servers(PeerKind client, PeerKind server)
    {
        await using var cloud = await CloudPair.StartAsync(client, server);

        // CloudTest 'A', 'B' and 'C'.
        await cloud.Client1.PostAsync(cloud.Server1, Key1, "DS1C1S1\0");
        await cloud.Client2.PostAsync(cloud.Server2, Key1, "DS1C2S2\0");
        await cloud.Client1.PostAsync(cloud.Server1, Key2, "DS2C2S1\0");

        // 'D': ProcessAndTransmitGetRequest writes its own rows, then server 2's (CloudServer.cpp:1165-1248).
        var fromServer1 = await cloud.Client1.GetAsync(cloud.Server1, [Key1, Key2]);
        Assert.Equal([Key1, Key2], fromServer1.Keys);
        Assert.Equal((0u, 0u, false, false), (fromServer1.StartingRowIndex, fromServer1.MaxRowsToReturn, fromServer1.QuerySubscribe, fromServer1.SubscribeToResults));
        Assert.Collection(fromServer1.Rows,
            r => cloud.AssertRow(r, Key1, "DS1C1S1\0", cloud.Client1, cloud.Server1),
            r => cloud.AssertRow(r, Key2, "DS2C2S1\0", cloud.Client1, cloud.Server1),
            r => cloud.AssertRow(r, Key1, "DS1C2S2\0", cloud.Client2, cloud.Server2));

        var fromServer2 = await cloud.Client2.GetAsync(cloud.Server2, [Key1, Key2]);
        Assert.Collection(fromServer2.Rows,
            r => cloud.AssertRow(r, Key1, "DS1C2S2\0", cloud.Client2, cloud.Server2),
            r => cloud.AssertRow(r, Key1, "DS1C1S1\0", cloud.Client1, cloud.Server1),
            r => cloud.AssertRow(r, Key2, "DS2C2S1\0", cloud.Client1, cloud.Server1));

        // Both servers name the same ipList[0], as they run on one machine.
        Assert.Equal(cloud.Server1Ip, cloud.Server2Ip);
        Assert.Equal([cloud.Server2.Guid], (await cloud.Server1.InfoAsync()).RemoteServers);
        Assert.Equal([cloud.Server1.Guid], (await cloud.Server2.InfoAsync()).RemoteServers);
        cloud.AssertNoCloudRequestsReachedTheApplication();
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_client_subscribed_on_one_server_is_told_of_changes_on_the_other(PeerKind client, PeerKind server)
    {
        await using var cloud = await CloudPair.StartAsync(client, server);
        await cloud.Client1.PostAsync(cloud.Server1, Key1, "DS1C1S1\0");
        await cloud.Client2.PostAsync(cloud.Server2, Key1, "DS1C2S2\0");
        await cloud.Client1.PostAsync(cloud.Server1, Key2, "DS2C2S1\0");

        // CloudTest 'F': client 2 subscribes to client 1's data sets. Server 2 adds an empty row for client 1 under key 1,
        // which its own query returns since key 1 has an uploader there (CloudServer.cpp:646-676, 1269-1293).
        var subscribed = await cloud.Client2.GetAsync(cloud.Server2, [Key1, Key2], subscribe: true, specificSystems: [cloud.Client1.Guid]);
        Assert.True(subscribed.SubscribeToResults);
        Assert.Collection(subscribed.Rows,
            r => Assert.Equal(new CloudRowRecord(Key1.PrimaryKey, 1, "", Unassigned, Unassigned, cloud.Server2.Guid, cloud.Client1.Guid), r),
            r => cloud.AssertRow(r, Key1, "DS1C1S1\0", cloud.Client1, cloud.Server1),
            r => cloud.AssertRow(r, Key2, "DS2C2S1\0", cloud.Client1, cloud.Server1));

        // 'A' again, then 'E': server 1 tells server 2, which tells client 2 with server 1's row.
        await cloud.Client1.Cloud.CloudPostAsync(cloud.Server1.Guid, Key1, Encoding.ASCII.GetBytes("v2"), Token);
        await cloud.Client2.WaitForAsync(i => i.Notifications.Count == 1, "the update notification");
        await cloud.Client1.Cloud.CloudReleaseAsync(cloud.Server1.Guid, [Key1, Key2], Token);
        var notified = await cloud.Client2.WaitForAsync(i => i.Notifications.Count == 3, "the release notifications");
        Assert.Equal([(true, Key1, "v2"), (false, Key1, "v2"), (false, Key2, "DS2C2S1\0")],
            notified.Notifications.Select(n => (n.WasUpdated, n.Row.Key, Text(n.Row))));
        Assert.All(notified.Notifications, n => cloud.AssertRow(n.Row, n.Row.Key, Text(n.Row), cloud.Client1, cloud.Server1));

        // 'G': after unsubscribing, a post from client 1 reaches client 2 no more. Client 1's get goes through server 2
        // after the change on the server link, and client 2's get answer follows anything server 2 would have sent it.
        await cloud.Client2.Cloud.CloudUnsubscribeAsync(cloud.Server2.Guid, [Key1, Key2], [cloud.Client1.Guid], Token);
        await cloud.Client2.GetAsync(cloud.Server2, [Key2]);
        await cloud.Client1.PostAsync(cloud.Server1, Key1, "v3");
        var after = await cloud.Client2.GetAsync(cloud.Server2, [Key1]);
        Assert.Equal(["DS1C2S2\0", "v3"], after.Rows.Select(Text));
        Assert.Equal(3, (await cloud.Client2.InfoAsync()).Notifications.Count);
        cloud.AssertNoCloudRequestsReachedTheApplication();
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Queries_page_through_the_servers_own_rows_then_the_other_servers(PeerKind client, PeerKind server)
    {
        await using var cloud = await CloudPair.StartAsync(client, server, server2Client: true);
        await cloud.Server1.Cloud.CloudServerSetMaxUploadBytesPerClientAsync(8, Token);

        await cloud.Client1.PostAsync(cloud.Server1, Key1, "local");
        // Over the limit: dropped silently (CloudServer.cpp:247-248).
        await cloud.Client1.PostAsync(cloud.Server1, Key2, "123456789");
        await cloud.Client2.PostAsync(cloud.Server2, Key1, "remote");
        // CloudServerHelper posts through the server's own CloudClient to its own GUID.
        await cloud.Server2.PostAsync(cloud.Server2, Key1, "server");

        var remote = new[] { cloud.Client2.Guid, cloud.Server2.Guid }.Order().ToArray();
        ulong[][] expected = [[cloud.Client1.Guid, .. remote], [remote[0]], [remote[1]], [cloud.Client1.Guid, remote[0]], []];
        ulong[][] actual = [await RowsAsync(0, 0), await RowsAsync(1, 1), await RowsAsync(2, 5), await RowsAsync(0, 2), await RowsAsync(3, 5)];
        Assert.Equal(expected, actual);
        Assert.Empty((await cloud.Client1.GetAsync(cloud.Server1, [Key2])).Rows);

        // The server's own row: from ipList[0] by loopback, so both addresses are ipList[0] (CloudServer.cpp:343-352).
        var own = (await cloud.Client1.GetAsync(cloud.Server1, [Key1])).Rows.Single(r => r.ClientGuid == cloud.Server2.Guid);
        var server2Address = $"{cloud.Server2Ip}:{cloud.Server2.Port}";
        Assert.Equal(new CloudRowRecord(Key1.PrimaryKey, 1, Hex("server"), server2Address, server2Address, cloud.Server2.Guid, cloud.Server2.Guid), own);

        async Task<ulong[]> RowsAsync(uint start, uint max) =>
            [.. (await cloud.Client1.GetAsync(cloud.Server1, [Key1], start, max)).Rows.Select(r => r.ClientGuid)];
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_directory_server_lists_its_clients_and_its_load(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var host = new CloudPeer(session.ServerPeer);
        await host.Cloud.AttachCloudServerAsync(Token);
        await host.Cloud.AttachCloudClientAsync(Token);
        // CloudServerHelperFilter: only the server may post its connection count.
        await host.Cloud.CloudServerSetFilterAsync(ConnectionCount.PrimaryKey, host.Guid, Token);
        // OnJoinCloudResult with no other server posts a count of 0 (CloudServerHelper.cpp:506-511).
        await host.PostAsync(host, ConnectionCount, [0, 0]);

        var first = new CloudPeer(session.ClientPeer);
        var second = new CloudPeer(await session.AddClientAsync(client, Token));
        foreach (var (joining, count) in new[] { (first, 1), (second, 2) })
        {
            await joining.Cloud.AttachCloudClientAsync(Token);
            await session.ConnectAsync(joining.Peer, Token);
            // OnConnectionCountChange on ID_NEW_INCOMING_CONNECTION.
            await host.PostAsync(host, ConnectionCount, [0, (byte)count]);

            // Samples/CloudClient on ID_CONNECTION_REQUEST_ACCEPTED: UploadInstanceToCloud, GetClientSubscription, GetServers.
            await joining.Cloud.CloudPostAsync(host.Guid, ClientList, HelloWorld, Token);
            var clients = await joining.GetAsync(host, [ClientList], subscribe: true);
            var servers = await joining.GetAsync(host, [ConnectionCount]);

            Assert.True(clients.SubscribeToResults);
            Assert.Equal(new[] { first, second }.Take(count).Select(c => c.Guid).Order(), clients.Rows.Select(r => r.ClientGuid));
            var self = clients.Rows.Single(r => r.ClientGuid == joining.Guid);
            Assert.Equal(new CloudRowRecord(ClientList.PrimaryKey, 0, Convert.ToHexStringLower(HelloWorld), $"{await host.LocalIpAsync()}:{session.ProxyEndPoint.Port}",
                                            Text(session.AddressAtServer(joining.Peer)), host.Guid, joining.Guid), self);
            var load = Assert.Single(servers.Rows);
            Assert.Equal((Hex([0, (byte)count]), host.Guid, host.Guid), (load.Data, load.ClientGuid, load.ServerGuid));
        }

        // The first client's subscription reports the second.
        var joined = await first.WaitForAsync(i => i.Notifications.Count == 1, "the new client notification");
        Assert.True(joined.Notifications[0].WasUpdated);
        Assert.Equal((second.Guid, Convert.ToHexStringLower(HelloWorld)), (joined.Notifications[0].Row.ClientGuid, joined.Notifications[0].Row.Data));

        // A client may not post the connection count; the filter sees the attempt.
        await second.Cloud.CloudPostAsync(host.Guid, ConnectionCount, new byte[] { 0x7f, 0x7f }, Token);
        Assert.Equal(Hex([0, 2]), Assert.Single((await second.GetAsync(host, [ConnectionCount])).Rows).Data);
        var calls = (await host.InfoAsync()).FilterCalls;
        Assert.Contains(new CloudFilterCall("post", second.Guid, Text(session.AddressAtServer(second.Peer)), [ConnectionCount], "7f7f", []), calls, CallComparer);
        Assert.Contains(calls, c => c.Op == "get" && c.ClientGuid == first.Guid && c.Keys.SequenceEqual([ClientList]));

        // A client that leaves is released, and its subscribers told with its last row.
        await second.Peer.Driver.CloseAsync(host.Guid, notify: true, Token);
        var left = await first.WaitForAsync(i => i.Notifications.Count == 2, "the lost client notification");
        Assert.False(left.Notifications[1].WasUpdated);
        Assert.Equal((second.Guid, Convert.ToHexStringLower(HelloWorld)), (left.Notifications[1].Row.ClientGuid, left.Notifications[1].Row.Data));
        Assert.Equal([first.Guid], (await first.GetAsync(host, [ClientList])).Rows.Select(r => r.ClientGuid));
    }

    // BitStream::Write("Hello World") in UploadInstanceToCloud: a RakString.
    private static readonly byte[] HelloWorld = [0x00, 0x0b, .. "Hello World"u8];

    private const string Unassigned = "UNASSIGNED_SYSTEM_ADDRESS";

    private static readonly IEqualityComparer<CloudFilterCall> CallComparer = EqualityComparer<CloudFilterCall>.Create(
        (a, b) => a!.Op == b!.Op && a.ClientGuid == b.ClientGuid && a.ClientAddress == b.ClientAddress && a.Keys.SequenceEqual(b.Keys) && a.Data == b.Data
                  && a.SpecificSystems.SequenceEqual(b.SpecificSystems),
        c => c.Op.GetHashCode(StringComparison.Ordinal));

    private static string Text(CloudRowRecord row) => Encoding.ASCII.GetString(Convert.FromHexString(row.Data));

    private static string Text(IPEndPoint endPoint) => $"{endPoint.Address}:{endPoint.Port}";

    private static string Hex(string text) => Convert.ToHexStringLower(Encoding.ASCII.GetBytes(text));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>A peer of a scenario and its cloud commands.</summary>
    private sealed class CloudPeer(InteropPeer peer)
    {
        public InteropPeer Peer { get; } = peer;

        public ICloudDriver Cloud => (ICloudDriver)Peer.Driver;

        public ulong Guid => Peer.Started.PeerGuid;

        public int Port => Peer.Started.BoundAddress.Port;

        public Task<CloudInfo> InfoAsync() => Cloud.GetCloudInfoAsync(Token);

        public async Task<string> LocalIpAsync() => (await InfoAsync()).LocalIp;

        /// <summary>Posts, then gets the key from the same server, so the post has landed when this returns.</summary>
        public async Task PostAsync(CloudPeer server, CloudKey key, byte[] data)
        {
            await Cloud.CloudPostAsync(server.Guid, key, data, Token);
            await GetAsync(server, [key]);
        }

        public Task PostAsync(CloudPeer server, CloudKey key, string data) => PostAsync(server, key, Encoding.ASCII.GetBytes(data));

        public async Task<CloudGetRecord> GetAsync(CloudPeer server, IReadOnlyList<CloudKey> keys, uint start = 0, uint max = 0, bool subscribe = false,
                                                   IReadOnlyList<ulong>? specificSystems = null)
        {
            var before = (await InfoAsync()).Gets.Count;
            await Cloud.CloudGetAsync(server.Guid, keys, start, max, subscribe, specificSystems, Token);
            return (await WaitForAsync(i => i.Gets.Count > before, "the get response")).Gets[before];
        }

        public async Task<CloudInfo> WaitForAsync(Func<CloudInfo, bool> condition, string expectation)
        {
            var deadline = DateTime.UtcNow + InteropSession.DefaultTimeout;
            while (true)
            {
                var info = await InfoAsync();
                if (condition(info))
                    return info;
                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"Timed out waiting at {Peer.Label} for {expectation}; saw {info.Gets.Count} gets and {info.Notifications.Count} notifications.");
                await Task.Delay(20, Token);
            }
        }
    }

    /// <summary>Samples/CloudTest's four systems: two servers that added each other, each with a client.</summary>
    private sealed class CloudPair : IAsyncDisposable
    {
        private readonly InteropSession _session;

        private CloudPair(InteropSession session, CloudPeer server2, CloudPeer client1, CloudPeer client2)
        {
            _session = session;
            Server1 = new CloudPeer(session.ServerPeer);
            Server2 = server2;
            Client1 = client1;
            Client2 = client2;
        }

        public CloudPeer Server1 { get; }

        public CloudPeer Server2 { get; }

        public CloudPeer Client1 { get; }

        public CloudPeer Client2 { get; }

        /// <summary>GetLocalIP(0) of each server.</summary>
        public string Server1Ip { get; private set; } = "";

        public string Server2Ip { get; private set; } = "";

        public static async Task<CloudPair> StartAsync(PeerKind client, PeerKind server, bool server2Client = false)
        {
            var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
            try
            {
                var server2 = new CloudPeer(session.ClientPeer);
                var client1 = new CloudPeer(await session.AddClientAsync(client, Token));
                var client2 = new CloudPeer(await session.AddClientAsync(server, Token));
                var pair = new CloudPair(session, server2, client1, client2);
                await pair.Server1.Cloud.AttachCloudServerAsync(Token);
                await server2.Cloud.AttachCloudServerAsync(Token);
                if (server2Client)
                    await server2.Cloud.AttachCloudClientAsync(Token);
                await client1.Cloud.AttachCloudClientAsync(Token);
                await client2.Cloud.AttachCloudClientAsync(Token);

                await session.ConnectAsync(Token);
                await session.ConnectAsync(client1.Peer, Token);
                await ConnectDirectAsync(client2, server2);
                // CloudTest tells each server about the other while neither holds anything.
                await pair.Server1.Cloud.CloudServerAddServerAsync(server2.Guid, Token);
                await server2.Cloud.CloudServerAddServerAsync(pair.Server1.Guid, Token);
                pair.Server1Ip = await pair.Server1.LocalIpAsync();
                pair.Server2Ip = await server2.LocalIpAsync();
                return pair;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Checks a row from a client connected to <paramref name="server"/>. Client 1 reaches server 1 through the proxy,
        /// so server 1 sees it at its proxy address and is seen at the proxy's; client 2 and server 2 see each other
        /// directly. The server's loopback address becomes its ipList[0] (CloudServer.cpp:343-347).
        /// </summary>
        public void AssertRow(CloudRowRecord row, CloudKey key, string data, CloudPeer client, CloudPeer server)
        {
            var (clientAddress, serverPort) = client == Client1
                ? (Text(_session.AddressAtServer(client.Peer)), _session.ProxyEndPoint.Port)
                : ($"127.0.0.1:{client.Port}", server.Port);
            var localIp = server == Server1 ? Server1Ip : Server2Ip;
            Assert.Equal(new CloudRowRecord(key.PrimaryKey, key.SecondaryKey, Hex(data), $"{localIp}:{serverPort}", clientAddress, server.Guid, client.Guid), row);
        }

        // Every cloud request is consumed by the servers (CloudServer.cpp:188-238).
        public void AssertNoCloudRequestsReachedTheApplication() =>
            Assert.DoesNotContain(new[] { Server1, Server2 }.SelectMany(s => s.Peer.Inbox.Unconsumed), p => p.MessageId is
                (byte)MessageId.CloudPostRequest or (byte)MessageId.CloudReleaseRequest or (byte)MessageId.CloudGetRequest
                or (byte)MessageId.CloudUnsubscribeRequest or (byte)MessageId.CloudServerToServerCommand);

        public ValueTask DisposeAsync() => _session.DisposeAsync();

        private static async Task ConnectDirectAsync(CloudPeer client, CloudPeer server)
        {
            Assert.Equal("CONNECTION_ATTEMPT_STARTED", await client.Peer.Driver.ConnectAsync(new Harness.ConnectOptions(server.Peer.Started.BoundAddress), Token));
            await client.Peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED",
                                                       InteropSession.DefaultTimeout, Token);
            await server.Peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == client.Guid,
                                                       "ID_NEW_INCOMING_CONNECTION", InteropSession.DefaultTimeout, Token);
        }
    }
}
