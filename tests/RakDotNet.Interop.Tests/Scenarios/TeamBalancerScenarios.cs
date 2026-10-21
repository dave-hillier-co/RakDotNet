using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// TeamBalancer, which RakNet 4.081 ships without a sample: what its header describes, made deterministic. Each system has
/// one member whose NetworkID is its index; FullyConnectedMesh2 makes the oldest system host, which TeamBalancer learns
/// from ID_FCM2_NEW_HOST (TeamBalancer.cpp:248-268); the team size limits are set on every system, as the header advises.
/// The host answers only the member's own system, so each step waits on that system's answer.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class TeamBalancerScenarios
{
    private const byte NoTeam = TeamBalancer.UnassignedTeamId;

    private static readonly TimeSpan Seniority = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => MeshSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Members_asking_for_any_team_join_the_smallest(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [2, 2]);

        // GetSmallestNonFullTeam (TeamBalancer.cpp:791-805): the first of the smallest; the host asks itself.
        foreach (var (peer, team) in new[] { (mesh[1], (byte)0), (mesh[2], (byte)1), (mesh[0], (byte)0) })
        {
            await Balancer(peer).RequestAnyTeamAsync(Member(mesh, peer), Token);
            var assigned = await WaitForAssignedAsync(peer, mesh, team);
            // NotifyTeamAssigment (TeamBalancer.cpp:681-692): the team, then the member.
            Assert.Equal([(byte)MessageId.TeamBalancerTeamAssigned, team, .. Id(Member(mesh, peer))], assigned.Data.ToArray());
        }
        foreach (var peer in mesh.Peers)
            Assert.DoesNotContain(peer.Inbox.Unconsumed, p => p.MessageId is (byte)MessageId.TeamBalancerInternal or (byte)MessageId.TeamBalancerTeamAssigned);
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Filling_in_order_puts_members_in_the_first_team_with_room(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [2, 2]);
        await Balancer(mesh[0]).SetDefaultAssignmentAlgorithmAsync(DefaultAssignmentAlgorithm.FillInOrder, Token);

        foreach (var (peer, team) in new[] { (mesh[1], (byte)0), (mesh[2], (byte)0), (mesh[0], (byte)1) })
        {
            await Balancer(peer).RequestAnyTeamAsync(Member(mesh, peer), Token);
            await WaitForAssignedAsync(peer, mesh, team);
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_switch_to_a_full_team_waits_until_a_member_of_it_wants_to_swap(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [1, 1]);
        var (_, second, third) = (mesh[0], mesh[1], mesh[2]);
        await JoinAnyAsync(mesh, second, 0);
        await JoinAnyAsync(mesh, third, 1);

        await Balancer(third).RequestSpecificTeamAsync(Member(mesh, third), 0, Token);

        // NotifyTeamSwitchPending (TeamBalancer.cpp:854-861): the team asked for, then the member.
        var pending = await third.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.TeamBalancerRequestedTeamFull, "ID_TEAM_BALANCER_REQUESTED_TEAM_FULL", Timeout, Token);
        Assert.Equal([(byte)MessageId.TeamBalancerRequestedTeamFull, 0, .. Id(Member(mesh, third))], pending.Data.ToArray());
        Assert.Equal(mesh[0].Started.PeerGuid, pending.PeerGuid);
        Assert.Equal(1, await Balancer(third).GetMyTeamAsync(Member(mesh, third), Token));

        // TeamBalancer.cpp:543-556: the second member wants the third's team, and the third wants the second's.
        await Balancer(second).RequestSpecificTeamAsync(Member(mesh, second), 1, Token);

        await WaitForAssignedAsync(second, mesh, 1);
        await WaitForAssignedAsync(third, mesh, 0);
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Locked_teams_refuse_a_switch_and_make_it_when_unlocked(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [2, 2]);
        var (host, second, _) = (mesh[0], mesh[1], mesh[2]);
        await JoinAnyAsync(mesh, second, 0);
        await Balancer(host).SetLockTeamsAsync(true, Token);

        await Balancer(second).RequestSpecificTeamAsync(Member(mesh, second), 1, Token);

        // NotifyTeamsLocked (TeamBalancer.cpp:847-853): the team asked for alone. The request is kept.
        var locked = await second.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.TeamBalancerRequestedTeamLocked, "ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED", Timeout, Token);
        Assert.Equal([(byte)MessageId.TeamBalancerRequestedTeamLocked, 1], locked.Data.ToArray());
        Assert.Equal(0, await Balancer(second).GetMyTeamAsync(Member(mesh, second), Token));

        // TeamBalancer.cpp:105-118: unlocking moves members waiting for a team with room.
        await Balancer(host).SetLockTeamsAsync(false, Token);
        await WaitForAssignedAsync(second, mesh, 1);

        // NotifyNoTeam (TeamBalancer.cpp:870-877): asking for no team leaves the team.
        await Balancer(second).RequestSpecificTeamAsync(Member(mesh, second), NoTeam, Token);
        var none = await WaitForAssignedAsync(second, mesh, NoTeam);
        Assert.Equal([(byte)MessageId.TeamBalancerTeamAssigned, NoTeam, .. Id(Member(mesh, second))], none.Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_member_waiting_for_a_full_team_moves_in_when_a_member_of_it_disconnects(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [1, 2]);
        var (host, second, third) = (mesh[0], mesh[1], mesh[2]);
        await JoinAnyAsync(mesh, second, 0);
        await JoinAnyAsync(mesh, third, 1);
        await Balancer(third).RequestSpecificTeamAsync(Member(mesh, third), 0, Token);
        await third.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.TeamBalancerRequestedTeamFull, "ID_TEAM_BALANCER_REQUESTED_TEAM_FULL", Timeout, Token);

        // TeamBalancer.cpp:333-368: the host drops the disconnected member and moves in the one waiting for its team.
        await second.Driver.CloseAsync(host.Started.PeerGuid, notify: true, Token);

        await WaitForAssignedAsync(third, mesh, 0);
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_new_host_is_told_each_systems_members_and_resolves_requests(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormGroupAsync(mesh, [2, 2], connectOthers: true);
        var (host, second, third) = (mesh[0], mesh[1], mesh[2]);
        await JoinAnyAsync(mesh, second, 0);
        await JoinAnyAsync(mesh, third, 1);

        // The host drops the second system first, so the second is host before the third tells it its members.
        await host.Driver.CloseAsync(second.Started.PeerGuid, notify: true, Token);
        await WaitForNewHostAsync(second, second, host);
        // TeamBalancer.cpp:369-437: the new host adds the members it is told of on their teams and tells each so.
        var own = await WaitForAssignedAsync(second, mesh, 0, from: second);
        Assert.Equal(second.Started.PeerGuid, own.PeerGuid);
        await host.Driver.CloseAsync(third.Started.PeerGuid, notify: true, Token);
        await WaitForNewHostAsync(third, second, host);
        await WaitForAssignedAsync(third, mesh, 1, from: second);

        await Balancer(third).RequestSpecificTeamAsync(Member(mesh, third), 0, Token);

        await WaitForAssignedAsync(third, mesh, 0, from: second);
    }

    private static ITeamDriver Balancer(InteropPeer peer) => (ITeamDriver)peer.Driver;

    private static ITopologyDriver Topology(InteropPeer peer) => (ITopologyDriver)peer.Driver;

    private static ulong Member(MeshSession mesh, InteropPeer peer) => (ulong)mesh.Peers.ToList().IndexOf(peer);

    // FullyConnectedMesh2 then TeamBalancer on each, the first attached earlier so it is host, the limits on every system,
    // and the others connected to the first; with connectOthers they connect to each other too, so one can succeed it.
    private static async Task FormGroupAsync(MeshSession mesh, ushort[] limits, bool connectOthers = false)
    {
        for (var i = 0; i < mesh.Peers.Count; i++)
        {
            var peer = mesh[i];
            await Topology(peer).AttachFullyConnectedMesh2Async(new MeshOptions(), Token);
            await Balancer(peer).AttachTeamBalancerAsync(Token);
            for (var team = 0; team < limits.Length; team++)
                await Balancer(peer).SetTeamSizeLimitAsync((byte)team, limits[team], Token);
            if (i < mesh.Peers.Count - 1)
                await Task.Delay(Seniority, Token);
        }
        foreach (var peer in mesh.Peers.Skip(1))
            await MeshSession.ConnectAsync(peer, mesh[0], Token);
        if (connectOthers)
        {
            for (var i = 2; i < mesh.Peers.Count; i++)
            {
                for (var j = 1; j < i; j++)
                    await MeshSession.ConnectAsync(mesh[i], mesh[j], Token);
            }
        }
        foreach (var peer in mesh.Peers.Skip(1))
            await WaitForNewHostAsync(peer, mesh[0], null);
    }

    // ID_FCM2_NEW_HOST naming host, after old host when given.
    private static Task<ReceivedPacket> WaitForNewHostAsync(InteropPeer peer, InteropPeer host, InteropPeer? oldHost) =>
        peer.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.Fcm2NewHost && p.PeerGuid == host.Started.PeerGuid
                 && (oldHost is null || BinaryPrimitives.ReadUInt64BigEndian(p.Data.Span[1..]) == oldHost.Started.PeerGuid),
            $"ID_FCM2_NEW_HOST naming {host.Label}", Timeout, Token);

    private static async Task JoinAnyAsync(MeshSession mesh, InteropPeer peer, byte team)
    {
        await Balancer(peer).RequestAnyTeamAsync(Member(mesh, peer), Token);
        await WaitForAssignedAsync(peer, mesh, team);
    }

    // The member's ID_TEAM_BALANCER_TEAM_ASSIGNED for team, from the host (peer 0 unless given), and GetMyTeam agreeing.
    private static async Task<ReceivedPacket> WaitForAssignedAsync(InteropPeer peer, MeshSession mesh, byte team, InteropPeer? from = null)
    {
        var member = Member(mesh, peer);
        var host = (from ?? mesh[0]).Started.PeerGuid;
        var assigned = await peer.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.TeamBalancerTeamAssigned && p.Data.Length >= 10 && p.Data.Span[1] == team
                 && BinaryPrimitives.ReadUInt64BigEndian(p.Data.Span[2..]) == member && p.PeerGuid == host,
            $"ID_TEAM_BALANCER_TEAM_ASSIGNED to team {team} on {peer.Label}", Timeout, Token);
        Assert.Equal(team, await Balancer(peer).GetMyTeamAsync(member, Token));
        return assigned;
    }

    private static byte[] Id(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
