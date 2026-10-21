using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The tm* and tb* commands on a <see cref="TeamManager"/> and <see cref="TeamBalancer"/>, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : ITeamDriver
{
    private TeamManager? _teamManager;
    private TeamBalancer? _teamBalancer;

    public async Task AttachTeamManagerAsync(TeamManagerTopology topology = TeamManagerTopology.PeerToPeer, CancellationToken cancellationToken = default)
    {
        var peer = Started("tmAttach");
        if (_teamManager is not null)
            return;
        _teamManager = new TeamManager { Topology = topology };
        await AttachAsync(peer, _teamManager, cancellationToken).ConfigureAwait(false);
    }

    public Task SetTeamAutoManageConnectionsAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireTeamManager().AutoManageConnections = enabled;
        return Task.CompletedTask;
    }

    public Task AddTeamWorldAsync(byte worldId, CancellationToken cancellationToken = default)
    {
        var manager = RequireTeamManager();
        if (manager.GetWorldWithId(worldId) is not null)
            throw new PeerCommandException("tmAddWorld", "world is already in use");
        manager.AddWorld(worldId);
        return Task.CompletedTask;
    }

    public Task SetTeamHostAsync(byte worldId, ulong host, CancellationToken cancellationToken = default)
    {
        RequireWorld(worldId).Host = new RakNetGuid(host);
        return Task.CompletedTask;
    }

    public Task AddTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default)
    {
        RequireWorld(worldId).AddParticipant(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task RemoveTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default)
    {
        RequireWorld(worldId).RemoveParticipant(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task ReferenceTeamAsync(byte worldId, ulong team, bool applyBalancing, CancellationToken cancellationToken = default)
    {
        RequireWorld(worldId).ReferenceTeam(new Team(), team, applyBalancing);
        return Task.CompletedTask;
    }

    public Task ReferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default)
    {
        RequireWorld(worldId).ReferenceTeamMember(new TeamMember(), member);
        return Task.CompletedTask;
    }

    public Task DereferenceTeamAsync(byte worldId, ulong team, byte noTeamId, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        world.DereferenceTeam(RequireTeam(world, team, "team"), noTeamId);
        return Task.CompletedTask;
    }

    public Task DereferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        world.DereferenceTeamMember(RequireMember(world, member));
        return Task.CompletedTask;
    }

    public Task<bool> RequestTeamAsync(byte worldId, ulong member, JoinTeamType selection, ulong team = 0, byte noTeamId = 0, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        var teamMember = RequireMember(world, member);
        var request = selection switch
        {
            JoinTeamType.AnyAvailableTeam => TeamSelection.AnyAvailable(),
            JoinTeamType.SpecificTeam => TeamSelection.Specific(RequireTeam(world, team, "team")),
            _ => TeamSelection.NoTeam(noTeamId),
        };
        return Task.FromResult(teamMember.RequestTeam(request));
    }

    public Task<bool> RequestTeamSwitchAsync(byte worldId, ulong member, ulong teamToJoin, ulong? teamToLeave, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        var teamMember = RequireMember(world, member);
        var leave = teamToLeave is { } id ? RequireTeam(world, id, "teamToLeave") : null;
        return Task.FromResult(teamMember.RequestTeamSwitch(RequireTeam(world, teamToJoin, "teamToJoin"), leave));
    }

    public Task<bool> CancelTeamRequestAsync(byte worldId, ulong member, ulong? team, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        var teamMember = RequireMember(world, member);
        return Task.FromResult(teamMember.CancelTeamRequest(team is { } id ? RequireTeam(world, id, "team") : null));
    }

    public Task<bool> LeaveTeamAsync(byte worldId, ulong member, ulong team, byte noTeamId, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        var teamMember = RequireMember(world, member);
        return Task.FromResult(teamMember.LeaveTeam(RequireTeam(world, team, "team"), noTeamId));
    }

    public Task<bool> LeaveAllTeamsAsync(byte worldId, ulong member, byte noTeamId, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        return Task.FromResult(RequireMember(world, member).LeaveAllTeams(noTeamId));
    }

    public Task<bool> SetTeamMemberLimitAsync(byte worldId, ulong team, ushort limit, byte noTeamId, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        return Task.FromResult(RequireTeam(world, team, "team").SetMemberLimit(limit, noTeamId));
    }

    public Task<bool> SetTeamJoinPermissionsAsync(byte worldId, ulong team, JoinPermissions permissions, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        return Task.FromResult(RequireTeam(world, team, "team").SetJoinPermissions(permissions));
    }

    public Task<bool> SetBalanceTeamsAsync(byte worldId, bool enabled, byte noTeamId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireWorld(worldId).SetBalanceTeams(enabled, noTeamId));

    public Task<byte[]> SerializeTeamAsync(byte worldId, ulong team, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        using var bits = new BitStream();
        RequireTeam(world, team, "team").SerializeConstruction(bits);
        return Task.FromResult(bits.Written.ToArray());
    }

    public Task<byte[]> SerializeTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        using var bits = new BitStream();
        RequireMember(world, member).SerializeConstruction(bits);
        return Task.FromResult(bits.Written.ToArray());
    }

    public Task<bool> DeserializeTeamAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var manager = RequireTeamManager();
        var (world, networkId) = PeekConstruction(manager, data, "tmDeserializeTeam");
        // Into the team already referenced with that NetworkID, as the sample's static teams; otherwise a new one.
        var team = world.GetTeamByNetworkId(networkId) ?? new Team();
        var reader = new BitStreamReader(data);
        return Task.FromResult(team.DeserializeConstruction(manager, ref reader));
    }

    public Task<bool> DeserializeTeamMemberAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        var manager = RequireTeamManager();
        PeekConstruction(manager, data, "tmDeserializeTeamMember");
        var reader = new BitStreamReader(data);
        return Task.FromResult(new TeamMember().DeserializeConstruction(manager, ref reader));
    }

    public Task<TeamWorldInfo> GetTeamWorldInfoAsync(byte worldId, CancellationToken cancellationToken = default)
    {
        var world = RequireWorld(worldId);
        var teams = Enumerable.Range(0, world.TeamCount).Select(world.GetTeamByIndex).Select(t => new TeamInfo(
            t.NetworkId, [.. t.GetTeamMembers().Select(m => m.NetworkId)], t.MemberLimit, t.MemberLimitSetting, t.JoinPermissions, t.BalancingApplies));
        var members = Enumerable.Range(0, world.TeamMemberCount).Select(world.GetTeamMemberByIndex).Select(m => new TeamMemberInfo(
            m.NetworkId,
            [.. m.GetCurrentTeams().Select(t => t.NetworkId)],
            [.. m.GetLastTeams().Select(t => t.NetworkId)],
            m.NoTeamId,
            [.. m.GetRequestedSpecificTeams().Select(t => t.NetworkId)],
            m.RequestedTeam.JoinTeamType));
        return Task.FromResult(new TeamWorldInfo(world.Host.Value, world.BalanceTeams, [.. world.GetParticipantList().Select(g => g.Value)], [.. teams], [.. members]));
    }

    public async Task AttachTeamBalancerAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("tbAttach");
        if (_teamBalancer is not null)
            return;
        _teamBalancer = new TeamBalancer();
        await AttachAsync(peer, _teamBalancer, cancellationToken).ConfigureAwait(false);
    }

    public Task SetTeamSizeLimitAsync(byte team, ushort limit, CancellationToken cancellationToken = default)
    {
        if (team == TeamBalancer.UnassignedTeamId)
            throw new PeerCommandException("tbSetTeamSizeLimit", "field 'team' is out of range");
        RequireTeamBalancer().SetTeamSizeLimit(team, limit);
        return Task.CompletedTask;
    }

    public Task SetDefaultAssignmentAlgorithmAsync(DefaultAssignmentAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().DefaultAssignmentAlgorithm = algorithm;
        return Task.CompletedTask;
    }

    public Task SetForceEvenTeamsAsync(bool force, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().SetForceEvenTeams(force);
        return Task.CompletedTask;
    }

    public Task SetLockTeamsAsync(bool lockTeams, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().SetLockTeams(lockTeams);
        return Task.CompletedTask;
    }

    public Task RequestSpecificTeamAsync(ulong member, byte team, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().RequestSpecificTeam(member, team);
        return Task.CompletedTask;
    }

    public Task CancelRequestSpecificTeamAsync(ulong member, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().CancelRequestSpecificTeam(member);
        return Task.CompletedTask;
    }

    public Task RequestAnyTeamAsync(ulong member, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().RequestAnyTeam(member);
        return Task.CompletedTask;
    }

    public Task<byte> GetMyTeamAsync(ulong member, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireTeamBalancer().GetMyTeam(member));

    public Task DeleteBalancedMemberAsync(ulong member, CancellationToken cancellationToken = default)
    {
        RequireTeamBalancer().DeleteMember(member);
        return Task.CompletedTask;
    }

    // The world ID and NetworkID a construction starts with, as interop-peer's PeekConstruction.
    private static (TeamWorld World, ulong NetworkId) PeekConstruction(TeamManager manager, byte[] data, string command)
    {
        var reader = new BitStreamReader(data);
        if (!reader.TryReadByte(out var worldId) || !reader.TryReadUInt64(out var networkId))
            throw new PeerCommandException(command, "field 'data' is too short for a construction");
        var world = manager.GetWorldWithId(worldId) ?? throw new PeerCommandException(command, "no world with the construction's world ID");
        return (world, networkId);
    }

    // interop-peer's RequireTeamManager, RequireTeamBalancer, RequireWorld, RequireTeam and RequireMember.
    private TeamManager RequireTeamManager() =>
        _teamManager ?? throw new PeerCommandException("tm", "no TeamManager is attached; send tmAttach first");

    private TeamBalancer RequireTeamBalancer() =>
        _teamBalancer ?? throw new PeerCommandException("tb", "no TeamBalancer is attached; send tbAttach first");

    private TeamWorld RequireWorld(byte worldId) =>
        RequireTeamManager().GetWorldWithId(worldId) ?? throw new PeerCommandException("tm", "no such world; send tmAddWorld first");

    private static Team RequireTeam(TeamWorld world, ulong team, string field) =>
        world.GetTeamByNetworkId(team) ?? throw new PeerCommandException("tm", $"no team with the NetworkID in '{field}'");

    private static TeamMember RequireMember(TeamWorld world, ulong member) =>
        world.GetTeamMemberByNetworkId(member) ?? throw new PeerCommandException("tm", "no team member with the NetworkID in 'member'");
}
