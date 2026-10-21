using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// FullyConnectedMesh2 with ConnectionGraph2, from Samples/Fully Connected Mesh, FCMHost and FCMHostSimultaneous, and
/// the verified join of FCMVerifiedJoinSimultaneous. Every system agrees on one host, the one running longest; a change
/// of host reaches the application as ID_FCM2_NEW_HOST with the old host's GUID, injected from the new host
/// (FullyConnectedMesh2.cpp:437-451); the FCM2 GUID negotiation never does.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class FullyConnectedMesh2Scenarios
{
    private static readonly byte[] InternalMessages =
    [
        (byte)MessageId.Fcm2RequestFcmguid,
        (byte)MessageId.Fcm2RespondConnectionCount,
        (byte)MessageId.Fcm2InformFcmguid,
        (byte)MessageId.Fcm2UpdateMinTotalConnectionCount,
    ];

    // FCM2VerifiedJoinSimultaneousTest.cpp writes user data with WriteVJSUserData and WriteVJCUserData.
    private static readonly byte[] JoinStartUserData = [0x56, 0x4A, 0x53];
    private static readonly byte[] JoinCapableUserData = [0x56, 0x4A, 0x43, 0x21];

    // The host is decided by elapsed runtime since the plugin was attached; this separates the first system's.
    private static readonly TimeSpan Seniority = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => MeshSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_chain_of_connections_becomes_a_full_mesh_whose_host_is_the_oldest_system(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormChainAsync(mesh);
        var (oldest, second, third) = (mesh[0], mesh[1], mesh[2]);

        // The third system learnt of the oldest from ID_REMOTE_NEW_INCOMING_CONNECTION and connected to it.
        await third.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.RemoteNewIncomingConnection && p.PeerGuid == second.Started.PeerGuid,
            "ID_REMOTE_NEW_INCOMING_CONNECTION from peer 1", Timeout, Token);
        // The oldest learnt of the third from the second at the same time, so they may have connected to each other at
        // once and either side's attempt won.
        await oldest.Inbox.WaitForPacketAsync(
            p => p.MessageId is (byte)MessageId.NewIncomingConnection or (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == third.Started.PeerGuid,
            "the connection with peer 2", Timeout, Token);

        var order = mesh.Peers.Select(p => p.Started.PeerGuid).ToArray();
        foreach (var peer in mesh.Peers)
        {
            var info = await Mesh(peer).GetMeshInfoAsync(Token);
            Assert.Equal(oldest.Started.PeerGuid, info.ConnectedHost);
            Assert.Equal(oldest.Started.PeerGuid, info.HostSystem);
            Assert.Equal(peer == oldest, info.IsHostSystem);
            Assert.Equal(order, info.HostOrder);
            Assert.Equal(2, info.Participants.Count);
            var changes = await WaitForHostChangesAsync(peer, oldest.Started.PeerGuid);
            // The oldest knew itself host from its first participant; others may see the system they met first as host.
            if (peer == oldest)
                Assert.Single(changes);
            Assert.DoesNotContain(peer.Inbox.Unconsumed, p => InternalMessages.Contains(p.MessageId));
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_star_of_connections_becomes_a_full_mesh(PeerKind first, PeerKind others)
    {
        // Samples/Fully Connected Mesh, "connecting each peer to a central peer". The last system is listed both others
        // in one ID_REMOTE_NEW_INCOMING_CONNECTION, of which FullyConnectedMesh2 reads only the first correctly
        // (FullyConnectedMesh2.cpp:766-772); the others were told about it too and connect to it.
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 4), Token);
        await AttachMeshAndGraphAsync(mesh[0]);
        await Task.Delay(Seniority, Token);
        foreach (var peer in mesh.Peers.Skip(1))
            await AttachMeshAndGraphAsync(peer);

        foreach (var peer in mesh.Peers.Skip(1))
            await MeshSession.ConnectAsync(peer, mesh[0], Token);

        var order = await WaitForAgreementAsync(mesh);
        Assert.Equal(mesh[0].Started.PeerGuid, order[0]);
        foreach (var peer in mesh.Peers)
        {
            Assert.Equal(3, (await peer.Driver.GetInfoAsync(Token)).Connections.Count);
            await WaitForHostChangesAsync(peer, mesh[0].Started.PeerGuid);
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task When_the_host_shuts_down_the_next_oldest_system_becomes_host(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormChainAsync(mesh);
        var (oldest, second, third) = (mesh[0], mesh[1], mesh[2]);
        foreach (var peer in mesh.Peers)
            await WaitForHostChangesAsync(peer, oldest.Started.PeerGuid);

        // FCM2HostSimultaneousTest.cpp 'd': Shutdown(100) on the host.
        await oldest.Driver.ShutdownAsync(100, Token);

        // FullyConnectedMesh2.cpp:363-375: the next lowest FCM2 GUID, with the lost host as the old one.
        foreach (var peer in new[] { second, third })
        {
            var newHost = await peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2NewHost, "ID_FCM2_NEW_HOST after the host left", Timeout, Token);
            Assert.Equal(second.Started.PeerGuid, newHost.PeerGuid);
            AssertNewHost(newHost, oldest.Started.PeerGuid);
            var info = await Mesh(peer).GetMeshInfoAsync(Token);
            Assert.Equal(second.Started.PeerGuid, info.ConnectedHost);
            Assert.Equal([second.Started.PeerGuid, third.Started.PeerGuid], info.HostOrder);
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Systems_that_all_connect_to_each_other_at_once_agree_on_one_host(PeerKind first, PeerKind others)
    {
        // FCM2HostSimultaneousTest.cpp: every peer connects to every other at the same time.
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        foreach (var peer in mesh.Peers)
        {
            await Mesh(peer).AttachFullyConnectedMesh2Async(new MeshOptions(), Token);
            await Mesh(peer).AttachConnectionGraph2Async(cancellationToken: Token);
        }
        foreach (var from in mesh.Peers)
        {
            foreach (var to in mesh.Peers.Where(p => p != from))
                await from.Driver.ConnectAsync(new Harness.ConnectOptions(to.Started.BoundAddress), Token);
        }

        var order = await WaitForAgreementAsync(mesh);

        Assert.Equal(mesh.Peers.Select(p => p.Started.PeerGuid).Order(), order.Order());
        foreach (var peer in mesh.Peers)
        {
            var info = await Mesh(peer).GetMeshInfoAsync(Token);
            Assert.Equal(order[0], info.HostSystem);
            await WaitForHostChangesAsync(peer, order[0]);
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_verified_join_admits_a_client_that_connects_to_every_participant(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        var (host, member, client) = (mesh[0], mesh[1], mesh[2]);
        await FormVerifiedPairAsync(host, member);
        await Mesh(client).AttachFullyConnectedMesh2Async(VerifiedJoinOptions, Token);
        await MeshSession.ConnectAsync(client, host, Token);

        await Mesh(host).StartVerifiedJoinAsync(client.Started.PeerGuid, Token);

        // ID_FCM2_VERIFIED_JOIN_START: the count, then GUID, address, user data bit count and bytes (FullyConnectedMesh2.cpp:784-800).
        var start = await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinStart, "ID_FCM2_VERIFIED_JOIN_START", Timeout, Token);
        Assert.Equal(StartBytes(member), start.Data.ToArray());
        var required = Assert.Single(await Mesh(client).GetVerifiedJoinRequiredAsync(host.Started.PeerGuid, Token));
        Assert.Equal((member.Started.PeerGuid, member.Started.BoundAddress), (required.PeerGuid, required.Address));
        Assert.Equal(JoinStartUserData, required.UserData);
        Assert.Equal(JoinStartUserData.Length * 8, required.UserDataBits);
        await client.Driver.ConnectAsync(new Harness.ConnectOptions(required.Address), Token);

        // ID_FCM2_VERIFIED_JOIN_CAPABLE: GUID, address and JIPS_CONNECTED for the member, then WriteVJCUserData.
        var capable = await host.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinCapable, "ID_FCM2_VERIFIED_JOIN_CAPABLE", Timeout, Token);
        Assert.Equal(client.Started.PeerGuid, capable.PeerGuid);
        Assert.Equal(CapableBytes(member, state: 2), capable.Data.ToArray());
        await Mesh(host).RespondOnVerifiedJoinCapableAsync(capable, accept: true, new byte[] { 0xAD }, Token);

        // ID_FCM2_VERIFIED_JOIN_ACCEPTED goes to the member and the client, and is injected at the host.
        var expected = AcceptedBytes(client, member, 0xAD);
        foreach (var told in new[] { host, member, client })
        {
            var accepted = await told.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinAccepted, "ID_FCM2_VERIFIED_JOIN_ACCEPTED", Timeout, Token);
            Assert.Equal(expected, accepted.Data.ToArray());
            var read = await Mesh(told).GetVerifiedJoinAcceptedAsync(accepted, Token);
            Assert.Equal(told == client, read.ThisSystemAccepted);
            Assert.Equal(told == client ? [member.Started.PeerGuid, host.Started.PeerGuid] : [client.Started.PeerGuid], read.SystemsAccepted);
            Assert.Equal([0xAD], read.AdditionalData);
        }
        var order = await WaitForAgreementAsync(mesh);
        Assert.Equal(host.Started.PeerGuid, order[0]);
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_rejected_client_closes_what_it_connected_to_for_the_join(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        var (host, member, client) = (mesh[0], mesh[1], mesh[2]);
        await FormVerifiedPairAsync(host, member, JoinWithoutUserData);
        await Mesh(client).AttachFullyConnectedMesh2Async(JoinWithoutUserData, Token);
        await MeshSession.ConnectAsync(client, host, Token);
        await Mesh(host).StartVerifiedJoinAsync(client.Started.PeerGuid, Token);
        await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinStart, "ID_FCM2_VERIFIED_JOIN_START", Timeout, Token);
        await client.Driver.ConnectAsync(new Harness.ConnectOptions(member.Started.BoundAddress), Token);
        var capable = await host.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinCapable, "ID_FCM2_VERIFIED_JOIN_CAPABLE", Timeout, Token);

        await Mesh(host).RespondOnVerifiedJoinCapableAsync(capable, accept: false, new byte[] { 0x5E, 0x1F }, Token);

        var rejected = await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinRejected, "ID_FCM2_VERIFIED_JOIN_REJECTED", Timeout, Token);
        Assert.Equal([(byte)MessageId.Fcm2VerifiedJoinRejected, 0x5E, 0x1F], rejected.Data.ToArray());
        // FullyConnectedMesh2.cpp:1176-1188: the member is closed, the host is not.
        await member.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.DisconnectionNotification && p.PeerGuid == client.Started.PeerGuid,
            "ID_DISCONNECTION_NOTIFICATION from the rejected client", Timeout, Token);
        Assert.Equal("IS_CONNECTED", await client.Driver.GetConnectionStateAsync(host.Started.PeerGuid, Token));
        Assert.Equal([member.Started.PeerGuid], (await Mesh(host).GetMeshInfoAsync(Token)).Participants);
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_client_that_cannot_reach_a_participant_is_told_the_join_failed(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        var (host, member, client) = (mesh[0], mesh[1], mesh[2]);
        await FormVerifiedPairAsync(host, member, JoinWithoutUserData);
        await member.Driver.SetMaximumIncomingConnectionsAsync(0, Token);
        await Mesh(client).AttachFullyConnectedMesh2Async(JoinWithoutUserData, Token);
        await MeshSession.ConnectAsync(client, host, Token);
        await Mesh(host).StartVerifiedJoinAsync(client.Started.PeerGuid, Token);
        await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinStart, "ID_FCM2_VERIFIED_JOIN_START", Timeout, Token);

        await client.Driver.ConnectAsync(new Harness.ConnectOptions(member.Started.BoundAddress), Token);

        // The refused attempt marks the member failed; the host consumes the answer and replies
        // ID_FCM2_VERIFIED_JOIN_FAILED (FullyConnectedMesh2.cpp:1125-1132).
        await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NoFreeIncomingConnections, "ID_NO_FREE_INCOMING_CONNECTIONS", Timeout, Token);
        var failed = await client.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinFailed, "ID_FCM2_VERIFIED_JOIN_FAILED", Timeout, Token);
        Assert.Equal([(byte)MessageId.Fcm2VerifiedJoinFailed], failed.Data.ToArray());
        Assert.Equal(host.Started.PeerGuid, failed.PeerGuid);
        Assert.DoesNotContain(host.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinCapable);
        Assert.Empty(await Mesh(client).GetVerifiedJoinRequiredAsync(host.Started.PeerGuid, Token));
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Verified_joins_started_for_several_clients_at_once_end_in_one_mesh(PeerKind first, PeerKind others)
    {
        // FCM2VerifiedJoinSimultaneousTest.cpp: three clients connect to one system, which starts a verified join for each
        // at once; each client connects to what it is told and the host accepts every capable client.
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 4), Token);
        var host = mesh[0];
        await Mesh(host).AttachFullyConnectedMesh2Async(VerifiedJoinOptions, Token);
        await Task.Delay(Seniority, Token);
        foreach (var peer in mesh.Peers.Skip(1))
            await Mesh(peer).AttachFullyConnectedMesh2Async(VerifiedJoinOptions, Token);
        foreach (var client in mesh.Peers.Skip(1))
            await MeshSession.ConnectAsync(client, host, Token);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var userData = new List<byte[]>();
        var responders = mesh.Peers.Select(p => RespondToVerifiedJoinsAsync(p, userData, stop.Token)).ToList();
        foreach (var client in mesh.Peers.Skip(1))
            await Mesh(host).StartVerifiedJoinAsync(client.Started.PeerGuid, Token);

        try
        {
            await MeshSession.WaitUntilAsync(async () =>
            {
                foreach (var peer in mesh.Peers)
                {
                    if ((await Mesh(peer).GetMeshInfoAsync(Token)).Participants.Count != 3)
                        return false;
                }
                return true;
            }, "every system has the other three as participants", Token);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(responders);
        }

        var order = await WaitForAgreementAsync(mesh);
        Assert.Equal(host.Started.PeerGuid, order[0]);
        lock (userData)
        {
            Assert.NotEmpty(userData);
            Assert.All(userData, d => Assert.Equal(JoinStartUserData, d));
        }
        foreach (var client in mesh.Peers.Skip(1))
        {
            var accepted = await client.Inbox.WaitForPacketAsync(
                p => p.MessageId == (byte)MessageId.Fcm2VerifiedJoinAccepted && p.PeerGuid == host.Started.PeerGuid && ReadGuid(p.Data, 1) == client.Started.PeerGuid,
                "ID_FCM2_VERIFIED_JOIN_ACCEPTED for this client", Timeout, Token);
            Assert.True((await Mesh(client).GetVerifiedJoinAcceptedAsync(accepted, Token)).ThisSystemAccepted);
        }
    }

    private static MeshOptions VerifiedJoinOptions { get; } = new(
        AutoParticipate: false, ConnectOnNewRemote: false, JoinStartUserData: JoinStartUserData, JoinCapableUserData: JoinCapableUserData);

    // A rejected or failed join frees each system's ID_FCM2_VERIFIED_JOIN_START user data twice in RakNet
    // (FullyConnectedMesh2.cpp:1186-1196), which kills a native client, so those scenarios send none.
    private static MeshOptions JoinWithoutUserData { get; } = new(AutoParticipate: false, ConnectOnNewRemote: false);

    private static ITopologyDriver Mesh(InteropPeer peer) => (ITopologyDriver)peer.Driver;

    // Samples/Fully Connected Mesh: FullyConnectedMesh2 then ConnectionGraph2 on each; the first system is attached
    // earlier, so it has run longest; peer 1 connects to peer 0 and peer 2 to peer 1, and the mesh completes itself.
    private static async Task FormChainAsync(MeshSession mesh)
    {
        await AttachMeshAndGraphAsync(mesh[0]);
        await Task.Delay(Seniority, Token);
        await AttachMeshAndGraphAsync(mesh[1]);
        await AttachMeshAndGraphAsync(mesh[2]);
        await MeshSession.ConnectAsync(mesh[1], mesh[0], Token);
        await MeshSession.ConnectAsync(mesh[2], mesh[1], Token);
        await WaitForAgreementAsync(mesh);
    }

    private static async Task AttachMeshAndGraphAsync(InteropPeer peer)
    {
        await Mesh(peer).AttachFullyConnectedMesh2Async(new MeshOptions(), Token);
        await Mesh(peer).AttachConnectionGraph2Async(cancellationToken: Token);
    }

    // A host and a member that are participants of each other, the host having run longer.
    private static async Task FormVerifiedPairAsync(InteropPeer host, InteropPeer member, MeshOptions? options = null)
    {
        await Mesh(host).AttachFullyConnectedMesh2Async(options ?? VerifiedJoinOptions, Token);
        await Task.Delay(Seniority, Token);
        await Mesh(member).AttachFullyConnectedMesh2Async(options ?? VerifiedJoinOptions, Token);
        await MeshSession.ConnectAsync(member, host, Token);
        await Mesh(host).AddMeshParticipantAsync(member.Started.PeerGuid, Token);
        await Mesh(member).AddMeshParticipantAsync(host.Started.PeerGuid, Token);
        await MeshSession.WaitUntilAsync(async () => (await Mesh(member).GetMeshInfoAsync(Token)).ConnectedHost == host.Started.PeerGuid,
            "the member sees the host", Token);
    }

    // Until every system has every other as a participant and all report the same host order; returns that order.
    private static async Task<IReadOnlyList<ulong>> WaitForAgreementAsync(MeshSession mesh)
    {
        IReadOnlyList<ulong> order = [];
        await MeshSession.WaitUntilAsync(async () =>
        {
            var infos = new List<MeshInfo>();
            foreach (var peer in mesh.Peers)
                infos.Add(await Mesh(peer).GetMeshInfoAsync(Token));
            order = infos[0].HostOrder;
            var expected = order;
            return infos.TrueForAll(i => i.Participants.Count == mesh.Peers.Count - 1 && i.HostOrder.SequenceEqual(expected)
                                          && i.ConnectedHost == expected[0]);
        }, "every system agrees on the host order", Token);
        return order;
    }

    // The ID_FCM2_NEW_HOST packets up to the one naming host: the first has no old host, and each later one names the
    // host before it as the old one.
    private static async Task<IReadOnlyList<ReceivedPacket>> WaitForHostChangesAsync(InteropPeer peer, ulong host)
    {
        var changes = await peer.Inbox.WaitForPacketsAsync(
            p => p.MessageId == (byte)MessageId.Fcm2NewHost, found => found[^1].PeerGuid == host,
            $"ID_FCM2_NEW_HOST naming {host}", Timeout, Token);
        var oldHost = RakNetGuid.Unassigned.Value;
        foreach (var change in changes)
        {
            AssertNewHost(change, oldHost);
            oldHost = change.PeerGuid;
        }
        return changes;
    }

    // The sample's Receive loop: connect to what ID_FCM2_VERIFIED_JOIN_START asks for, accept every capable client.
    private static async Task RespondToVerifiedJoinsAsync(InteropPeer peer, List<byte[]> userData, CancellationToken stop)
    {
        try
        {
            while (true)
            {
                var packet = await peer.Inbox.WaitForPacketAsync(
                    p => p.MessageId is (byte)MessageId.Fcm2VerifiedJoinStart or (byte)MessageId.Fcm2VerifiedJoinCapable,
                    "a verified join message", TimeSpan.FromMinutes(5), stop);
                if (packet.MessageId == (byte)MessageId.Fcm2VerifiedJoinStart)
                {
                    foreach (var system in await Mesh(peer).GetVerifiedJoinRequiredAsync(packet.PeerGuid, stop))
                    {
                        lock (userData)
                            userData.Add(system.UserData);
                        await peer.Driver.ConnectAsync(new Harness.ConnectOptions(system.Address), stop);
                    }
                }
                else
                {
                    var reader = new BitStreamReader(packet.Data.Span);
                    Assert.True(RakDotNet.Plugins.FullyConnectedMesh2.SkipToVerifiedJoinCapableUserData(ref reader));
                    Assert.Equal(JoinCapableUserData, reader.RemainingAlignedBytes.ToArray());
                    await Mesh(peer).RespondOnVerifiedJoinCapableAsync(packet, accept: true, cancellationToken: stop);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private static ulong ReadGuid(ReadOnlyMemory<byte> data, int offset) =>
        data.Length >= offset + 8 ? BinaryPrimitives.ReadUInt64BigEndian(data.Span[offset..]) : 0;

    // ID_FCM2_NEW_HOST and the old host in a packet of sizeof(MessageID)+sizeof(RakNetGUID) bytes, whose last eight
    // RakNet never writes.
    private static void AssertNewHost(ReceivedPacket packet, ulong oldHost)
    {
        Assert.Equal(1 + 16, packet.Data.Length);
        Assert.Equal((byte)MessageId.Fcm2NewHost, packet.Data.Span[0]);
        Assert.Equal(oldHost, ReadGuid(packet.Data, 1));
    }

    private static byte[] StartBytes(InteropPeer member)
    {
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinStart);
        writer.WriteUInt16(1);
        writer.WriteRakNetGuid(new RakNetGuid(member.Started.PeerGuid));
        writer.WriteAddress(member.Started.BoundAddress);
        writer.WriteUInt32((uint)JoinStartUserData.Length * 8);
        writer.WriteBytes(JoinStartUserData);
        return writer.Written.ToArray();
    }

    // FullyConnectedMesh2.cpp:1333-1344, then WriteVJCUserData.
    private static byte[] CapableBytes(InteropPeer member, byte state)
    {
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinCapable);
        writer.WriteUInt16(1);
        writer.WriteRakNetGuid(new RakNetGuid(member.Started.PeerGuid));
        writer.WriteAddress(member.Started.BoundAddress);
        writer.WriteByte(state);
        writer.WriteBytes(JoinCapableUserData);
        return writer.Written.ToArray();
    }

    // FullyConnectedMesh2.cpp:858-872: the client, no systems to close, the member it reached, the additional data.
    private static byte[] AcceptedBytes(InteropPeer client, InteropPeer member, byte additional)
    {
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2VerifiedJoinAccepted);
        writer.WriteRakNetGuid(new RakNetGuid(client.Started.PeerGuid));
        writer.WriteUInt16(0);
        writer.WriteUInt16(1);
        writer.WriteRakNetGuid(new RakNetGuid(member.Started.PeerGuid));
        writer.WriteByte(additional);
        return writer.Written.ToArray();
    }
}
