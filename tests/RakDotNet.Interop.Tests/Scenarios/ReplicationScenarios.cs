using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// The utilities object replication rests on, each system using what the other sent: RandSync from the test at the end of
/// RandSync.cpp; NetworkIDManager as NetworkIDObject.h describes it, an authority handing out IDs that the other system
/// sets; VariableDeltaSerializer as the ReplicaManager3 sample's Serialize and Deserialize use it, with an acknowledged,
/// a lost and an unchanged update; and GridSectorizer, which has no messages, by the same calls giving the same entries.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ReplicationScenarios
{
    private const byte RandSyncConstruction = (byte)MessageId.UserPacketEnum;
    private const byte RandSyncUpdate = (byte)MessageId.UserPacketEnum + 1;
    private const byte NetworkIds = (byte)MessageId.UserPacketEnum + 2;
    private const byte UnreliableVariables = (byte)MessageId.UserPacketEnum + 3;
    private const byte ReliableVariables = (byte)MessageId.UserPacketEnum + 4;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Two_systems_draw_the_same_random_numbers_after_construction_and_each_update(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var r1 = (IRandSyncDriver)session.Client;
        var r2 = (IRandSyncDriver)session.Server;
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        // Both generators give the same numbers, floats and buffer fill for a seed (Rand.cpp).
        var reference = await r1.DrawRakNetRandomAsync(12345, 20, floatCount: 3, fillBytes: 6, Token);
        Assert.Equal(reference, await r2.DrawRakNetRandomAsync(12345, 20, floatCount: 3, fillBytes: 6, Token), RakNetRandomOutputComparer.Instance);

        // r1 is seeded and draws; its construction reaches r2, which continues from the same point.
        await r1.SeedRandSyncAsync(12345, cancellationToken: Token);
        Assert.Equal(reference.Values.Take(3), await r1.NextRandSyncAsync(3, cancellationToken: Token));
        await SendAsync(session.Client, serverGuid, RandSyncConstruction, await r1.SerializeRandSyncConstructionAsync(Token));
        var construction = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == RandSyncConstruction, "the RandSync construction", Timeout, Token);
        // SerializeConstruction: the seed then the call count, big-endian.
        Assert.Equal([RandSyncConstruction, 0, 0, 0x30, 0x39, 0, 0, 0, 3], construction.Data.ToArray());
        Assert.True(await r2.DeserializeRandSyncConstructionAsync(construction.Data[1..], Token));
        Assert.Equal((12345u, 3u), await r2.GetRandSyncInfoAsync(Token));
        Assert.Equal(reference.Values.Skip(3).Take(4), await r1.NextRandSyncAsync(4, cancellationToken: Token));
        Assert.Equal(reference.Values.Skip(3).Take(4), await r2.NextRandSyncAsync(4, cancellationToken: Token));

        // r2 draws three more; its update makes r1 skip them.
        Assert.Equal(reference.Values.Skip(7).Take(3), await r2.NextRandSyncAsync(3, cancellationToken: Token));
        await SendAsync(session.Server, clientGuid, RandSyncUpdate, await r2.SerializeRandSyncAsync(Token));
        var update = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == RandSyncUpdate, "r2's RandSync update", Timeout, Token);
        Assert.Equal([RandSyncUpdate, 0, 0, 0, 10], update.Data.ToArray());
        await r1.DeserializeRandSyncAsync(update.Data[1..], Token);
        Assert.Equal((12345u, 10u), await r1.GetRandSyncInfoAsync(Token));
        Assert.Equal(reference.Values.Skip(10).Take(1), await r1.NextRandSyncAsync(1, cancellationToken: Token));
        Assert.Equal(reference.Values.Skip(10).Take(1), await r2.NextRandSyncAsync(1, cancellationToken: Token));

        // r1 draws two more than r2; r2's update makes r1 replay them as r2 draws them.
        Assert.Equal(reference.Values.Skip(11).Take(2), await r1.NextRandSyncAsync(2, cancellationToken: Token));
        await SendAsync(session.Server, clientGuid, RandSyncUpdate, await r2.SerializeRandSyncAsync(Token));
        var behind = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == RandSyncUpdate, "r2's second RandSync update", Timeout, Token);
        await r1.DeserializeRandSyncAsync(behind.Data[1..], Token);
        Assert.Equal(reference.Values.Skip(11).Take(2), await r1.NextRandSyncAsync(2, cancellationToken: Token));
        Assert.Equal(reference.Values.Skip(11).Take(2), await r2.NextRandSyncAsync(2, cancellationToken: Token));
        Assert.Equal((12345u, 13u), await r1.GetRandSyncInfoAsync(Token));

        // FrandomMT divides by 2^32 - 1 on both.
        Assert.Equal(await r1.NextRandSyncAsync(3, asFloat: true, Token), await r2.NextRandSyncAsync(3, asFloat: true, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Objects_the_authority_names_are_found_by_the_same_ids_on_the_other_system(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var authority = (INetworkIdDriver)session.Server;
        var remote = (INetworkIdDriver)session.Client;
        var (_, clientGuid) = await session.ConnectAsync(Token);

        // The authority's manager gives consecutive IDs from a random start (NetworkIDManager.cpp:48-58).
        var ids = new ulong[3];
        for (var handle = 1; handle <= 3; handle++)
        {
            await authority.NewNetworkIdObjectAsync(handle, Token);
            await authority.SetNetworkIdManagerAsync(handle, cancellationToken: Token);
            var (id, hasManager) = await authority.GetNetworkIdObjectInfoAsync(handle, Token);
            Assert.True(hasManager);
            ids[handle - 1] = id;
        }
        Assert.Equal([ids[0], ids[0] + 1, ids[0] + 2], ids);
        // An ID in use is skipped: object 4 takes the next ID by hand, so object 5 gets the one after.
        await authority.NewNetworkIdObjectAsync(4, Token);
        await authority.SetNetworkIdAsync(4, ids[2] + 1, Token);
        await authority.SetNetworkIdManagerAsync(4, cancellationToken: Token);
        await authority.NewNetworkIdObjectAsync(5, Token);
        await authority.SetNetworkIdManagerAsync(5, cancellationToken: Token);
        Assert.Equal(ids[2] + 2, (await authority.GetNetworkIdObjectInfoAsync(5, Token)).NetworkId);

        var message = new byte[1 + (8 * ids.Length)];
        message[0] = NetworkIds;
        for (var i = 0; i < ids.Length; i++)
            BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(1 + (8 * i)), ids[i]);
        await session.Server.SendAsync(new SendOptions(clientGuid, message), Token);
        var received = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == NetworkIds, "the network IDs", Timeout, Token);

        // The other system creates the same objects and sets the IDs from the message, before or after the manager.
        for (var i = 0; i < ids.Length; i++)
        {
            var id = BinaryPrimitives.ReadUInt64BigEndian(received.Data.Span[(1 + (8 * i))..]);
            await remote.NewNetworkIdObjectAsync(10 + i, Token);
            if (i == 0)
            {
                await remote.SetNetworkIdManagerAsync(10, cancellationToken: Token);
                await remote.SetNetworkIdAsync(10, id, Token);
            }
            else
            {
                await remote.SetNetworkIdAsync(10 + i, id, Token);
                await remote.SetNetworkIdManagerAsync(10 + i, cancellationToken: Token);
            }
        }
        for (var i = 0; i < ids.Length; i++)
        {
            Assert.Equal((10 + i, 10 + i), await remote.LookupNetworkIdAsync(ids[i], Token));
            Assert.Equal((1 + i, 1 + i), await authority.LookupNetworkIdAsync(ids[i], Token));
        }

        // GET_OBJECT_FROM_ID returns the parent; a destroyed or unassigned object is not found.
        await remote.SetNetworkIdParentAsync(12, 10, Token);
        Assert.Equal((12, 10), await remote.LookupNetworkIdAsync(ids[2], Token));
        await remote.DeleteNetworkIdObjectAsync(11, Token);
        Assert.Equal((null, null), await remote.LookupNetworkIdAsync(ids[1], Token));
        await remote.SetNetworkIdManagerAsync(10, attach: false, Token);
        Assert.Equal((RakNetConstants.UnassignedNetworkId, false), await remote.GetNetworkIdObjectInfoAsync(10, Token));
        Assert.Equal((null, null), await remote.LookupNetworkIdAsync(ids[0], Token));
        await authority.ClearNetworkIdsAsync(Token);
        Assert.Equal((null, null), await authority.LookupNetworkIdAsync(ids[0], Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Only_changed_variables_are_sent_and_a_lost_update_is_sent_again(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var owner = (IDeltaSerializerDriver)session.Client;
        var receiver = (IDeltaSerializerDriver)session.Server;
        var (serverGuid, _) = await session.ConnectAsync(Token);

        // Tick 1, the initial download: everything, the unreliable variables with a receipt, the reliable ones identically.
        var first = await SerializeAsync(owner, serverGuid, [5, 7], [1, 2], firstToThisSystem: true);
        Assert.True(first[0].Sent);
        Assert.True(first[1].Sent);
        Assert.Equal([5, 7], await ReceiveAsync(session, receiver, UnreliableVariables));
        Assert.Equal([1, 2], await ReceiveAsync(session, receiver, ReliableVariables));
        await WaitForReceiptAsync(session, first[0].Receipt);
        await owner.DeltaReceiptAsync(serverGuid, first[0].Receipt, arrived: true, Token);

        // Tick 2: only var2 changed, and nothing reliable.
        var second = await SerializeAsync(owner, serverGuid, [5, 8], [1, 2], firstToThisSystem: false);
        Assert.False(second[1].Sent);
        Assert.Equal([null, 8], await ReceiveAsync(session, receiver, UnreliableVariables));
        await WaitForReceiptAsync(session, second[0].Receipt);

        // ID_SND_RECEIPT_LOSS for tick 2's update, as the sample reports it: tick 3 sends var2 again though unchanged.
        await owner.DeltaReceiptAsync(serverGuid, second[0].Receipt, arrived: false, Token);
        var third = await SerializeAsync(owner, serverGuid, [5, 8], [1, 3], firstToThisSystem: false);
        Assert.Equal(second[0].Data, third[0].Data);
        Assert.Equal([null, 8], await ReceiveAsync(session, receiver, UnreliableVariables));
        Assert.Equal([null, 3], await ReceiveAsync(session, receiver, ReliableVariables));

        // A system whose history is removed gets everything again.
        await owner.RemoveDeltaSystemAsync(serverGuid, Token);
        var fourth = await SerializeAsync(owner, serverGuid, [5, 8], [1, 3], firstToThisSystem: false);
        Assert.False(fourth[1].Sent);
        Assert.Equal([5, 8], await ReceiveAsync(session, receiver, UnreliableVariables));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_grid_returns_the_same_entries_on_both_systems(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        foreach (var grid in new[] { (IGridSectorizerDriver)session.Client, (IGridSectorizerDriver)session.Server })
        {
            // Ten cells of 9.5 a side, so edges fall on single-precision rounding (GridSectorizer.cpp:28-37, 197-219).
            await grid.InitGridAsync(10, 10, new GridRectangle(0, 0, 95, 95), Token);
            await grid.AddGridEntryAsync(1, new GridRectangle(9.5f, 9.5f, 19, 19), Token);
            await grid.AddGridEntryAsync(2, new GridRectangle(-50, -50, 1, 1), Token);
            await grid.AddGridEntryAsync(3, new GridRectangle(28.5f, 0, 200, 5), Token);
            await grid.AddGridEntryAsync(4, new GridRectangle(47.5f, 47.5f, 57, 66.5f), Token);

            // Column by column: entry 4 spans cells 5 to 6 across and 5 to 7 down, as native RakNet places it.
            Assert.Equal([2, 1, 1, 1, 1, 3, 3, 3, 4, 4, 4, 3, 4, 4, 4, 3, 3, 3], await grid.GetGridEntriesAsync(new GridRectangle(0, 0, 95, 95), Token));
            Assert.Equal([1], await grid.GetGridEntriesAsync(new GridRectangle(9.5f, 9.5f, 9.6f, 9.6f), Token));
            Assert.Empty(await grid.GetGridEntriesAsync(new GridRectangle(18.9f, 0, 19, 1), Token));
            Assert.Equal([4, 4, 4, 4], await grid.GetGridEntriesAsync(new GridRectangle(56.9f, 66.4f, 57, 66.5f), Token));

            await grid.ClearGridAsync(Token);
            Assert.Empty(await grid.GetGridEntriesAsync(new GridRectangle(0, 0, 95, 95), Token));
        }
    }

    private static Task<uint> SendAsync(IPeerDriver driver, ulong target, byte messageId, byte[] payload) =>
        driver.SendAsync(new SendOptions(target, (byte[])[messageId, .. payload]), Token);

    // The sample's Serialize: an UNRELIABLE_WITH_ACK_RECEIPT group and a RELIABLE_ORDERED identical group, each sent as
    // its own message when it wrote anything.
    private static Task<IReadOnlyList<DeltaGroupResult>> SerializeAsync(IDeltaSerializerDriver owner, ulong target, int[] unreliable, int[] reliable, bool firstToThisSystem) =>
        owner.SerializeDeltaAsync(
            [
                new DeltaGroup("UNRELIABLE_ACKED", unreliable, Target: target, SendTo: target, MessageId: UnreliableVariables),
                new DeltaGroup("IDENTICAL", reliable, First: firstToThisSystem, SendTo: target, MessageId: ReliableVariables),
            ],
            preSerializeTick: true,
            Token);

    // The sample's Deserialize of one group: two variables, null for one that did not change.
    private static async Task<IReadOnlyList<int?>> ReceiveAsync(InteropSession session, IDeltaSerializerDriver receiver, byte messageId)
    {
        var packet = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == messageId, $"variables in message {messageId}", Timeout, Token);
        return await receiver.DeserializeDeltaAsync(packet.Data[1..], 2, Token);
    }

    // ID_SND_RECEIPT_ACKED carries the receipt as RakNet's memcpy writes it, little-endian here.
    private static Task<ReceivedPacket> WaitForReceiptAsync(InteropSession session, uint receipt) =>
        session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.SndReceiptAcked && BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[1..]) == receipt,
            $"ID_SND_RECEIPT_ACKED for {receipt}",
            Timeout,
            Token);

    private sealed class RakNetRandomOutputComparer : IEqualityComparer<RakNetRandomOutput>
    {
        public static readonly RakNetRandomOutputComparer Instance = new();

        public bool Equals(RakNetRandomOutput? x, RakNetRandomOutput? y) =>
            x is not null && y is not null && x.Values.SequenceEqual(y.Values) && x.FloatBits.SequenceEqual(y.FloatBits)
            && (x.Fill ?? []).AsSpan().SequenceEqual(y.Fill ?? []) && x.AfterFill == y.AfterFill;

        public int GetHashCode(RakNetRandomOutput obj) => obj.Values.Count;
    }
}
