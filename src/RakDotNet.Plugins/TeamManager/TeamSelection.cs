namespace RakDotNet.Plugins;

/// <summary>What a team member asks for: JoinTeamType. The values are RakNet's, which go on the wire as 32 bits.</summary>
public enum JoinTeamType
{
    /// <summary>JOIN_ANY_AVAILABLE_TEAM: the first team with room that allows <see cref="JoinPermissions.AnyAvailableTeam"/>.</summary>
    AnyAvailableTeam = 0,

    /// <summary>JOIN_SPECIFIC_TEAM: one team, which must allow <see cref="JoinPermissions.SpecificTeam"/>.</summary>
    SpecificTeam = 1,

    /// <summary>JOIN_NO_TEAM: leave every team. Always succeeds.</summary>
    NoTeam = 2,
}

/// <summary>Who may join a team while it has room: RakNet's ALLOW_JOIN_* bits, a JoinPermissions byte on the wire.</summary>
[Flags]
public enum JoinPermissions : byte
{
    /// <summary>Nobody may join.</summary>
    None = 0,

    /// <summary>ALLOW_JOIN_ANY_AVAILABLE_TEAM: members asking for any team.</summary>
    AnyAvailableTeam = 1 << 0,

    /// <summary>ALLOW_JOIN_SPECIFIC_TEAM: members asking for this team.</summary>
    SpecificTeam = 1 << 1,

    /// <summary>ALLOW_JOIN_REBALANCING: members the host moves here to balance or trim teams.</summary>
    Rebalancing = 1 << 2,

    /// <summary>Every permission, a team's default.</summary>
    All = AnyAvailableTeam | SpecificTeam | Rebalancing,
}

/// <summary>Who resolves and relays team operations: TMTopology.</summary>
public enum TeamManagerTopology
{
    /// <summary>TM_PEER_TO_PEER: every system sends to every participant, and the host relays nothing.</summary>
    PeerToPeer,

    /// <summary>TM_CLIENT_SERVER: the host relays what one participant sends to the others.</summary>
    ClientServer,
}

/// <summary>
/// The argument of <see cref="TeamMember.RequestTeam"/>: TeamSelection. Make one with <see cref="AnyAvailable"/>,
/// <see cref="Specific"/> or <see cref="NoTeam"/>.
/// </summary>
/// <param name="JoinTeamType">What is asked for.</param>
/// <param name="SpecificTeam">The team, for <see cref="Plugins.JoinTeamType.SpecificTeam"/>.</param>
/// <param name="NoTeamSubcategory">Which kind of no team, such as spectator, for <see cref="Plugins.JoinTeamType.NoTeam"/>.</param>
public readonly record struct TeamSelection(JoinTeamType JoinTeamType, Team? SpecificTeam, byte NoTeamSubcategory)
{
    /// <summary>TeamSelection::AnyAvailable</summary>
    public static TeamSelection AnyAvailable() => new(JoinTeamType.AnyAvailableTeam, null, 0);

    /// <summary>TeamSelection::SpecificTeam</summary>
    public static TeamSelection Specific(Team team)
    {
        ArgumentNullException.ThrowIfNull(team);
        return new(JoinTeamType.SpecificTeam, team, 0);
    }

    /// <summary>TeamSelection::NoTeam</summary>
    public static TeamSelection NoTeam(byte noTeamSubcategory) => new(JoinTeamType.NoTeam, null, noTeamSubcategory);
}

/// <summary>
/// What ID_TEAM_BALANCER_REQUESTED_TEAM_FULL and ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED carry, as
/// <see cref="TeamManager.TryDecomposeTeamFull"/> and <see cref="TeamManager.TryDecomposeTeamLocked"/> read it.
/// </summary>
/// <param name="WorldId">The world's ID.</param>
/// <param name="TeamMemberId">The NetworkID of the member whose request failed.</param>
/// <param name="TeamId">The NetworkID of the team it asked for.</param>
/// <param name="World">The world with <paramref name="WorldId"/>, or null.</param>
/// <param name="TeamMember">The member, or null when this system does not know it.</param>
/// <param name="Team">The team, or null when this system does not know it.</param>
/// <param name="CurrentMembers">The team's member count on the host.</param>
/// <param name="MemberLimitIncludingBalancing">The team's <see cref="Team.MemberLimit"/> on the host.</param>
/// <param name="BalancingIsActive">The team's <see cref="Team.BalancingApplies"/>, under RakNet's name for this field.</param>
/// <param name="JoinPermissions">The team's permissions on the host.</param>
public sealed record TeamRequestRefusal(
    byte WorldId,
    ulong TeamMemberId,
    ulong TeamId,
    TeamWorld? World,
    TeamMember? TeamMember,
    Team? Team,
    ushort CurrentMembers,
    ushort MemberLimitIncludingBalancing,
    bool BalancingIsActive,
    JoinPermissions JoinPermissions);
