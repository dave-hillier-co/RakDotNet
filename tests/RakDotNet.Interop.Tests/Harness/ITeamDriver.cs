using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives TeamManager and TeamBalancer on a peer: the tm* and tb* commands of docs/interop-protocol.md. Both
/// drivers implement it; every command of a plugin but its attach command fails until it is attached. Teams and members
/// are named by world ID and NetworkID.
/// </summary>
public interface ITeamDriver
{
    /// <summary>AttachPlugin of a TeamManager after SetTopology.</summary>
    Task AttachTeamManagerAsync(TeamManagerTopology topology = TeamManagerTopology.PeerToPeer, CancellationToken cancellationToken = default);

    /// <summary>TeamManager::SetAutoManageConnections, which reaches the worlds that exist.</summary>
    Task SetTeamAutoManageConnectionsAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>TeamManager::AddWorld</summary>
    Task AddTeamWorldAsync(byte worldId, CancellationToken cancellationToken = default);

    /// <summary>TM_World::SetHost</summary>
    Task SetTeamHostAsync(byte worldId, ulong host, CancellationToken cancellationToken = default);

    /// <summary>TM_World::AddParticipant</summary>
    Task AddTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default);

    /// <summary>TM_World::RemoveParticipant</summary>
    Task RemoveTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default);

    /// <summary>TM_World::ReferenceTeam of a new TM_Team.</summary>
    Task ReferenceTeamAsync(byte worldId, ulong team, bool applyBalancing, CancellationToken cancellationToken = default);

    /// <summary>TM_World::ReferenceTeamMember of a new TM_TeamMember.</summary>
    Task ReferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default);

    /// <summary>TM_World::DereferenceTeam</summary>
    Task DereferenceTeamAsync(byte worldId, ulong team, byte noTeamId, CancellationToken cancellationToken = default);

    /// <summary>TM_World::DereferenceTeamMember, as when a member's replica is destroyed.</summary>
    Task DereferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::RequestTeam; <paramref name="team"/> for a specific team, <paramref name="noTeamId"/> for none.</summary>
    Task<bool> RequestTeamAsync(byte worldId, ulong member, JoinTeamType selection, ulong team = 0, byte noTeamId = 0, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::RequestTeamSwitch; a null <paramref name="teamToLeave"/> leaves every team.</summary>
    Task<bool> RequestTeamSwitchAsync(byte worldId, ulong member, ulong teamToJoin, ulong? teamToLeave, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::CancelTeamRequest; null cancels every request.</summary>
    Task<bool> CancelTeamRequestAsync(byte worldId, ulong member, ulong? team, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::LeaveTeam</summary>
    Task<bool> LeaveTeamAsync(byte worldId, ulong member, ulong team, byte noTeamId, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::LeaveAllTeams</summary>
    Task<bool> LeaveAllTeamsAsync(byte worldId, ulong member, byte noTeamId, CancellationToken cancellationToken = default);

    /// <summary>TM_Team::SetMemberLimit</summary>
    Task<bool> SetTeamMemberLimitAsync(byte worldId, ulong team, ushort limit, byte noTeamId, CancellationToken cancellationToken = default);

    /// <summary>TM_Team::SetJoinPermissions</summary>
    Task<bool> SetTeamJoinPermissionsAsync(byte worldId, ulong team, JoinPermissions permissions, CancellationToken cancellationToken = default);

    /// <summary>TM_World::SetBalanceTeams</summary>
    Task<bool> SetBalanceTeamsAsync(byte worldId, bool enabled, byte noTeamId, CancellationToken cancellationToken = default);

    /// <summary>TM_Team::SerializeConstruction</summary>
    Task<byte[]> SerializeTeamAsync(byte worldId, ulong team, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::SerializeConstruction</summary>
    Task<byte[]> SerializeTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default);

    /// <summary>TM_Team::DeserializeConstruction into the team referenced with the construction's NetworkID, or a new one.</summary>
    Task<bool> DeserializeTeamAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>TM_TeamMember::DeserializeConstruction into a new member.</summary>
    Task<bool> DeserializeTeamMemberAsync(byte[] data, CancellationToken cancellationToken = default);

    /// <summary>A world's host, balancing, participants, teams and members.</summary>
    Task<TeamWorldInfo> GetTeamWorldInfoAsync(byte worldId, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a TeamBalancer.</summary>
    Task AttachTeamBalancerAsync(CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::SetTeamSizeLimit</summary>
    Task SetTeamSizeLimitAsync(byte team, ushort limit, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::SetDefaultAssignmentAlgorithm</summary>
    Task SetDefaultAssignmentAlgorithmAsync(DefaultAssignmentAlgorithm algorithm, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::SetForceEvenTeams</summary>
    Task SetForceEvenTeamsAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::SetLockTeams</summary>
    Task SetLockTeamsAsync(bool lockTeams, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::RequestSpecificTeam</summary>
    Task RequestSpecificTeamAsync(ulong member, byte team, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::CancelRequestSpecificTeam</summary>
    Task CancelRequestSpecificTeamAsync(ulong member, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::RequestAnyTeam</summary>
    Task RequestAnyTeamAsync(ulong member, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::GetMyTeam</summary>
    Task<byte> GetMyTeamAsync(ulong member, CancellationToken cancellationToken = default);

    /// <summary>TeamBalancer::DeleteMember</summary>
    Task DeleteBalancedMemberAsync(ulong member, CancellationToken cancellationToken = default);
}

/// <summary>GetHost, GetBalanceTeams, GetParticipantList, and every team and member in world order.</summary>
public sealed record TeamWorldInfo(ulong Host, bool BalanceTeams, IReadOnlyList<ulong> Participants, IReadOnlyList<TeamInfo> Teams, IReadOnlyList<TeamMemberInfo> Members)
{
    public TeamInfo Team(ulong team) => Teams.Single(t => t.Team == team);

    public TeamMemberInfo Member(ulong member) => Members.Single(m => m.Member == member);
}

/// <summary>A team's NetworkID, members in order, GetMemberLimit, GetMemberLimitSetting, GetJoinPermissions and GetBalancingApplies.</summary>
public sealed record TeamInfo(ulong Team, IReadOnlyList<ulong> Members, ushort MemberLimit, ushort MemberLimitSetting, JoinPermissions JoinPermissions, bool BalancingApplies);

/// <summary>
/// A member's NetworkID, GetCurrentTeams, GetLastTeams, GetNoTeamId, GetRequestedSpecificTeams and the type of
/// GetRequestedTeam.
/// </summary>
public sealed record TeamMemberInfo(ulong Member, IReadOnlyList<ulong> Teams, IReadOnlyList<ulong> LastTeams, byte NoTeamId, IReadOnlyList<ulong> RequestedTeams, JoinTeamType RequestedTeam);
