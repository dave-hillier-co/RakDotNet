using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// A team, holding the <see cref="TeamMember"/>s on it: TM_Team. Make one per team your game has, register it with
/// <see cref="TeamWorld.ReferenceTeam"/> on every system, and change it through its members and this class; the changes
/// are networked by the <see cref="TeamManager"/>.
/// </summary>
/// <remarks>
/// Ported from TeamManager.cpp:821-1056. A team can be in one world. RakNet's destructor dereferences the team; here
/// call <see cref="TeamWorld.DereferenceTeam"/>. Members may be called from any thread.
/// </remarks>
public sealed class Team
{
    private readonly List<TeamMember> _members = [];

    /// <summary>The NetworkID given to <see cref="TeamWorld.ReferenceTeam"/>; 0 before.</summary>
    public ulong NetworkId
    {
        get
        {
            lock (Gate)
                return Id;
        }
    }

    /// <summary>GetTM_World: the world the team is referenced in, or null.</summary>
    public TeamWorld? World
    {
        get
        {
            lock (Gate)
                return WorldRef;
        }
    }

    /// <summary>SetOwner / GetOwner: anything the application wants to keep with the team, such as its game object.</summary>
    public object? Owner { get; set; }

    /// <summary>GetMemberLimitSetting: what <see cref="SetMemberLimit"/> last set; 65535 by default.</summary>
    public ushort MemberLimitSetting
    {
        get
        {
            lock (Gate)
                return Limit;
        }
    }

    /// <summary>
    /// GetMemberLimit: <see cref="MemberLimitSetting"/>, or less while the world balances teams, the most members the
    /// team can have without being unbalanced.
    /// </summary>
    public ushort MemberLimit
    {
        get
        {
            lock (Gate)
                return GetMemberLimit();
        }
    }

    /// <summary>GetJoinPermissions: what <see cref="SetJoinPermissions"/> last set; <see cref="JoinPermissions.All"/> by default.</summary>
    public JoinPermissions JoinPermissions
    {
        get
        {
            lock (Gate)
                return Permissions;
        }
    }

    /// <summary>GetBalancingApplies: the applyBalancing given to <see cref="TeamWorld.ReferenceTeam"/>; true by default.</summary>
    public bool BalancingApplies
    {
        get
        {
            lock (Gate)
                return Balancing;
        }
    }

    /// <summary>GetTeamMembersCount</summary>
    public int TeamMembersCount
    {
        get
        {
            lock (Gate)
                return _members.Count;
        }
    }

    /// <summary>GetWorldIndex: the team's index in its world, or -1.</summary>
    public int WorldIndex
    {
        get
        {
            lock (Gate)
                return RequireWorld().GetTeamIndex(this);
        }
    }

    internal ulong Id { get; set; }

    internal TeamWorld? WorldRef { get; set; }

    internal ushort Limit { get; set; } = ushort.MaxValue;

    internal JoinPermissions Permissions { get; set; } = JoinPermissions.All;

    internal bool Balancing { get; set; } = true;

    internal List<TeamMember> Members => _members;

    private Lock Gate => WorldRef?.Manager.Gate ?? _ownGate;

    private readonly Lock _ownGate = new();

    /// <summary>GetTeamMembers, in the order they joined.</summary>
    public IReadOnlyList<TeamMember> GetTeamMembers()
    {
        lock (Gate)
            return [.. _members];
    }

    /// <summary>GetTeamMemberByIndex</summary>
    public TeamMember GetTeamMemberByIndex(int index)
    {
        lock (Gate)
            return _members[index];
    }

    /// <summary>
    /// SetMemberLimit: sets the limit here and asks the host to apply it, which removes the newest members while the team
    /// is over it, moving them to the emptiest team that allows rebalancing or, failing that, to no team with
    /// <paramref name="noTeamSubcategory"/>; a higher limit lets members waiting for the team join. False when the limit
    /// is unchanged.
    /// </summary>
    public bool SetMemberLimit(ushort limit, byte noTeamSubcategory)
    {
        lock (Gate)
        {
            if (Limit == limit)
                return false;
            Limit = limit;
            var world = RequireWorld();
            using var message = TeamManager.Internal(TeamManager.Operation.SetMemberLimit, world.WorldId);
            message.WriteUInt64(Id);
            message.WriteUInt16(limit);
            message.WriteByte(noTeamSubcategory);
            world.Manager.SendTo(message, world.Host);
            return true;
        }
    }

    /// <summary>
    /// SetJoinPermissions: sets who may join while there is room, here and on the host; the host then lets waiting
    /// members join. False when unchanged.
    /// </summary>
    public bool SetJoinPermissions(JoinPermissions permissions)
    {
        lock (Gate)
        {
            if (Permissions == permissions)
                return false;
            Permissions = permissions;
            var world = RequireWorld();
            using var message = TeamManager.Internal(TeamManager.Operation.SetJoinPermissions, world.WorldId);
            message.WriteUInt64(Id);
            message.WriteByte((byte)permissions);
            world.Manager.SendTo(message, world.Host);
            return true;
        }
    }

    /// <summary>LeaveTeam: the same as <paramref name="teamMember"/>.<see cref="TeamMember.LeaveTeam"/>(this, ...).</summary>
    public void LeaveTeam(TeamMember teamMember, byte noTeamSubcategory)
    {
        ArgumentNullException.ThrowIfNull(teamMember);
        teamMember.LeaveTeam(this, noTeamSubcategory);
    }

    /// <summary>
    /// SerializeConstruction: the team's state for a new system, which <see cref="DeserializeConstruction"/> reads: world
    /// ID, NetworkID, join permissions, a balancing bit and the member limit. Members are not written; each member writes
    /// its own teams.
    /// </summary>
    public void SerializeConstruction(BitStream constructionBitstream)
    {
        ArgumentNullException.ThrowIfNull(constructionBitstream);
        lock (Gate)
        {
            constructionBitstream.WriteByte(RequireWorld().WorldId);
            constructionBitstream.WriteUInt64(Id);
            constructionBitstream.WriteByte((byte)Permissions);
            constructionBitstream.WriteBool(Balancing);
            constructionBitstream.WriteUInt16(Limit);
        }
    }

    /// <summary>
    /// DeserializeConstruction: reads what <see cref="SerializeConstruction"/> wrote into this team, then references it in
    /// that world of <paramref name="teamManager"/>; a team already referenced only takes the values. False when the world
    /// is unknown or the data is truncated, leaving the team unchanged.
    /// </summary>
    public bool DeserializeConstruction(TeamManager teamManager, ref BitStreamReader constructionBitstream)
    {
        ArgumentNullException.ThrowIfNull(teamManager);
        lock (teamManager.Gate)
        {
            if (!constructionBitstream.TryReadByte(out var worldId) || teamManager.GetWorldWithId(worldId) is not { } world)
                return false;
            if (!constructionBitstream.TryReadUInt64(out var id) || !constructionBitstream.TryReadByte(out var permissions)
                || !constructionBitstream.TryReadBool(out var balancing) || !constructionBitstream.TryReadUInt16(out var limit))
                return false;
            // TeamManager.cpp:978-994: read straight into the fields, then ReferenceTeam.
            Id = id;
            Permissions = (JoinPermissions)permissions;
            Balancing = balancing;
            Limit = limit;
            world.ReferenceTeam(this, id, balancing);
            return true;
        }
    }

    // GetMemberLimit (TeamManager.cpp:862-875).
    internal ushort GetMemberLimit()
    {
        if (WorldRef is not { } world || !world.BalanceTeamsActive)
            return Limit;
        var balanced = world.GetBalancedTeamLimit();
        return balanced < Limit ? balanced : Limit;
    }

    // RemoveFromTeamMemberList (TeamManager.cpp:1028-1033).
    internal void RemoveFromTeamMemberList(TeamMember teamMember) => _members.Remove(teamMember);

    // GetMemberWithRequestedSingleTeamSwitch (TeamManager.cpp:1037-1056): a member on one team that wants to switch from
    // it to team.
    internal int GetMemberWithRequestedSingleTeamSwitch(Team team)
    {
        for (var i = 0; i < _members.Count; i++)
        {
            var member = _members[i];
            if (member.Teams.Count != 1)
                continue;
            var j = member.GetRequestedTeamIndexInternal(team);
            if (j < 0)
                continue;
            var request = member.Requests[j];
            if (request.IsTeamSwitch && (request.TeamToLeave is null || request.TeamToLeave == member.Teams[0]))
                return i;
        }
        return -1;
    }

    private TeamWorld RequireWorld() =>
        WorldRef ?? throw new InvalidOperationException("The team is not referenced in a world; call TeamWorld.ReferenceTeam first.");
}
