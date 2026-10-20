using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TeamBalancer;

public sealed class TeamBalancerTests : IDisposable
{
    private const byte NoTeam = Plugins.TeamBalancer.UnassignedTeamId;

    private readonly PluginMesh _mesh = new();
    private readonly List<(RakPeer Peer, Plugins.TeamBalancer Balancer)> _systems = [];

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void Until_FullyConnectedMesh2_names_another_this_system_is_host()
    {
        var (peer, balancer) = AddSystem();

        Assert.Equal(peer.RakNetGuid, balancer.HostGuid);
        Assert.Equal(NoTeam, balancer.GetMyTeam(1));
    }

    [Fact]
    public void Members_asking_for_any_team_join_the_smallest()
    {
        var (host, clients) = FormGroup(limits: [2, 2]);

        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        clients[1].Balancer.RequestAnyTeam(2);
        RunUntilTeam(clients[1].Balancer, 2, 1);
        host.Balancer.RequestAnyTeam(3);
        RunUntilTeam(host.Balancer, 3, 0);

        // NotifyTeamAssigment (TeamBalancer.cpp:681-692): the team, then the member, to the member's system only.
        var assigned = Assert.Single(_mesh.Seen(clients[1].Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned);
        Assert.Equal([(byte)MessageId.TeamBalancerTeamAssigned, 1, .. Id(2)], assigned.Data);
        Assert.Equal(host.Peer.RakNetGuid, assigned.RakNetGuid);
        Assert.DoesNotContain(_mesh.Seen(clients[0].Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned && e.Data[^1] == 2);
        Assert.DoesNotContain(_systems.SelectMany(s => _mesh.Seen(s.Peer)), e => e.Id == (byte)MessageId.TeamBalancerInternal);
    }

    [Fact]
    public void Filling_in_order_puts_members_in_the_first_team_with_room()
    {
        var (host, clients) = FormGroup(limits: [2, 2]);
        host.Balancer.DefaultAssignmentAlgorithm = DefaultAssignmentAlgorithm.FillInOrder;

        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        clients[1].Balancer.RequestAnyTeam(2);
        RunUntilTeam(clients[1].Balancer, 2, 0);
        host.Balancer.RequestAnyTeam(3);

        RunUntilTeam(host.Balancer, 3, 1);
    }

    [Fact]
    public void A_switch_to_a_full_team_waits_until_a_member_of_it_wants_to_swap()
    {
        var (_, clients) = FormGroup(limits: [1, 1]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        clients[1].Balancer.RequestAnyTeam(2);
        RunUntilTeam(clients[1].Balancer, 2, 1);

        clients[1].Balancer.RequestSpecificTeam(2, 0);

        // NotifyTeamSwitchPending (TeamBalancer.cpp:854-861): the team asked for, then the member.
        _mesh.RunUntil(() => _mesh.Seen(clients[1].Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamFull), "the switch is pending");
        Assert.Equal([(byte)MessageId.TeamBalancerRequestedTeamFull, 0, .. Id(2)],
            _mesh.Seen(clients[1].Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamFull).Data);
        Assert.Equal(1, clients[1].Balancer.GetMyTeam(2));

        clients[0].Balancer.RequestSpecificTeam(1, 1);

        RunUntilTeam(clients[0].Balancer, 1, 1);
        RunUntilTeam(clients[1].Balancer, 2, 0);
    }

    [Fact]
    public void While_teams_are_locked_a_switch_is_refused_and_made_on_unlocking()
    {
        var (host, clients) = FormGroup(limits: [2, 2]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        host.Balancer.SetLockTeams(true);

        clients[0].Balancer.RequestSpecificTeam(1, 1);

        // NotifyTeamsLocked (TeamBalancer.cpp:847-853): the team asked for alone.
        _mesh.RunUntil(() => _mesh.Seen(clients[0].Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked), "the switch is locked");
        Assert.Equal([(byte)MessageId.TeamBalancerRequestedTeamLocked, 1],
            _mesh.Seen(clients[0].Peer).Single(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamLocked).Data);
        Assert.Equal(0, clients[0].Balancer.GetMyTeam(1));

        host.Balancer.SetLockTeams(false);

        RunUntilTeam(clients[0].Balancer, 1, 1);
    }

    [Fact]
    public void Asking_for_no_team_leaves_the_team()
    {
        var (_, clients) = FormGroup(limits: [1, 1]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);

        clients[0].Balancer.RequestSpecificTeam(1, NoTeam);
        RunUntilTeam(clients[0].Balancer, 1, NoTeam);

        // The place it left is free for another.
        clients[1].Balancer.RequestSpecificTeam(2, 0);
        RunUntilTeam(clients[1].Balancer, 2, 0);
    }

    [Fact]
    public void When_a_member_disconnects_a_member_waiting_for_its_team_moves_in()
    {
        var (host, clients) = FormGroup(limits: [1, 2]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        clients[1].Balancer.RequestAnyTeam(2);
        RunUntilTeam(clients[1].Balancer, 2, 1);
        clients[1].Balancer.RequestSpecificTeam(2, 0);
        _mesh.RunUntil(() => _mesh.Seen(clients[1].Peer).Any(e => e.Id == (byte)MessageId.TeamBalancerRequestedTeamFull), "the switch is pending");

        clients[0].Peer.Close(host.Peer.RakNetGuid, notify: true);

        RunUntilTeam(clients[1].Balancer, 2, 0);
    }

    [Fact]
    public void Forcing_even_teams_moves_a_member_from_the_largest_team()
    {
        var (host, clients) = FormGroup(limits: [3, 3]);
        clients[0].Balancer.RequestSpecificTeam(1, 0);
        RunUntilTeam(clients[0].Balancer, 1, 0);
        clients[1].Balancer.RequestSpecificTeam(2, 0);
        RunUntilTeam(clients[1].Balancer, 2, 0);

        host.Balancer.SetForceEvenTeams(true);

        _mesh.RunUntil(() => clients.Count(c => c.Balancer.GetMyTeam(c == clients[0] ? 1UL : 2UL) == 1) == 1, "one member moved to the empty team");
    }

    [Fact]
    public void A_new_host_is_told_each_systems_members()
    {
        var (host, clients) = FormGroup(limits: [2, 2]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);

        // ID_FCM2_NEW_HOST naming the client: it becomes host, sends itself its members (TeamBalancer.cpp:248-268) and,
        // knowing nobody, adds the member on its team and tells it so.
        clients[0].Peer.Inject([(byte)MessageId.Fcm2NewHost, .. new byte[16]], SystemAddress.Unassigned, clients[0].Peer.RakNetGuid, generatedLocally: true);
        _mesh.Settle();

        Assert.Equal(clients[0].Peer.RakNetGuid, clients[0].Balancer.HostGuid);
        Assert.Equal(2, _mesh.Seen(clients[0].Peer).Count(e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned));
        Assert.Equal(0, clients[0].Balancer.GetMyTeam(1));
        Assert.Equal(host.Peer.RakNetGuid, host.Balancer.HostGuid);
    }

    [Fact]
    public void An_assignment_from_a_system_that_is_not_host_is_dropped()
    {
        var (host, clients) = FormGroup(limits: [2, 2]);
        clients[0].Balancer.RequestAnyTeam(1);
        RunUntilTeam(clients[0].Balancer, 1, 0);

        byte[] forged = [(byte)MessageId.TeamBalancerTeamAssigned, 1, .. Id(1)];
        clients[1].Peer.Send(forged, clients[0].Peer.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Equal(0, clients[0].Balancer.GetMyTeam(1));
        Assert.DoesNotContain(_mesh.Seen(clients[0].Peer), e => e.Id == (byte)MessageId.TeamBalancerTeamAssigned && e.RakNetGuid == clients[1].Peer.RakNetGuid);
        Assert.Equal(host.Peer.RakNetGuid, clients[0].Balancer.HostGuid);
    }

    // The oldest system is host by FullyConnectedMesh2; the others connect to it. Limits are set on every system, as
    // the header advises in case one becomes host.
    private ((RakPeer Peer, Plugins.TeamBalancer Balancer) Host, List<(RakPeer Peer, Plugins.TeamBalancer Balancer)> Clients) FormGroup(ushort[] limits)
    {
        var host = AddSystem();
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        List<(RakPeer, Plugins.TeamBalancer)> clients = [AddSystem(), AddSystem()];
        foreach (var (peer, balancer) in _systems)
        {
            for (var i = 0; i < limits.Length; i++)
                balancer.SetTeamSizeLimit((byte)i, limits[i]);
        }
        foreach (var (peer, _) in clients)
            _mesh.Connect(peer, host.Peer);
        _mesh.RunUntil(() => _systems.All(s => s.Balancer.HostGuid == host.Peer.RakNetGuid), "every system knows the host");
        return (host, clients);
    }

    private (RakPeer Peer, Plugins.TeamBalancer Balancer) AddSystem()
    {
        var balancer = new Plugins.TeamBalancer();
        var peer = _mesh.Add(4, new Plugins.FullyConnectedMesh2(), balancer);
        _systems.Add((peer, balancer));
        return (peer, balancer);
    }

    private void RunUntilTeam(Plugins.TeamBalancer balancer, ulong memberId, byte team) =>
        _mesh.RunUntil(() => balancer.GetMyTeam(memberId) == team, $"member {memberId} on team {team}");

    private static byte[] Id(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }
}
