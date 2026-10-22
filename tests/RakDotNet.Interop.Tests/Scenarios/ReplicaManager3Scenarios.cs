using System.Buffers.Binary;
using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// ReplicaManager3 replicating the same objects between the two systems. From Samples/ReplicaManager3: a server and a
/// client each create the four client/server sample replicas ('C'), change the variables of those they serialize
/// ('R', with fixed values instead of random ones), destroy theirs ('D') and disconnect; two peers do the same with
/// P2PReplica. From Samples/ReplicatedLogin: clients log in, the server creates a User for each, and deletes it when its
/// client leaves.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ReplicaManager3Scenarios
{
    private const string ClientCreatibleClientSerialized = "ClientCreatible_ClientSerialized";
    private const string ServerCreatedClientSerialized = "ServerCreated_ClientSerialized";
    private const string ClientCreatibleServerSerialized = "ClientCreatible_ServerSerialized";
    private const string ServerCreatedServerSerialized = "ServerCreated_ServerSerialized";

    private static readonly string[] SampleKinds = [ClientCreatibleClientSerialized, ServerCreatedClientSerialized, ClientCreatibleServerSerialized, ServerCreatedServerSerialized];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Client_and_server_replicate_the_sample_objects_by_their_topology_rules(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var clientRm3 = (IReplicaManager3Driver)session.Client;
        var serverRm3 = (IReplicaManager3Driver)session.Server;
        await serverRm3.AttachReplicaManager3Async("SERVER", cancellationToken: Token);
        await clientRm3.AttachReplicaManager3Async("CLIENT", cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // 'C' on both, each creating its four in one update.
        var serverIds = SampleKinds.Zip(await serverRm3.CreateReplicasAsync(SampleKinds, cancellationToken: Token)).ToDictionary();
        var clientIds = SampleKinds.Zip(await clientRm3.CreateReplicasAsync(SampleKinds, cancellationToken: Token)).ToDictionary();

        // The server constructs all four to the client; the client only those clients may create (ClientCreatible_*).
        var atClient = await WaitForAsync(clientRm3, i => i.Replicas.Count == 8 && i.Connections.All(c => c.DownloadComplete && c.Constructed.Count == 6), "the client to have its four and the server's four");
        var atServer = await WaitForAsync(serverRm3, i => i.Replicas.Count == 6 && i.Connections.All(c => c.DownloadComplete), "the server to have its four and two of the client's");
        foreach (var kind in SampleKinds)
        {
            var copy = Assert.Single(atClient.Replicas, r => r.NetworkId == serverIds[kind]);
            Assert.Equal((kind, serverGuid, $"{kind} SerializeConstruction"), (copy.Kind, copy.CreatingGuid, copy.ConstructionText));
        }
        foreach (var kind in new[] { ClientCreatibleClientSerialized, ClientCreatibleServerSerialized })
        {
            var copy = Assert.Single(atServer.Replicas, r => r.NetworkId == clientIds[kind]);
            Assert.Equal((kind, clientGuid, $"{kind} SerializeConstruction"), (copy.Kind, copy.CreatingGuid, copy.ConstructionText));
        }
        Assert.DoesNotContain(atServer.Replicas, r => r.NetworkId == clientIds[ServerCreatedClientSerialized] || r.NetworkId == clientIds[ServerCreatedServerSerialized]);
        var serverAtClient = Assert.Single(atClient.Connections);
        Assert.Equal((serverGuid, true, true), (serverAtClient.PeerGuid, serverAtClient.Validated, serverAtClient.DownloadComplete));
        Assert.Equal(6, serverAtClient.Constructed.Count);
        Assert.True(atServer.AllDownloadsCompleted);

        // 'R': each side changes the replicas it serializes; the server all four of its own, the client the one it
        // creates and serializes. The unreliable pair goes UNRELIABLE_WITH_ACK_RECEIPT and the reliable pair identically.
        var values = new Dictionary<ulong, (int, int, int, int)>();
        var n = 10;
        foreach (var kind in SampleKinds)
        {
            values[serverIds[kind]] = (n + 1, n + 2, n + 3, n + 4);
            await serverRm3.SetReplicaAsync(serverIds[kind], new ReplicaValues(n + 1, n + 2, n + 3, n + 4), Token);
            n += 10;
        }
        values[clientIds[ClientCreatibleClientSerialized]] = (51, 52, 53, 54);
        await clientRm3.SetReplicaAsync(clientIds[ClientCreatibleClientSerialized], new ReplicaValues(51, 52, 53, 54), Token);

        atClient = await WaitForAsync(clientRm3, i => SampleKinds.All(k => Vars(i, serverIds[k]) == values[serverIds[k]]), "the server's variables at the client");
        atServer = await WaitForAsync(serverRm3, i => Vars(i, clientIds[ClientCreatibleClientSerialized]) == (51, 52, 53, 54), "the client's variables at the server");
        // The server serializes the client's ClientCreatible_ServerSerialized, which it has not changed.
        Assert.Equal<(int, int, int, int)?>((0, 0, 0, 0), Vars(atServer, clientIds[ClientCreatibleServerSerialized]));

        // A second change sends only the changed variables, and they arrive.
        values[serverIds[ServerCreatedServerSerialized]] = (41, 99, 43, 98);
        await serverRm3.SetReplicaAsync(serverIds[ServerCreatedServerSerialized], new ReplicaValues(Var2: 99, Var4: 98), Token);
        await WaitForAsync(clientRm3, i => Vars(i, serverIds[ServerCreatedServerSerialized]) == (41, 99, 43, 98), "the second change at the client");
        // Serializations carry the sample's timestamp.
        await session.ClientInbox.WaitForPacketAsync(p => p.Data.Span[0] == (byte)MessageId.Timestamp && p.MessageId == (byte)MessageId.ReplicaManagerSerialize, "a timestamped ID_REPLICA_MANAGER_SERIALIZE", Timeout, Token);

        // 'D' on the client: the server destroys the two it had, with the text SerializeDestruction wrote.
        Assert.Equal(4, await clientRm3.DestroyMyReplicasAsync(Token));
        atServer = await WaitForAsync(serverRm3, i => i.Replicas.Count == 4 && i.Destructions.Count == 2, "the client's replicas destroyed at the server");
        Assert.Equal(
            [new ReplicaDestruction(clientIds[ClientCreatibleClientSerialized], ClientCreatibleClientSerialized, $"{ClientCreatibleClientSerialized} SerializeDestruction", clientGuid),
             new ReplicaDestruction(clientIds[ClientCreatibleServerSerialized], ClientCreatibleServerSerialized, $"{ClientCreatibleServerSerialized} SerializeDestruction", clientGuid)],
            atServer.Destructions.OrderBy(d => d.Kind, StringComparer.Ordinal));
        Assert.Equal(4, (await clientRm3.GetReplicaManager3InfoAsync(Token)).Replicas.Count);

        // The client leaves; QueryActionOnPopConnection deletes the server's replicas there, and the server keeps its own.
        await session.Client.CloseAsync(serverGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        var left = await WaitForAsync(clientRm3, i => i.Replicas.Count == 0 && i.Connections.Count == 0, "the client to delete the server's replicas");
        Assert.Empty(left.Destructions);
        atServer = await WaitForAsync(serverRm3, i => i.Connections.Count == 0, "the server to pop the client");
        Assert.Equal(SampleKinds.Select(k => serverIds[k]), atServer.Replicas.Select(r => r.NetworkId));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Peers_replicate_each_other_s_objects_and_delete_them_when_the_owner_leaves(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var peerA = (IReplicaManager3Driver)session.Client;
        var peerB = (IReplicaManager3Driver)session.Server;
        await peerA.AttachReplicaManager3Async("P2P", cancellationToken: Token);
        await peerB.AttachReplicaManager3Async("P2P", cancellationToken: Token);
        var (bGuid, aGuid) = await session.ConnectAsync(Token);

        var aIds = await peerA.CreateReplicasAsync(["P2PReplica", "P2PReplica"], cancellationToken: Token);
        var (a1, a2) = (aIds[0], aIds[1]);
        var b1 = Assert.Single(await peerB.CreateReplicasAsync(["P2PReplica"], cancellationToken: Token));

        // QueryConstruction_PeerToPeer: only the creator sends, and each peer has all three.
        var atA = await WaitForAsync(peerA, i => i.Replicas.Count == 3, "peer A to have B's replica");
        var atB = await WaitForAsync(peerB, i => i.Replicas.Count == 3, "peer B to have A's replicas");
        Assert.Equal(bGuid, Assert.Single(atA.Replicas, r => r.NetworkId == b1).CreatingGuid);
        Assert.Equal([aGuid, aGuid], atB.Replicas.Where(r => r.NetworkId == a1 || r.NetworkId == a2).Select(r => r.CreatingGuid));

        await peerA.SetReplicaAsync(a1, new ReplicaValues(1, 2, 3, 4), Token);
        await peerB.SetReplicaAsync(b1, new ReplicaValues(5, 6, 7, 8), Token);
        await WaitForAsync(peerB, i => Vars(i, a1) == (1, 2, 3, 4), "A's variables at B");
        await WaitForAsync(peerA, i => Vars(i, b1) == (5, 6, 7, 8), "B's variables at A");

        // B's 'D' destroys its replica at A.
        Assert.Equal(1, await peerB.DestroyMyReplicasAsync(Token));
        atA = await WaitForAsync(peerA, i => i.Replicas.Count == 2, "B's replica destroyed at A");
        Assert.Equal(new ReplicaDestruction(b1, "P2PReplica", "P2PReplica SerializeDestruction", bGuid), Assert.Single(atA.Destructions));

        // A leaves; QueryActionOnPopConnection_PeerToPeer deletes its replicas at B.
        await session.Client.CloseAsync(bGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        await WaitForAsync(peerB, i => i.Replicas.Count == 0, "A's replicas deleted at B");
        Assert.Equal([a1, a2], (await WaitForAsync(peerA, i => i.Connections.Count == 0, "A to pop B")).Replicas.Select(r => r.NetworkId));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Logged_in_users_are_replicated_to_every_client_and_removed_when_their_client_leaves(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // The second client is of the server's kind, so every pair of kinds talks.
        var second = await session.AddClientAsync(server, Token);
        var serverRm3 = (IReplicaManager3Driver)session.Server;
        var firstRm3 = (IReplicaManager3Driver)session.Client;
        var secondRm3 = (IReplicaManager3Driver)second.Driver;
        // ReplicatedLogin's connections do not group download messages.
        await serverRm3.AttachReplicaManager3Async("SERVER", groupDownloads: false, cancellationToken: Token);
        await firstRm3.AttachReplicaManager3Async("CLIENT", groupDownloads: false, cancellationToken: Token);
        await secondRm3.AttachReplicaManager3Async("CLIENT", groupDownloads: false, cancellationToken: Token);

        var (serverGuid, firstGuid) = await session.ConnectAsync(Token);
        var firstUser = await LogInAsync(session, session.ClientPeer, serverGuid, firstGuid);

        // The construction, as SendConstruction writes it: offsets to the end of the entry and of the allocation ID,
        // the NetworkID, the create bit, the creator, "User", then User::SerializeToBitStream; no post-construction data,
        // no destructions.
        var construction = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReplicaManagerConstruction, "the User's construction", Timeout, Token);
        var username = $"User {firstGuid}";
        var expected = new List<byte> { (byte)MessageId.ReplicaManagerConstruction, 0, 0, 1 };
        expected.AddRange(BigEndian((uint)(312 + 16 + (8 * username.Length) + 64)));
        expected.AddRange(BigEndian(firstUser));
        expected.Add(0x80);
        expected.AddRange(BigEndian(serverGuid));
        expected.AddRange(BigEndian(280u));
        expected.AddRange(RakString("User"));
        expected.AddRange([0, 0, 0, 0]);
        expected.AddRange(RakString(username));
        expected.AddRange(BigEndian(firstGuid));
        expected.AddRange([0, 0, 0]);
        Assert.Equal(expected, construction.Data.ToArray());
        var atFirst = await WaitForAsync(firstRm3, i => i.Replicas.Count == 1, "the first user at the first client");
        Assert.Equal(new ReplicaState(firstUser, "User", serverGuid, Score: 0, Username: username, UserGuid: firstGuid), atFirst.Replicas[0]);

        // The server changes the score; RM3SR_BROADCAST_IDENTICALLY sends it.
        await serverRm3.SetReplicaAsync(firstUser, new ReplicaValues(Score: 10), Token);
        await WaitForAsync(firstRm3, i => i.Replicas.Single().Score == 10, "the new score at the first client");

        // A second client logs in, downloads both users, and the first client gets the second's.
        var (_, secondGuid) = await session.ConnectAsync(second, Token);
        var secondUser = await LogInAsync(session, second, serverGuid, secondGuid);
        var atSecond = await WaitForAsync(secondRm3, i => i.Replicas.Count == 2, "both users at the second client");
        Assert.Equal(
            [new ReplicaState(firstUser, "User", serverGuid, Score: 10, Username: username, UserGuid: firstGuid),
             new ReplicaState(secondUser, "User", serverGuid, Score: 0, Username: $"User {secondGuid}", UserGuid: secondGuid)],
            atSecond.Replicas.OrderBy(r => r.NetworkId == secondUser));
        await WaitForAsync(firstRm3, i => i.Replicas.Count == 2, "the second user at the first client");

        // The first client leaves; the server deletes its user (DeleteUserWithGuid), and the second client destroys it.
        await session.Client.CloseAsync(serverGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification && p.PeerGuid == firstGuid, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        await serverRm3.DestroyReplicaAsync(firstUser, Token);
        atSecond = await WaitForAsync(secondRm3, i => i.Replicas.Count == 1, "the first user destroyed at the second client");
        Assert.Equal(secondUser, atSecond.Replicas[0].NetworkId);
        Assert.Equal(new ReplicaDestruction(firstUser, "User", "", serverGuid), Assert.Single(atSecond.Destructions));
        Assert.Equal([secondUser], (await serverRm3.GetReplicaManager3InfoAsync(Token)).Replicas.Select(r => r.NetworkId));
    }

    // ReplicatedLogin: the client sends ID_USER_PACKET_ENUM and its name; the server creates a User for the sender.
    private static async Task<ulong> LogInAsync(InteropSession session, InteropPeer client, ulong serverGuid, ulong clientGuid)
    {
        var username = $"User {clientGuid}";
        await client.Driver.SendAsync(new SendOptions(serverGuid, (byte[])[(byte)MessageId.UserPacketEnum, .. RakString(username)]), Token);
        var login = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum && p.PeerGuid == clientGuid, "the login", Timeout, Token);
        var reader = new BitStreamReader(login.Data.Span[1..]);
        Assert.True(reader.TryReadRakString(out var received));
        return Assert.Single(await ((IReplicaManager3Driver)session.Server).CreateReplicasAsync(["User"], received, login.PeerGuid, Token));
    }

    private static async Task<ReplicaManager3Info> WaitForAsync(IReplicaManager3Driver driver, Func<ReplicaManager3Info, bool> condition, string description)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Timeout);
        ReplicaManager3Info? info = null;
        try
        {
            while (true)
            {
                info = await driver.GetReplicaManager3InfoAsync(deadline.Token);
                if (condition(info))
                    return info;
                await Task.Delay(20, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for {description}; last saw {Describe(info)}.");
            throw;
        }
    }

    private static string Describe(ReplicaManager3Info? info) => info is null
        ? "nothing"
        : $"replicas [{string.Join(", ", info.Replicas)}], connections [{string.Join(", ", info.Connections.Select(c => $"{c.PeerGuid} validated={c.Validated} download={c.DownloadComplete} constructed={c.Constructed.Count}"))}], destructions [{string.Join(", ", info.Destructions)}]";

    private static (int, int, int, int)? Vars(ReplicaManager3Info info, ulong networkId) =>
        info.Replicas.FirstOrDefault(r => r.NetworkId == networkId) is { Var1: { } v1, Var2: { } v2, Var3: { } v3, Var4: { } v4 } ? (v1, v2, v3, v4) : null;

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    // RakString::Serialize: a u16 length then the bytes.
    private static byte[] RakString(string text) => [(byte)(text.Length >> 8), (byte)text.Length, .. Encoding.ASCII.GetBytes(text)];
}
