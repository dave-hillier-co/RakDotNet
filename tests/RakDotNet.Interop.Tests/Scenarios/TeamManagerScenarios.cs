using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// TeamManager, from Samples/TeamManager: FullyConnectedMesh2 elects the host, and three teams are static objects on every
/// system, two player teams of two that balancing applies to and a referee team of one joined only on request. The
/// sample's ReplicaManager3 replication is done here by passing each object's SerializeConstruction bytes to the others'
/// DeserializeConstruction, and its keyboard commands become the steps of each scenario. Every step waits until every
/// system agrees, so the host's answers and the messages relayed between members are checked on both kinds of peer.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class TeamManagerScenarios
{
    private const byte World = 0;
    private const ulong PlayerTeam1 = 0;
    private const ulong PlayerTeam2 = 1;
    private const ulong RefereeTeam = 2;
    private const ulong NoTeamListed = ulong.MaxValue;

    // The host is decided by elapsed runtime since FullyConnectedMesh2 was attached; this separates the first system's.
    private static readonly TimeSpan Seniority = TimeSpan.FromMilliseconds(300);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => MeshSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Static_teams_and_every_user_are_serialised_to_every_system(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormLobbyAsync(mesh);

        // TM_Team::SerializeConstruction (TeamManager.cpp:965-974): world, NetworkID, permissions, a balancing bit, the limit.
        Assert.Equal([World, .. Id(PlayerTeam1), (byte)JoinPermissions.All, 0x80, 0x01, 0x00], await Teams(mesh[0]).SerializeTeamAsync(World, PlayerTeam1, Token));
        Assert.Equal([World, .. Id(RefereeTeam), (byte)JoinPermissions.SpecificTeam, 0x00, 0x00, 0x80], await Teams(mesh[0]).SerializeTeamAsync(World, RefereeTeam, Token));
        foreach (var peer in mesh.Peers)
        {
            // TM_TeamMember::SerializeConstruction (TeamManager.cpp:442-472): no requests, then the TEAM_ASSIGNED body.
            var user = User(mesh, peer);
            Assert.Equal([World, .. Id(user), 0, 0, World, .. Id(user), 0, 0, 0, 0, 0, 0, 2], await Teams(peer).SerializeTeamMemberAsync(World, user, Token));

            var info = await Teams(peer).GetTeamWorldInfoAsync(World, Token);
            Assert.Equal(mesh[0].Started.PeerGuid, info.Host);
            Assert.False(info.BalanceTeams);
            Assert.Equal(mesh.Peers.Where(p => p != peer).Select(p => p.Started.PeerGuid).Order(), info.Participants.Order());
            Assert.Equal(
                [new TeamSummary(PlayerTeam1, 2, JoinPermissions.All, true), new(PlayerTeam2, 2, JoinPermissions.All, true), new(RefereeTeam, 1, JoinPermissions.SpecificTeam, false)],
                info.Teams.Select(t => new TeamSummary(t.Team, t.MemberLimitSetting, t.JoinPermissions, t.BalancingApplies)));
            Assert.Equal(mesh.Peers.Select(p => User(mesh, p)).Order(), info.Members.Select(m => m.Member).Order());
            Assert.All(info.Members, m => Assert.Equal((0, JoinTeamType.NoTeam, 0), (m.Teams.Count, m.RequestedTeam, m.NoTeamId)));
            Assert.DoesNotContain(peer.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.TeamBalancerInternal);
        }
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Requests_fill_the_emptiest_team_and_a_full_team_keeps_the_request_until_a_place_frees(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormLobbyAsync(mesh);
        var (host, second, third) = (mesh[0], mesh[1], mesh[2]);

        // Sample 'A': any team, from each system in turn, the host's own request answered by itself.
        foreach (var (peer, team) in new[] { (second, PlayerTeam1), (third, PlayerTeam2), (host, PlayerTeam1) })
        {
            Assert.True(await Teams(peer).RequestTeamAsync(World, User(mesh, peer), JoinTeamType.AnyAvailableTeam, cancellationToken: Token));
            // EncodeTeamAssigned (TeamManager.cpp:1984-1995): world, member, the teams, the no-team subcategory and the
            // 32-bit JoinTeamType, JOIN_NO_TEAM once the request is met.
            byte[] assigned = [(byte)MessageId.TeamBalancerTeamAssigned, World, .. Id(User(mesh, peer)), 0, 1, .. Id(team), 0, 0, 0, 0, 2];
            await WaitForEverySystemAsync(mesh, p => p.MessageId == (byte)MessageId.TeamBalancerTeamAssigned && Member(p) == User(mesh, peer),
                p => Assert.Equal(assigned, p.Data.ToArray()), "ID_TEAM_BALANCER_TEAM_ASSIGNED");
        }
        await WaitUntilAgreedAsync(mesh, i => i.Team(PlayerTeam1).Members.SequenceEqual([User(mesh, second), User(mesh, host)])
                                              && i.Team(PlayerTeam2).Members.SequenceEqual([User(mesh, third)]), "the players are on the emptiest teams");

        // Sample 'B': the referee team, then a second request for it, refused on every system as full.
        Assert.True(await Teams(second).RequestTeamAsync(World, User(mesh, second), JoinTeamType.SpecificTeam, RefereeTeam, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(RefereeTeam).Members.SequenceEqual([User(mesh, second)]), "the second system's user is the referee");
        Assert.True(await Teams(third).RequestTeamAsync(World, User(mesh, third), JoinTeamType.SpecificTeam, RefereeTeam, cancellationToken: Token));
        // EncodeTeamFullOrLocked (TeamManager.cpp:1931-1940): world, member, team, count, limit, balancing bit, permissions.
        byte[] full = [(byte)MessageId.TeamBalancerRequestedTeamFull, World, .. Id(User(mesh, third)), .. Id(RefereeTeam), 0, 1, 0, 1, 0x01, 0x00];
        await WaitForEverySystemAsync(mesh, p => p.MessageId == (byte)MessageId.TeamBalancerRequestedTeamFull,
            p => Assert.Equal(full, p.Data.ToArray()), "ID_TEAM_BALANCER_REQUESTED_TEAM_FULL");
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, third)).RequestedTeams.SequenceEqual([RefereeTeam]), "the request is kept everywhere");

        // Sample 'D': cancelled, and every system told.
        Assert.True(await Teams(third).CancelTeamRequestAsync(World, User(mesh, third), RefereeTeam, Token));
        byte[] cancelled = [(byte)MessageId.TeamBalancerTeamRequestedCancelled, World, .. Id(User(mesh, third)), .. AfterBit(true, Id(RefereeTeam))];
        await WaitForEverySystemAsync(mesh, p => p.MessageId == (byte)MessageId.TeamBalancerTeamRequestedCancelled,
            p => Assert.Equal(cancelled, p.Data.ToArray()), "ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED");
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, third)).RequestedTeams.Count == 0, "the request is gone everywhere");

        // Asked again, then sample 'E': the referee leaves, and the host lets the waiting member in.
        Assert.True(await Teams(third).RequestTeamAsync(World, User(mesh, third), JoinTeamType.SpecificTeam, RefereeTeam, cancellationToken: Token));
        await WaitForEverySystemAsync(mesh, p => p.MessageId == (byte)MessageId.TeamBalancerRequestedTeamFull, _ => { }, "ID_TEAM_BALANCER_REQUESTED_TEAM_FULL again");
        Assert.True(await Teams(second).LeaveTeamAsync(World, User(mesh, second), RefereeTeam, 0, Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(RefereeTeam).Members.SequenceEqual([User(mesh, third)])
                                              && i.Member(User(mesh, third)).Teams.SequenceEqual([PlayerTeam2, RefereeTeam])
                                              && i.Member(User(mesh, third)).RequestedTeams.Count == 0
                                              && i.Member(User(mesh, second)).Teams.SequenceEqual([PlayerTeam1]), "the waiting member is the referee");
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task Switching_member_limits_and_balancing_move_members_between_teams(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormLobbyAsync(mesh);
        var (host, second, third) = (mesh[0], mesh[1], mesh[2]);
        foreach (var peer in new[] { second, third })
        {
            Assert.True(await Teams(peer).RequestTeamAsync(World, User(mesh, peer), JoinTeamType.SpecificTeam, PlayerTeam1, cancellationToken: Token));
            await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, peer)).Teams.SequenceEqual([PlayerTeam1]), "on the first team");
        }

        // Sample 'C': a switch from the first team to the second.
        Assert.True(await Teams(third).RequestTeamSwitchAsync(World, User(mesh, third), PlayerTeam2, PlayerTeam1, Token));
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, third)).Teams.SequenceEqual([PlayerTeam2]), "the switch is made");
        Assert.True(await Teams(host).RequestTeamAsync(World, User(mesh, host), JoinTeamType.SpecificTeam, PlayerTeam2, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(PlayerTeam2).Members.SequenceEqual([User(mesh, third), User(mesh, host)]), "the host's user joins the second team");

        // Sample 'G' on a system that is not host: the newest member of the second team moves to the first, the emptiest
        // team that allows rebalancing (TeamManager.cpp:1406-1459).
        Assert.True(await Teams(second).SetTeamMemberLimitAsync(World, PlayerTeam2, 1, 0, Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(PlayerTeam2).MemberLimitSetting == 1
                                              && i.Team(PlayerTeam2).Members.SequenceEqual([User(mesh, third)])
                                              && i.Team(PlayerTeam1).Members.SequenceEqual([User(mesh, second), User(mesh, host)]), "the limit trims the team");

        // Sample 'H': three members over three teams makes one each; with nowhere to go the newest member of the first
        // team leaves to no team.
        Assert.True(await Teams(third).SetBalanceTeamsAsync(World, true, 4, Token));
        await WaitUntilAgreedAsync(mesh, i => i.BalanceTeams
                                              && i.Team(PlayerTeam1).Members.SequenceEqual([User(mesh, second)])
                                              && i.Member(User(mesh, host)).Teams.Count == 0
                                              && i.Member(User(mesh, host)).NoTeamId == 4
                                              && i.Team(PlayerTeam1).MemberLimit == 1, "balancing empties the overfull team");
        Assert.False(await Teams(third).SetBalanceTeamsAsync(World, true, 4, Token));
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task When_the_host_leaves_the_next_host_resolves_requests(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        await FormLobbyAsync(mesh);
        var (host, second, third) = (mesh[0], mesh[1], mesh[2]);
        Assert.True(await Teams(second).RequestTeamAsync(World, User(mesh, second), JoinTeamType.AnyAvailableTeam, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, second)).Teams.SequenceEqual([PlayerTeam1]), "on the first team");

        await host.Driver.ShutdownAsync(100, Token);
        // TeamManager.cpp:2171-2177: ID_FCM2_NEW_HOST makes the next oldest system every world's host. The departed
        // user's replica is destroyed, which dereferences it, as the sample's ReplicaManager3 does.
        var remaining = new[] { second, third };
        foreach (var peer in remaining)
        {
            await peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Fcm2NewHost && p.PeerGuid == second.Started.PeerGuid,
                "ID_FCM2_NEW_HOST naming peer 1", Timeout, Token);
            await Teams(peer).DereferenceTeamMemberAsync(World, User(mesh, host), Token);
        }
        await WaitUntilAgreedAsync(remaining, i => i.Host == second.Started.PeerGuid && i.Participants.Count == 1, "peer 1 is host");

        Assert.True(await Teams(third).RequestTeamAsync(World, User(mesh, third), JoinTeamType.AnyAvailableTeam, cancellationToken: Token));

        var assigned = await third.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.TeamBalancerTeamAssigned && Member(p) == User(mesh, third), "ID_TEAM_BALANCER_TEAM_ASSIGNED from the new host", Timeout, Token);
        Assert.Equal(second.Started.PeerGuid, assigned.PeerGuid);
        await WaitUntilAgreedAsync(remaining, i => i.Team(PlayerTeam2).Members.SequenceEqual([User(mesh, third)]), "the new host put the member on the emptiest team");
    }

    [Theory]
    [MemberData(nameof(MeshMatrix.FirstAndOthers), MemberType = typeof(MeshMatrix))]
    public async Task A_client_server_host_relays_each_clients_changes_to_the_other_clients(PeerKind first, PeerKind others)
    {
        await using var mesh = await MeshSession.StartAsync(MeshMatrix.Kinds(first, others, 3), Token);
        var (server, client1, client2) = (mesh[0], mesh[1], mesh[2]);
        foreach (var peer in mesh.Peers)
        {
            await Teams(peer).AttachTeamManagerAsync(TeamManagerTopology.ClientServer, Token);
            await SetUpWorldAsync(mesh, peer);
        }
        // TM_World::SetHost on every system, before connecting so no client claims to be host.
        foreach (var client in new[] { client1, client2 })
        {
            await Teams(client).SetTeamHostAsync(World, server.Started.PeerGuid, Token);
            await MeshSession.ConnectAsync(client, server, Token);
        }
        await ReplicateUsersAsync(mesh);

        Assert.True(await Teams(client2).RequestTeamAsync(World, User(mesh, client2), JoinTeamType.SpecificTeam, RefereeTeam, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(RefereeTeam).Members.SequenceEqual([User(mesh, client2)]), "client 2 is the referee");
        Assert.True(await Teams(client1).RequestTeamAsync(World, User(mesh, client1), JoinTeamType.SpecificTeam, RefereeTeam, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, client1)).RequestedTeams.SequenceEqual([RefereeTeam]), "client 1 waits for the referee team");

        // TeamManager.cpp:2701-2705: the server passes the cancellation on as it came.
        Assert.True(await Teams(client1).CancelTeamRequestAsync(World, User(mesh, client1), null, Token));
        var relayed = await client2.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.TeamBalancerTeamRequestedCancelled, "ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED relayed by the server", Timeout, Token);
        Assert.Equal(server.Started.PeerGuid, relayed.PeerGuid);
        Assert.Equal([(byte)MessageId.TeamBalancerTeamRequestedCancelled, World, .. Id(User(mesh, client1)), 0x00], relayed.Data.ToArray());
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, client1)).RequestedTeams.Count == 0, "the request is gone everywhere");

        // TeamManager.cpp:2355-2359 and 2750-2751: leaving one team and leaving all are relayed too.
        Assert.True(await Teams(client2).LeaveTeamAsync(World, User(mesh, client2), RefereeTeam, 3, Token));
        await WaitUntilAgreedAsync(mesh, i => i.Team(RefereeTeam).Members.Count == 0 && i.Member(User(mesh, client2)).NoTeamId == 3, "client 2 left the referee team");
        Assert.True(await Teams(client1).RequestTeamAsync(World, User(mesh, client1), JoinTeamType.SpecificTeam, PlayerTeam1, cancellationToken: Token));
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, client1)).Teams.SequenceEqual([PlayerTeam1]), "client 1 is on the first team");
        Assert.True(await Teams(client1).LeaveAllTeamsAsync(World, User(mesh, client1), 5, Token));
        await WaitUntilAgreedAsync(mesh, i => i.Member(User(mesh, client1)).Teams.Count == 0 && i.Member(User(mesh, client1)).NoTeamId == 5, "client 1 left every team");
        Assert.Equal([server.Started.PeerGuid], (await Teams(client2).GetTeamWorldInfoAsync(World, Token)).Participants);
    }

    private static ITeamDriver Teams(InteropPeer peer) => (ITeamDriver)peer.Driver;

    private static ITopologyDriver Topology(InteropPeer peer) => (ITopologyDriver)peer.Driver;

    // Each system's user has a NetworkID of its own.
    private static ulong User(MeshSession mesh, InteropPeer peer) => 100 + (ulong)mesh.Peers.ToList().IndexOf(peer);

    private static ulong Member(ReceivedPacket packet) => BinaryPrimitives.ReadUInt64BigEndian(packet.Data.Span[2..]);

    // Samples/TeamManager: FullyConnectedMesh2 and TeamManager on each, world 0 with its participants added by hand once
    // the host is known, the static teams on every system and the host's serialised over them, and every user
    // replicated. The first system is attached earlier, so it is host. The systems connect to each other directly.
    private static async Task FormLobbyAsync(MeshSession mesh)
    {
        for (var i = 0; i < mesh.Peers.Count; i++)
        {
            var peer = mesh[i];
            await Topology(peer).AttachFullyConnectedMesh2Async(new MeshOptions(), Token);
            await Teams(peer).AttachTeamManagerAsync(cancellationToken: Token);
            await SetUpWorldAsync(mesh, peer);
            await Teams(peer).SetTeamAutoManageConnectionsAsync(false, Token);
            if (i == 0)
                await Task.Delay(Seniority, Token);
        }
        for (var i = 1; i < mesh.Peers.Count; i++)
        {
            for (var j = 0; j < i; j++)
                await MeshSession.ConnectAsync(mesh[i], mesh[j], Token);
        }
        var host = mesh[0].Started.PeerGuid;
        await WaitUntilAgreedAsync(mesh, i => i.Host == host, "every world's host is peer 0");

        // RegisterFCM2Participants, then the teams as SerializeConstructionExisting sends them.
        foreach (var peer in mesh.Peers)
        {
            foreach (var other in mesh.Peers.Where(p => p != peer))
                await Teams(peer).AddTeamParticipantAsync(World, other.Started.PeerGuid, Token);
        }
        foreach (var team in new[] { PlayerTeam1, PlayerTeam2, RefereeTeam })
        {
            var construction = await Teams(mesh[0]).SerializeTeamAsync(World, team, Token);
            foreach (var peer in mesh.Peers.Skip(1))
                Assert.True(await Teams(peer).DeserializeTeamAsync(construction, Token));
        }
        await ReplicateUsersAsync(mesh);
    }

    // The sample's static teams, and this system's user.
    private static async Task SetUpWorldAsync(MeshSession mesh, InteropPeer peer)
    {
        var teams = Teams(peer);
        await teams.AddTeamWorldAsync(World, Token);
        foreach (var team in new[] { PlayerTeam1, PlayerTeam2, RefereeTeam })
        {
            await teams.ReferenceTeamAsync(World, team, applyBalancing: team != RefereeTeam, Token);
            Assert.True(await teams.SetTeamMemberLimitAsync(World, team, team == RefereeTeam ? (ushort)1 : (ushort)2, 0, Token));
        }
        Assert.True(await teams.SetTeamJoinPermissionsAsync(World, RefereeTeam, JoinPermissions.SpecificTeam, Token));
        await teams.ReferenceTeamMemberAsync(World, User(mesh, peer), Token);
    }

    // Each user's construction, from its own system, deserialised on every other.
    private static async Task ReplicateUsersAsync(MeshSession mesh)
    {
        foreach (var peer in mesh.Peers)
        {
            var construction = await Teams(peer).SerializeTeamMemberAsync(World, User(mesh, peer), Token);
            foreach (var other in mesh.Peers.Where(p => p != peer))
                Assert.True(await Teams(other).DeserializeTeamMemberAsync(construction, Token));
        }
        await WaitUntilAgreedAsync(mesh, i => i.Members.Count == mesh.Peers.Count, "every user is on every system");
    }

    private static Task WaitUntilAgreedAsync(MeshSession mesh, Func<TeamWorldInfo, bool> condition, string description) =>
        WaitUntilAgreedAsync(mesh.Peers, condition, description);

    // Until every system's world satisfies the condition.
    private static Task WaitUntilAgreedAsync(IEnumerable<InteropPeer> peers, Func<TeamWorldInfo, bool> condition, string description) =>
        MeshSession.WaitUntilAsync(async () =>
        {
            foreach (var peer in peers)
            {
                if (!condition(await Teams(peer).GetTeamWorldInfoAsync(World, Token)))
                    return false;
            }
            return true;
        }, description, Token);

    // The first packet each system gets that matches, checked.
    private static async Task WaitForEverySystemAsync(MeshSession mesh, Func<ReceivedPacket, bool> predicate, Action<ReceivedPacket> check, string description)
    {
        foreach (var peer in mesh.Peers)
            check(await peer.Inbox.WaitForPacketAsync(predicate, $"{description} on {peer.Label}", Timeout, Token));
    }

    private static byte[] Id(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    // Bytes written after one bit: each shifted right by one, the last bit spilling into one more byte.
    private static byte[] AfterBit(bool bit, byte[] bytes)
    {
        var result = new byte[bytes.Length + 1];
        var carry = bit ? 0x80 : 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            result[i] = (byte)((bytes[i] >> 1) | carry);
            carry = (bytes[i] & 1) << 7;
        }
        result[^1] = (byte)carry;
        return result;
    }

    private sealed record TeamSummary(ulong Team, ushort MemberLimitSetting, JoinPermissions JoinPermissions, bool BalancingApplies);
}
