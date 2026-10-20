using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TeamManager;

public sealed class TeamManagerTests : IDisposable
{
    // Samples/TeamManager: two player teams of two with balancing, and a referee team of one joined only on request.
    private const ulong PlayerTeam1 = 0;
    private const ulong PlayerTeam2 = 1;
    private const ulong RefereeTeam = 2;

    private readonly PluginMesh _mesh = new();
    private readonly List<Lobby> _systems = [];

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_new_world_has_this_system_as_host_and_no_participants()
    {
        var manager = new Plugins.TeamManager();
        var peer = _mesh.Add(4, manager);

        var world = manager.AddWorld(0);

        Assert.Equal(peer.RakNetGuid, world.Host);
        Assert.Empty(world.GetParticipantList());
        Assert.Same(world, manager.GetWorldWithId(0));
        Assert.Null(manager.GetWorldWithId(1));
        Assert.Throws<InvalidOperationException>(() => manager.AddWorld(0));
    }

    [Fact]
    public void A_team_serialises_its_world_id_network_id_permissions_balancing_and_limit()
    {
        var host = AddLobby();
        var referee = host.Teams[2];

        using var bits = new BitStream();
        referee.SerializeConstruction(bits);

        // TM_Team::SerializeConstruction (TeamManager.cpp:965-974): world, ID, permissions, a balancing bit, the limit.
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 2, (byte)JoinPermissions.SpecificTeam, 0b0000_0000, 0b0000_0000, 0b1000_0000], bits.Written.ToArray());
        Assert.Equal((10 * 8) + 1 + 16, bits.BitsWritten);
    }

    [Fact]
    public void A_deserialised_team_takes_the_hosts_settings()
    {
        var host = AddLobby();
        var manager = new Plugins.TeamManager();
        _mesh.Add(4, manager);
        manager.AddWorld(0);
        var copy = new Team();
        using var bits = new BitStream();
        host.Teams[2].SerializeConstruction(bits);

        var reader = bits.CreateReader();
        Assert.True(copy.DeserializeConstruction(manager, ref reader));

        Assert.Equal(RefereeTeam, copy.NetworkId);
        Assert.Equal(1, copy.MemberLimitSetting);
        Assert.Equal(JoinPermissions.SpecificTeam, copy.JoinPermissions);
        Assert.False(copy.BalancingApplies);
        Assert.Same(copy, manager.GetWorldWithId(0)!.GetTeamByNetworkId(RefereeTeam));
    }

    [Fact]
    public void Deserialising_into_an_unknown_world_fails()
    {
        var manager = new Plugins.TeamManager();
        _mesh.Add(4, manager);
        var reader = new BitStreamReader([7, 0, 0, 0, 0, 0, 0, 0, 1, 7, 0x80, 0x00, 0x80]);

        Assert.False(new Team().DeserializeConstruction(manager, ref reader));
    }

    [Fact]
    public void Asking_for_any_team_joins_the_emptiest_team_that_allows_it_on_every_system()
    {
        var (host, second, third) = FormLobby();

        Assert.True(second.User.RequestTeam(TeamSelection.AnyAvailable()));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        Assert.True(third.User.RequestTeam(TeamSelection.AnyAvailable()));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, third);
        Assert.True(host.User.RequestTeam(TeamSelection.AnyAvailable()));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, host);

        foreach (var system in _systems)
        {
            Assert.Equal([second.User.NetworkId, host.User.NetworkId], system.Teams[0].GetTeamMembers().Select(m => m.NetworkId));
            Assert.Equal([third.User.NetworkId], system.Teams[1].GetTeamMembers().Select(m => m.NetworkId));
            Assert.Empty(system.Teams[2].GetTeamMembers());
        }
    }

    [Fact]
    public void Every_system_is_told_a_member_was_assigned()
    {
        var (host, second, third) = FormLobby();

        second.User.RequestTeam(TeamSelection.AnyAvailable());
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        _mesh.Settle();

        // EncodeTeamAssigned (TeamManager.cpp:1984-1995): world, member, a 16-bit count and the teams, the no-team
        // subcategory and the 32-bit JoinTeamType, JOIN_NO_TEAM once the request is met.
        byte[] expected = [(byte)MessageId.TeamBalancerTeamAssigned, 0, .. Id(second.User.NetworkId), 0, 1, .. Id(PlayerTeam1), 0, 0, 0, 0, 2];
        var onHost = Assert.Single(_mesh.Seen(host.Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned);
        Assert.Equal(expected, onHost.Data);
        Assert.True(onHost.IsLocal);
        Assert.Equal(RakNetGuid.Unassigned, onHost.RakNetGuid);
        // The requester is sent it by the host; the third system makes its own.
        var onRequester = Assert.Single(_mesh.Seen(second.Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned);
        Assert.Equal(expected, onRequester.Data);
        Assert.Equal(host.Peer.RakNetGuid, onRequester.RakNetGuid);
        Assert.Equal(expected, Assert.Single(_mesh.Seen(third.Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned).Data);
        Assert.DoesNotContain(_systems.SelectMany(s => _mesh.Seen(s.Peer)), e => e.Id == (byte)MessageId.TeamBalancerInternal);

        host.Manager.DecodeTeamAssigned(onHost.Data, out var world, out var member);
        Assert.Same(host.World, world);
        Assert.Equal(second.User.NetworkId, member!.NetworkId);
    }

    [Fact]
    public void Asking_again_for_what_is_already_asked_or_held_is_refused()
    {
        var (_, second, _) = FormLobby();

        Assert.False(second.User.LeaveAllTeams(0));
        Assert.True(second.User.RequestTeam(TeamSelection.AnyAvailable()));
        Assert.False(second.User.RequestTeam(TeamSelection.AnyAvailable()));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);

        Assert.False(second.User.RequestTeam(TeamSelection.AnyAvailable()));
        Assert.False(second.User.RequestTeam(TeamSelection.Specific(second.Teams[0])));
        Assert.False(second.User.RequestTeamSwitch(second.Teams[1], second.Teams[1]));
        Assert.False(second.User.LeaveTeam(second.Teams[1], 0));
    }

    [Fact]
    public void A_full_team_refuses_on_every_system_and_lets_the_member_in_when_a_place_frees()
    {
        var (host, second, third) = FormLobby();
        third.User.RequestTeam(TeamSelection.Specific(third.Teams[2]));
        RunUntilAgreed(m => m.IsOnTeam(m.World!.GetTeamByNetworkId(RefereeTeam)!), third);

        Assert.True(second.User.RequestTeam(TeamSelection.Specific(second.Teams[2])));
        _mesh.RunUntil(() => _systems.All(s => _mesh.Seen(s.Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamFull)), "every system told the team is full");

        // EncodeTeamFullOrLocked (TeamManager.cpp:1931-1940).
        byte[] expected = [(byte)MessageId.TeamBalancerRequestedTeamFull, 0, .. Id(second.User.NetworkId), .. Id(RefereeTeam), 0, 1, 0, 1, 0b0000_0001, 0b0000_0000];
        foreach (var system in _systems)
        {
            var full = _mesh.Seen(system.Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamFull);
            Assert.Equal(expected, full.Data);
            Assert.True(system.Manager.TryDecomposeTeamFull(full.Data, out var refusal));
            Assert.Equal(second.User.NetworkId, refusal!.TeamMember!.NetworkId);
            Assert.Same(system.Teams[2], refusal.Team);
            Assert.Equal((ushort)1, refusal.CurrentMembers);
            Assert.Equal((ushort)1, refusal.MemberLimitIncludingBalancing);
            Assert.Equal(JoinPermissions.SpecificTeam, refusal.JoinPermissions);
        }
        RunUntilAgreed(m => m.RequestedTeam.SpecificTeam?.NetworkId == RefereeTeam, second);

        Assert.True(third.User.LeaveTeam(third.Teams[2], 0));

        RunUntilAgreed(m => m.IsOnTeam(m.World!.GetTeamByNetworkId(RefereeTeam)!) && m.RequestedTeamCount == 0, second);
        RunUntilAgreed(m => m.CurrentTeamCount == 0, third);
    }

    [Fact]
    public void A_team_that_does_not_allow_specific_requests_is_locked()
    {
        var (_, second, _) = FormLobby();
        second.Teams[1].SetJoinPermissions(JoinPermissions.AnyAvailableTeam);
        _mesh.RunUntil(() => _systems.All(s => s.Teams[1].JoinPermissions == JoinPermissions.AnyAvailableTeam), "the permission reached every system");

        second.User.RequestTeam(TeamSelection.Specific(second.Teams[1]));

        _mesh.RunUntil(() => _systems.All(s => _mesh.Seen(s.Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked)), "every system told the team is locked");
        foreach (var system in _systems)
        {
            var locked = _mesh.Seen(system.Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked);
            Assert.True(system.Manager.TryDecomposeTeamLocked(locked.Data, out var refusal));
            Assert.Same(system.Teams[1], refusal!.Team);
        }
    }

    [Fact]
    public void Asking_for_any_team_when_none_allows_it_is_refused_as_locked_with_no_team()
    {
        var (_, second, _) = FormLobby();
        foreach (var team in second.Teams)
            team.SetJoinPermissions(JoinPermissions.SpecificTeam);
        _mesh.Settle();

        second.User.RequestTeam(TeamSelection.AnyAvailable());

        _mesh.RunUntil(() => _mesh.Seen(second.Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked), "the requester told it is locked");
        var locked = _mesh.Seen(second.Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked);
        Assert.True(second.Manager.TryDecomposeTeamLocked(locked.Data, out var refusal));
        Assert.Equal(ulong.MaxValue, refusal!.TeamId);
        Assert.Null(refusal.Team);
        RunUntilAgreed(m => m.RequestedTeam.JoinTeamType == JoinTeamType.AnyAvailableTeam, second);
    }

    [Fact]
    public void Cancelling_a_request_tells_every_system()
    {
        var (_, second, third) = FormLobby();
        third.User.RequestTeam(TeamSelection.Specific(third.Teams[2]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, third);
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[2]));
        RunUntilAgreed(m => m.RequestedTeamCount == 1, second);

        Assert.True(second.User.CancelTeamRequest(second.Teams[2]));

        RunUntilAgreed(m => m.RequestedTeamCount == 0, second);
        byte[] expected = [(byte)MessageId.TeamBalancerTeamRequestedCancelled, 0, .. Id(second.User.NetworkId), .. AfterBit(true, Id(RefereeTeam))];
        foreach (var system in _systems)
        {
            _mesh.RunUntil(() => _mesh.Seen(system.Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerTeamRequestedCancelled), "cancelled");
            var cancelled = _mesh.Seen(system.Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerTeamRequestedCancelled);
            Assert.Equal(expected, cancelled.Data);
            system.Manager.DecodeTeamCancelled(cancelled.Data, out _, out var member, out var team);
            Assert.Equal(second.User.NetworkId, member!.NetworkId);
            Assert.Same(system.Teams[2], team);
        }
        Assert.False(second.User.CancelTeamRequest(second.Teams[2]));
    }

    [Fact]
    public void A_team_switch_leaves_the_old_team_as_it_joins_the_new_one()
    {
        var (_, second, _) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeam?.NetworkId == PlayerTeam1, second);

        Assert.True(second.User.RequestTeamSwitch(second.Teams[1], second.Teams[0]));

        RunUntilAgreed(m => m.CurrentTeamCount == 1 && m.CurrentTeam!.NetworkId == PlayerTeam2, second);
        Assert.Equal([PlayerTeam1], second.User.GetLastTeams().Select(t => t.NetworkId));
    }

    [Fact]
    public void Lowering_a_member_limit_moves_the_newest_member_to_the_emptiest_team()
    {
        var (host, second, third) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        third.User.RequestTeam(TeamSelection.Specific(third.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, third);

        Assert.True(second.Teams[0].SetMemberLimit(1, 0));

        RunUntilAgreed(m => m.CurrentTeam?.NetworkId == PlayerTeam2, third);
        RunUntilAgreed(m => m.CurrentTeam?.NetworkId == PlayerTeam1, second);
        Assert.All(_systems, s => Assert.Equal(1, s.Teams[0].MemberLimitSetting));
        Assert.Equal(1, host.Teams[0].TeamMembersCount);
    }

    [Fact]
    public void A_member_with_nowhere_to_go_is_moved_to_no_team()
    {
        var (_, second, third) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        third.User.RequestTeam(TeamSelection.Specific(third.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, third);
        second.Teams[1].SetMemberLimit(0, 0);
        _mesh.Settle();

        second.Teams[0].SetMemberLimit(1, 7);

        RunUntilAgreed(m => m.CurrentTeamCount == 0 && m.NoTeamId == 7, third);
    }

    [Fact]
    public void Balancing_moves_members_off_a_team_with_more_than_its_share()
    {
        var (host, second, third) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        third.User.RequestTeam(TeamSelection.Specific(third.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, third);

        Assert.True(second.World.SetBalanceTeams(true, 0));

        // Three members over three teams: one each. The referee team does not allow rebalancing, so the newest member of
        // the first team moves to the second.
        RunUntilAgreed(m => m.CurrentTeam?.NetworkId == PlayerTeam2, third);
        _mesh.RunUntil(() => _systems.All(s => s.World.BalanceTeams), "balancing on every system");
        Assert.Equal(1, host.Teams[0].MemberLimit);
        Assert.Equal(2, host.Teams[0].MemberLimitSetting);
    }

    [Fact]
    public void Leaving_every_team_is_applied_at_once_and_told_to_every_system()
    {
        var (_, second, _) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);

        Assert.True(second.User.LeaveAllTeams(3));

        Assert.Equal(0, second.User.CurrentTeamCount);
        Assert.Equal(3, second.User.NoTeamId);
        RunUntilAgreed(m => m.CurrentTeamCount == 0 && m.NoTeamId == 3, second);
    }

    [Fact]
    public void A_member_serialises_its_requests_and_teams_and_is_rebuilt_from_them()
    {
        var (host, second, _) = FormLobby();
        second.User.RequestTeam(TeamSelection.Specific(second.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, second);
        second.Teams[1].SetMemberLimit(0, 0);
        _mesh.Settle();
        second.User.RequestTeamSwitch(second.Teams[1], second.Teams[0]);
        _mesh.Settle();

        using var bits = new BitStream();
        host.World.GetTeamMemberByNetworkId(second.User.NetworkId)!.SerializeConstruction(bits);

        var manager = new Plugins.TeamManager();
        _mesh.Add(4, manager);
        var world = manager.AddWorld(0);
        var teams = ReferenceTeams(world);
        var copy = new TeamMember();
        var reader = bits.CreateReader();
        Assert.True(copy.DeserializeConstruction(manager, ref reader));

        Assert.Equal(second.User.NetworkId, copy.NetworkId);
        Assert.Equal([teams[0]], copy.GetCurrentTeams());
        Assert.Equal([teams[1]], copy.GetRequestedSpecificTeams());
    }

    [Fact]
    public void A_client_server_host_relays_what_one_client_sends_to_the_others()
    {
        var (server, first, second) = FormClientServer();
        first.User.RequestTeam(TeamSelection.Specific(first.Teams[0]));
        RunUntilAgreed(m => m.CurrentTeamCount == 1, first);

        Assert.True(first.User.LeaveAllTeams(0));

        RunUntilAgreed(m => m.CurrentTeamCount == 0, first);
        // The second client is connected only to the server, which relayed both changes.
        Assert.Equal([server.Peer.RakNetGuid], second.World.GetParticipantList());
        Assert.Equal([first.Peer.RakNetGuid, second.Peer.RakNetGuid], server.World.GetParticipantList());
    }

    [Fact]
    public void A_new_host_from_FullyConnectedMesh2_becomes_every_worlds_host()
    {
        var manager = new Plugins.TeamManager();
        var peer = _mesh.Add(4, manager);
        var world = manager.AddWorld(0);
        var other = new RakNetGuid(0x1234);

        peer.Inject([(byte)MessageId.Fcm2NewHost, .. new byte[16]], SystemAddress.Unassigned, other);
        _mesh.Step();

        Assert.Equal(other, world.Host);
    }

    [Fact]
    public void The_balanced_limit_leaves_out_teams_whose_own_limit_is_lower()
    {
        var manager = new Plugins.TeamManager();
        _mesh.Add(4, manager);
        var world = manager.AddWorld(0);
        var small = new Team();
        var large = new Team();
        var other = new Team();
        world.ReferenceTeam(small, 1, true);
        world.ReferenceTeam(large, 2, true);
        world.ReferenceTeam(other, 3, true);
        small.SetMemberLimit(1, 0);
        for (var i = 0; i < 7; i++)
            world.ReferenceTeamMember(new TeamMember(), (ulong)(10 + i));
        _mesh.Settle();

        world.SetBalanceTeams(true, 0);

        // (7 + 2) / 3 = 3 per team, but the small team holds at most 1: (6 + 1) / 2 = 3 for the others.
        Assert.Equal(3, large.MemberLimit);
        Assert.Equal(1, small.MemberLimit);
    }

    private (Lobby Host, Lobby Second, Lobby Third) FormLobby()
    {
        var host = AddLobby();
        var second = AddLobby();
        var third = AddLobby();
        second.World.Host = host.Peer.RakNetGuid;
        third.World.Host = host.Peer.RakNetGuid;
        _mesh.Connect(second.Peer, host.Peer);
        _mesh.Connect(third.Peer, host.Peer);
        _mesh.Connect(third.Peer, second.Peer);
        _mesh.Settle();
        Replicate();
        return (host, second, third);
    }

    private (Lobby Server, Lobby First, Lobby Second) FormClientServer()
    {
        var server = AddLobby(TeamManagerTopology.ClientServer);
        var first = AddLobby(TeamManagerTopology.ClientServer);
        var second = AddLobby(TeamManagerTopology.ClientServer);
        first.World.Host = server.Peer.RakNetGuid;
        second.World.Host = server.Peer.RakNetGuid;
        _mesh.Connect(first.Peer, server.Peer);
        _mesh.Connect(second.Peer, server.Peer);
        _mesh.Settle();
        Replicate();
        return (server, first, second);
    }

    // Each system's user replicated to every other, as ReplicaManager3 does in the sample.
    private void Replicate()
    {
        foreach (var from in _systems)
        {
            using var bits = new BitStream();
            from.User.SerializeConstruction(bits);
            foreach (var to in _systems.Where(s => s != from))
            {
                var reader = bits.CreateReader();
                Assert.True(new TeamMember().DeserializeConstruction(to.Manager, ref reader));
            }
        }
    }

    private Lobby AddLobby(TeamManagerTopology topology = TeamManagerTopology.PeerToPeer)
    {
        var manager = new Plugins.TeamManager { Topology = topology };
        var peer = _mesh.Add(4, manager);
        var world = manager.AddWorld(0);
        var teams = ReferenceTeams(world);
        var user = new TeamMember();
        world.ReferenceTeamMember(user, (ulong)(100 + _systems.Count));
        _mesh.Settle();
        var lobby = new Lobby(peer, manager, world, teams, user);
        _systems.Add(lobby);
        return lobby;
    }

    private static Team[] ReferenceTeams(TeamWorld world)
    {
        Team[] teams = [new(), new(), new()];
        for (var i = 0; i < teams.Length; i++)
        {
            world.ReferenceTeam(teams[i], (ulong)i, applyBalancing: i != 2);
            teams[i].SetMemberLimit(i == 2 ? (ushort)1 : (ushort)2, 0);
        }
        teams[2].SetJoinPermissions(JoinPermissions.SpecificTeam);
        return teams;
    }

    // Until every system's copy of the member satisfies the condition.
    private void RunUntilAgreed(Func<TeamMember, bool> condition, Lobby owner) =>
        _mesh.RunUntil(() => _systems.All(s => s.World.GetTeamMemberByNetworkId(owner.User.NetworkId) is { } m && condition(m)),
            "every system agrees on the member");

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

    private sealed record Lobby(RakPeer Peer, Plugins.TeamManager Manager, TeamWorld World, Team[] Teams, TeamMember User);
}
