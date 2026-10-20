using System.Buffers.Binary;
using System.Net;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// ConnectionGraph2 on every system of a star, as the FullyConnectedMesh2 samples attach it: the centre tells each new
/// system about the others and the others about it with ID_REMOTE_NEW_INCOMING_CONNECTION (a 32-bit count, then
/// SystemAddress, RakNetGUID and a 16-bit ping per system), and tells the rest when one disconnects or is lost
/// (ConnectionGraph2.cpp:160-237). The messages reach the application.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ConnectionGraph2Scenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => MeshSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_new_connection_is_announced_to_the_others_and_they_are_listed_to_it(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 4), Token);
        var (centre, a, b, c) = (mesh[0], mesh[1], mesh[2], mesh[3]);
        await AttachAsync(mesh);

        await MeshSession.ConnectAsync(a, centre, Token);
        await MeshSession.ConnectAsync(b, centre, Token);
        var toA = await WaitForRemoteNewAsync(a, centre);
        AssertEntries(toA.Data, (b.Started.BoundAddress, b.Started.PeerGuid));
        var toB = await WaitForRemoteNewAsync(b, centre);
        AssertEntries(toB.Data, (a.Started.BoundAddress, a.Started.PeerGuid));

        // The third client is sent both others in one message, in the centre's connection list order.
        await MeshSession.ConnectAsync(c, centre, Token);
        var toC = await WaitForRemoteNewAsync(c, centre);
        AssertEntries(toC.Data, (a.Started.BoundAddress, a.Started.PeerGuid), (b.Started.BoundAddress, b.Started.PeerGuid));
        AssertEntries((await WaitForRemoteNewAsync(a, centre)).Data, (c.Started.BoundAddress, c.Started.PeerGuid));
        AssertEntries((await WaitForRemoteNewAsync(b, centre)).Data, (c.Started.BoundAddress, c.Started.PeerGuid));

        var centreInfo = await Graph(centre).GetConnectionGraph2InfoAsync(Token);
        Assert.Equal(new[] { a, b, c }.Select(p => p.Started.PeerGuid).Order(), centreInfo.Participants.Select(p => p.PeerGuid));
        Assert.All(centreInfo.Participants, p => Assert.Empty(p.Connections));
        var aInfo = await Graph(a).GetConnectionGraph2InfoAsync(Token);
        var listedByCentre = Assert.Single(aInfo.Participants);
        Assert.Equal(centre.Started.PeerGuid, listedByCentre.PeerGuid);
        Assert.Equal(
            new[] { b, c }.Select(p => (p.Started.PeerGuid, p.Started.BoundAddress)).OrderBy(p => p.PeerGuid),
            listedByCentre.Connections.Select(x => (x.PeerGuid, x.Address)));
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_disconnect_and_a_lost_connection_are_announced_to_the_others(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 4), Token);
        var (centre, a, b, c) = (mesh[0], mesh[1], mesh[2], mesh[3]);
        await AttachAsync(mesh);
        foreach (var client in new[] { a, b, c })
            await MeshSession.ConnectAsync(client, centre, Token);
        await MeshSession.WaitUntilAsync(async () => (await Graph(a).GetConnectionGraph2InfoAsync(Token)).Participants.Single().Connections.Count == 2,
            "the first client lists both others under the centre", Token);

        // b leaves gracefully: ID_REMOTE_DISCONNECTION_NOTIFICATION, SystemAddress, RakNetGUID (ConnectionGraph2.cpp:162-169).
        await b.Driver.CloseAsync(centre.Started.PeerGuid, notify: true, Token);
        var expected = AddressAndGuid(MessageId.RemoteDisconnectionNotification, b.Started.BoundAddress, b.Started.PeerGuid);
        foreach (var told in new[] { a, c })
        {
            var notice = await told.Inbox.WaitForPacketAsync(
                p => p.MessageId == (byte)MessageId.RemoteDisconnectionNotification, "ID_REMOTE_DISCONNECTION_NOTIFICATION", Timeout, Token);
            Assert.Equal(centre.Started.PeerGuid, notice.PeerGuid);
            Assert.Equal(expected, notice.Data.ToArray());
        }

        // The centre drops c without notifying it: ID_CONNECTION_LOST locally, so ID_REMOTE_CONNECTION_LOST to a.
        await centre.Driver.CloseAsync(c.Started.PeerGuid, notify: false, Token);
        var lost = await a.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RemoteConnectionLost, "ID_REMOTE_CONNECTION_LOST", Timeout, Token);
        Assert.Equal(AddressAndGuid(MessageId.RemoteConnectionLost, c.Started.BoundAddress, c.Started.PeerGuid), lost.Data.ToArray());

        Assert.Empty((await Graph(a).GetConnectionGraph2InfoAsync(Token)).Participants.Single().Connections);
        Assert.Equal([a.Started.PeerGuid], (await Graph(centre).GetConnectionGraph2InfoAsync(Token)).Participants.Select(p => p.PeerGuid));
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Without_auto_processing_a_system_is_announced_only_once_added(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        var (centre, a, b) = (mesh[0], mesh[1], mesh[2]);
        await Graph(centre).AttachConnectionGraph2Async(autoProcess: false, Token);
        await Graph(a).AttachConnectionGraph2Async(cancellationToken: Token);
        await Graph(b).AttachConnectionGraph2Async(cancellationToken: Token);
        await MeshSession.ConnectAsync(a, centre, Token);
        await MeshSession.ConnectAsync(b, centre, Token);
        Assert.Empty((await Graph(centre).GetConnectionGraph2InfoAsync(Token)).Participants);

        // ConnectionGraph2.cpp:197-245: added by hand, b is announced to a and a is listed to b.
        await Graph(centre).AddConnectionGraph2ParticipantAsync(b.Started.PeerGuid, Token);

        AssertEntries((await WaitForRemoteNewAsync(a, centre)).Data, (b.Started.BoundAddress, b.Started.PeerGuid));
        AssertEntries((await WaitForRemoteNewAsync(b, centre)).Data, (a.Started.BoundAddress, a.Started.PeerGuid));
        Assert.Equal([b.Started.PeerGuid], (await Graph(centre).GetConnectionGraph2InfoAsync(Token)).Participants.Select(p => p.PeerGuid));
        // a is not a participant of the centre, so its reports are ignored there, and nothing was announced to it before.
        Assert.DoesNotContain(a.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.RemoteNewIncomingConnection);
    }

    private static ITopologyDriver Graph(InteropPeer peer) => (ITopologyDriver)peer.Driver;

    private static async Task AttachAsync(MeshSession mesh)
    {
        foreach (var peer in mesh.Peers)
            await Graph(peer).AttachConnectionGraph2Async(cancellationToken: Token);
    }

    private static Task<ReceivedPacket> WaitForRemoteNewAsync(InteropPeer peer, InteropPeer from) =>
        peer.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.RemoteNewIncomingConnection && p.PeerGuid == from.Started.PeerGuid,
            $"ID_REMOTE_NEW_INCOMING_CONNECTION from {from.Label}", Timeout, Token);

    // The count, then for each system its address and GUID; the ping is the sender's average, which varies.
    private static void AssertEntries(ReadOnlyMemory<byte> data, params (IPEndPoint Address, ulong Guid)[] expected)
    {
        var bytes = data.Span;
        Assert.Equal((byte)MessageId.RemoteNewIncomingConnection, bytes[0]);
        Assert.Equal((uint)expected.Length, BinaryPrimitives.ReadUInt32BigEndian(bytes[1..]));
        Assert.Equal(1 + 4 + expected.Length * (7 + 8 + 2), bytes.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            var entry = bytes.Slice(1 + 4 + i * 17, 15).ToArray();
            Assert.Equal(AddressAndGuid(null, expected[i].Address, expected[i].Guid), entry);
        }
    }

    private static byte[] AddressAndGuid(MessageId? id, IPEndPoint address, ulong guid)
    {
        var buffer = new byte[1 + 7 + 8];
        var writer = new BitStreamWriter(buffer);
        if (id is { } messageId)
            writer.WriteByte((byte)messageId);
        writer.WriteAddress(address);
        writer.WriteRakNetGuid(new RakNetGuid(guid));
        return writer.Written.ToArray();
    }
}
