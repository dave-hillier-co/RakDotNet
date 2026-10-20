using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// A member of zero or more teams: TM_TeamMember. Make one per player, register it with
/// <see cref="TeamWorld.ReferenceTeamMember"/> on the system that owns it, and replicate it to the others with
/// <see cref="SerializeConstruction"/> and <see cref="DeserializeConstruction"/>. Requests go to the world's host, which
/// resolves them and tells every participant.
/// </summary>
/// <remarks>
/// Ported from TeamManager.cpp:92-809. A member can be on several teams at once and can wait for several. Every member is
/// told of with ID_TEAM_BALANCER_TEAM_ASSIGNED when its teams change, a failed request with
/// ID_TEAM_BALANCER_REQUESTED_TEAM_FULL or ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED, and a cancelled one with
/// ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED. As in RakNet, <see cref="LeaveTeam"/> tells the other systems but pushes no
/// ID_TEAM_BALANCER_TEAM_ASSIGNED here. RakNet's destructor dereferences the member; here call
/// <see cref="TeamWorld.DereferenceTeamMember"/>. Members may be called from any thread.
/// </remarks>
public sealed class TeamMember
{
    private readonly Lock _ownGate = new();
    private List<Team> _lastTeams = [];

    /// <summary>GetNetworkID: the NetworkID given to <see cref="TeamWorld.ReferenceTeamMember"/>; 0 before.</summary>
    public ulong NetworkId
    {
        get
        {
            lock (Gate)
                return Id;
        }
    }

    /// <summary>GetTM_World: the world the member was referenced in, or null.</summary>
    public TeamWorld? World
    {
        get
        {
            lock (Gate)
                return WorldRef;
        }
    }

    /// <summary>SetOwner / GetOwner: anything the application wants to keep with the member, such as its player object.</summary>
    public object? Owner { get; set; }

    /// <summary>GetNoTeamId: which kind of no team the member is on when it is on none; 0 by default.</summary>
    public byte NoTeamId
    {
        get
        {
            lock (Gate)
                return NoTeamSubcategory;
        }
    }

    /// <summary>GetCurrentTeam: the first team the member is on, or null.</summary>
    public Team? CurrentTeam
    {
        get
        {
            lock (Gate)
                return Teams.Count > 0 ? Teams[0] : null;
        }
    }

    /// <summary>GetCurrentTeamCount</summary>
    public int CurrentTeamCount
    {
        get
        {
            lock (Gate)
                return Teams.Count;
        }
    }

    /// <summary>GetRequestedTeamCount: how many specific teams the member is waiting for.</summary>
    public int RequestedTeamCount
    {
        get
        {
            lock (Gate)
                return Requests.Count;
        }
    }

    /// <summary>
    /// GetRequestedTeam: the first team waited for, otherwise <see cref="TeamSelection.NoTeam"/> with
    /// <see cref="NoTeamId"/> or <see cref="TeamSelection.AnyAvailable"/>.
    /// </summary>
    public TeamSelection RequestedTeam
    {
        get
        {
            lock (Gate)
            {
                if (Requests.Count > 0)
                    return new TeamSelection(JoinTeamType.SpecificTeam, Requests[0].Requested, 0);
                return JoinType == JoinTeamType.NoTeam ? TeamSelection.NoTeam(NoTeamSubcategory) : TeamSelection.AnyAvailable();
            }
        }
    }

    /// <summary>GetWorldIndex: the member's index in its world, or -1.</summary>
    public int WorldIndex
    {
        get
        {
            lock (Gate)
                return RequireWorld().GetTeamMemberIndex(this);
        }
    }

    internal ulong Id { get; set; }

    internal TeamWorld? WorldRef { get; set; }

    internal List<Team> Teams { get; } = [];

    internal List<RequestedTeam> Requests { get; } = [];

    internal byte NoTeamSubcategory { get; set; }

    internal JoinTeamType JoinType { get; set; } = JoinTeamType.NoTeam;

    internal ulong WhenJoinAnyRequested { get; private set; }

    internal uint JoinAnyRequestIndex { get; private set; }

    private Lock Gate => WorldRef?.Manager.Gate ?? _ownGate;

    /// <summary>
    /// RequestTeam: asks to join any team, a specific team, or none. Any or a specific team is asked of the host, which
    /// answers every system with ID_TEAM_BALANCER_TEAM_ASSIGNED, or with ID_TEAM_BALANCER_REQUESTED_TEAM_FULL or _LOCKED
    /// and keeps the request until it can be met. No team is applied here at once and told to every participant. False
    /// when the request changes nothing: already on no team of that kind, on a team or waiting for any when asking for
    /// any, or on or waiting for the specific team.
    /// </summary>
    public bool RequestTeam(TeamSelection teamSelection)
    {
        lock (Gate)
        {
            var world = RequireWorld();
            var manager = world.Manager;
            switch (teamSelection.JoinTeamType)
            {
                case JoinTeamType.NoTeam:
                    {
                        if (Teams.Count == 0 && NoTeamSubcategory == teamSelection.NoTeamSubcategory)
                            return false;
                        using (var message = TeamManager.Internal(TeamManager.Operation.UpdateListsToNoTeam, world.WorldId))
                        {
                            message.WriteUInt64(Id);
                            message.WriteByte(teamSelection.NoTeamSubcategory);
                            world.BroadcastToParticipants(message, RakNetGuid.Unassigned);
                        }
                        StoreLastTeams();
                        UpdateListsToNoTeam(teamSelection.NoTeamSubcategory);
                        manager.PushTeamAssigned(this);
                        if (world.IsHostHere)
                        {
                            world.FillRequestedSlots();
                            world.EnforceTeamBalance(teamSelection.NoTeamSubcategory);
                        }
                        return true;
                    }
                case JoinTeamType.AnyAvailableTeam:
                    {
                        if (!JoinAnyTeamCheck())
                            return false;
                        UpdateTeamsRequestedToAny();
                        using var message = TeamManager.Internal(TeamManager.Operation.JoinAnyTeam, world.WorldId);
                        message.WriteUInt64(Id);
                        manager.SendTo(message, world.Host);
                        return true;
                    }
                default:
                    {
                        var team = teamSelection.SpecificTeam
                                   ?? throw new ArgumentException("A specific team request needs a team.", nameof(teamSelection));
                        if (!JoinSpecificTeamCheck(team, ignoreRequested: false))
                            return false;
                        AddToRequestedTeams(team);
                        using var message = TeamManager.Internal(TeamManager.Operation.JoinRequestedTeam, world.WorldId);
                        message.WriteUInt64(Id);
                        message.WriteUInt64(team.Id);
                        message.WriteBool(false);
                        manager.SendTo(message, world.Host);
                        return true;
                    }
            }
        }
    }

    /// <summary>
    /// RequestTeamSwitch: like asking for <paramref name="teamToJoin"/>, but leaving <paramref name="teamToLeave"/>, or
    /// every team when null, once it can be joined; while the world balances teams, two members wanting each other's team
    /// swap. False when already on <paramref name="teamToJoin"/>, not on <paramref name="teamToLeave"/>, the two are the
    /// same, or the same switch is already waiting.
    /// </summary>
    public bool RequestTeamSwitch(Team teamToJoin, Team? teamToLeave)
    {
        ArgumentNullException.ThrowIfNull(teamToJoin);
        lock (Gate)
        {
            var world = RequireWorld();
            if (!SwitchSpecificTeamCheck(teamToJoin, teamToLeave, ignoreRequested: false))
                return false;
            AddToRequestedTeams(teamToJoin, teamToLeave);
            using var message = TeamManager.Internal(TeamManager.Operation.JoinRequestedTeam, world.WorldId);
            message.WriteUInt64(Id);
            message.WriteUInt64(teamToJoin.Id);
            message.WriteBool(true);
            WriteOptionalTeam(message, teamToLeave);
            world.Manager.SendTo(message, world.Host);
            return true;
        }
    }

    /// <summary>GetRequestedSpecificTeams: the teams waited for, oldest request first.</summary>
    public IReadOnlyList<Team> GetRequestedSpecificTeams()
    {
        lock (Gate)
            return [.. Requests.Select(r => r.Requested)];
    }

    /// <summary>HasRequestedTeam</summary>
    public bool HasRequestedTeam(Team team)
    {
        lock (Gate)
            return GetRequestedTeamIndexInternal(team) >= 0;
    }

    /// <summary>GetRequestedTeamIndex: the index of <paramref name="team"/> among the teams waited for, or -1.</summary>
    public int GetRequestedTeamIndex(Team team)
    {
        lock (Gate)
            return GetRequestedTeamIndexInternal(team);
    }

    /// <summary>
    /// CancelTeamRequest: stops waiting for <paramref name="specificTeamToCancel"/>, or for every team when null, and tells
    /// every participant and this system with ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED. A request the host is meeting at
    /// the same time may still be met. False when the team was not waited for.
    /// </summary>
    public bool CancelTeamRequest(Team? specificTeamToCancel)
    {
        lock (Gate)
        {
            var world = RequireWorld();
            if (!RemoveFromRequestedTeams(specificTeamToCancel))
                return false;
            using var message = new BitStream(32);
            message.WriteByte((byte)MessageId.TeamBalancerTeamRequestedCancelled);
            message.WriteByte(world.WorldId);
            message.WriteUInt64(Id);
            WriteOptionalTeam(message, specificTeamToCancel);
            world.BroadcastToParticipants(message, RakNetGuid.Unassigned);
            world.Manager.PushBitStream(message);
            return true;
        }
    }

    /// <summary>
    /// LeaveTeam: leaves <paramref name="team"/>, taking <paramref name="noTeamSubcategory"/> when it was the last team,
    /// and tells every participant; the host then lets waiting members join and rebalances. False when not on the team.
    /// </summary>
    public bool LeaveTeam(Team team, byte noTeamSubcategory)
    {
        ArgumentNullException.ThrowIfNull(team);
        lock (Gate)
        {
            var world = RequireWorld();
            if (!IsOnTeamInternal(team))
                return false;
            RemoveFromSpecificTeamInternal(team);
            if (Teams.Count == 0)
            {
                NoTeamSubcategory = noTeamSubcategory;
                JoinType = JoinTeamType.NoTeam;
            }
            // TeamManager.cpp:350-364: the member's own no-team subcategory is sent, and used to rebalance.
            using (var message = TeamManager.Internal(TeamManager.Operation.LeaveTeam, world.WorldId))
            {
                message.WriteUInt64(Id);
                message.WriteUInt64(team.Id);
                message.WriteByte(NoTeamSubcategory);
                world.BroadcastToParticipants(message, RakNetGuid.Unassigned);
            }
            if (world.IsHostHere)
            {
                world.FillRequestedSlots();
                world.EnforceTeamBalance(NoTeamSubcategory);
            }
            return true;
        }
    }

    /// <summary>LeaveAllTeams: <see cref="RequestTeam"/> with <see cref="TeamSelection.NoTeam"/>.</summary>
    public bool LeaveAllTeams(byte noTeamSubcategory) => RequestTeam(TeamSelection.NoTeam(noTeamSubcategory));

    /// <summary>GetCurrentTeamByIndex</summary>
    public Team GetCurrentTeamByIndex(int index)
    {
        lock (Gate)
            return Teams[index];
    }

    /// <summary>GetCurrentTeams, in the order they were joined.</summary>
    public IReadOnlyList<Team> GetCurrentTeams()
    {
        lock (Gate)
            return [.. Teams];
    }

    /// <summary>
    /// GetLastTeams: the teams the member was on before its teams last changed, saved as ID_TEAM_BALANCER_TEAM_ASSIGNED is
    /// produced and again as it is read.
    /// </summary>
    public IReadOnlyList<Team> GetLastTeams()
    {
        lock (Gate)
            return [.. _lastTeams];
    }

    /// <summary>IsOnTeam</summary>
    public bool IsOnTeam(Team team)
    {
        lock (Gate)
            return IsOnTeamInternal(team);
    }

    /// <summary>
    /// SerializeConstruction: the member's state for a new system: world ID, NetworkID, the requests (a 16-bit count, then
    /// for each a switch bit and the optional teams to leave and join, each a bit and a NetworkID), then the
    /// ID_TEAM_BALANCER_TEAM_ASSIGNED body. Serialize the teams first; <see cref="DeserializeConstruction"/> needs them.
    /// </summary>
    public void SerializeConstruction(BitStream constructionBitstream)
    {
        ArgumentNullException.ThrowIfNull(constructionBitstream);
        lock (Gate)
        {
            var world = RequireWorld();
            constructionBitstream.WriteByte(world.WorldId);
            constructionBitstream.WriteUInt64(Id);
            constructionBitstream.WriteUInt16(unchecked((ushort)Requests.Count));
            foreach (var request in Requests)
            {
                constructionBitstream.WriteBool(request.IsTeamSwitch);
                WriteOptionalTeam(constructionBitstream, request.TeamToLeave);
                WriteOptionalTeam(constructionBitstream, request.Requested);
            }
            TeamManager.EncodeTeamAssigned(constructionBitstream, this);
        }
    }

    /// <summary>
    /// DeserializeConstruction: references this member in its world of <paramref name="teamManager"/> under the NetworkID
    /// read, then reads its requests and teams. A request naming a team this system does not know is dropped. False when
    /// the world is unknown or the data is truncated.
    /// </summary>
    public bool DeserializeConstruction(TeamManager teamManager, ref BitStreamReader constructionBitstream)
    {
        ArgumentNullException.ThrowIfNull(teamManager);
        lock (teamManager.Gate)
        {
            if (!constructionBitstream.TryReadByte(out var worldId) || teamManager.GetWorldWithId(worldId) is not { } world)
                return false;
            if (!constructionBitstream.TryReadUInt64(out var id))
                return false;
            world.ReferenceTeamMember(this, id);
            if (!constructionBitstream.TryReadUInt16(out var count))
                return false;
            for (var i = 0; i < count; i++)
            {
                if (!constructionBitstream.TryReadBool(out var isTeamSwitch)
                    || !TryReadOptionalTeam(ref constructionBitstream, world, out var hasTeamToLeave, out var teamToLeave)
                    || !TryReadOptionalTeam(ref constructionBitstream, world, out var hasRequested, out var requested))
                    return false;
                // TeamManager.cpp:517-525: a fresh time and index, taken whether or not the request is kept; kept only when
                // every team named is known here. One naming no team to join is dropped too, where RakNet keeps it and
                // FillRequestedSlots then dereferences its null team (TeamManager.cpp:1495-1496).
                var whenRequested = world.Manager.Now();
                var requestIndex = world.NextRequestIndex();
                if ((!hasTeamToLeave || teamToLeave is not null) && hasRequested && requested is not null)
                    Requests.Add(new RequestedTeam(whenRequested, requestIndex, requested, isTeamSwitch, teamToLeave));
            }
            return teamManager.ProcessTeamAssigned(ref constructionBitstream);
        }
    }

    internal static void WriteOptionalTeam(BitStream message, Team? team)
    {
        message.WriteBool(team is not null);
        if (team is not null)
            message.WriteUInt64(team.Id);
    }

    private static bool TryReadOptionalTeam(ref BitStreamReader reader, TeamWorld world, out bool present, out Team? team)
    {
        team = null;
        if (!reader.TryReadBool(out present))
            return false;
        if (!present)
            return true;
        if (!reader.TryReadUInt64(out var id))
            return false;
        team = world.FindTeam(id);
        return true;
    }

    internal bool IsOnTeamInternal(Team team) => Teams.Contains(team);

    internal int GetRequestedTeamIndexInternal(Team team) => Requests.FindIndex(r => r.Requested == team);

    // UpdateListsToNoTeam (TeamManager.cpp:571-581).
    internal void UpdateListsToNoTeam(byte noTeamSubcategory)
    {
        Requests.Clear();
        foreach (var team in Teams)
            team.RemoveFromTeamMemberList(this);
        Teams.Clear();
        NoTeamSubcategory = noTeamSubcategory;
        JoinType = JoinTeamType.NoTeam;
    }

    // JoinAnyTeamCheck (TeamManager.cpp:585-596).
    internal bool JoinAnyTeamCheck() => Teams.Count == 0 && !(Requests.Count == 0 && JoinType == JoinTeamType.AnyAvailableTeam);

    // JoinSpecificTeamCheck (TeamManager.cpp:600-624): asking for a team waited for as a switch turns the switch off.
    internal bool JoinSpecificTeamCheck(Team team, bool ignoreRequested)
    {
        if (IsOnTeamInternal(team))
            return false;
        if (ignoreRequested)
            return true;
        var index = GetRequestedTeamIndexInternal(team);
        return index < 0 || Requests[index].IsTeamSwitch;
    }

    // SwitchSpecificTeamCheck (TeamManager.cpp:628-663).
    internal bool SwitchSpecificTeamCheck(Team teamToJoin, Team? teamToLeave, bool ignoreRequested)
    {
        if (IsOnTeamInternal(teamToJoin))
            return false;
        if (teamToLeave is not null && !IsOnTeamInternal(teamToLeave))
            return false;
        if (teamToJoin == teamToLeave)
            return false;
        if (ignoreRequested)
            return true;
        var index = GetRequestedTeamIndexInternal(teamToJoin);
        if (index < 0 || !Requests[index].IsTeamSwitch)
            return true;
        return Requests[index].TeamToLeave != teamToLeave;
    }

    // UpdateTeamsRequestedToAny (TeamManager.cpp:676-682).
    internal void UpdateTeamsRequestedToAny()
    {
        var world = RequireWorld();
        Requests.Clear();
        JoinType = JoinTeamType.AnyAvailableTeam;
        WhenJoinAnyRequested = world.Manager.Now();
        JoinAnyRequestIndex = world.NextRequestIndex();
    }

    // UpdateTeamsRequestedToNone (TeamManager.cpp:686-690).
    internal void UpdateTeamsRequestedToNone()
    {
        Requests.Clear();
        JoinType = JoinTeamType.NoTeam;
    }

    // AddToRequestedTeams (TeamManager.cpp:694-720): a request replaces any earlier one for the same team.
    internal void AddToRequestedTeams(Team teamToJoin)
    {
        RemoveFromRequestedTeams(teamToJoin);
        var world = RequireWorld();
        Requests.Add(new RequestedTeam(world.Manager.Now(), world.NextRequestIndex(), teamToJoin, false, null));
    }

    internal void AddToRequestedTeams(Team teamToJoin, Team? teamToLeave)
    {
        RemoveFromRequestedTeams(teamToJoin);
        var world = RequireWorld();
        Requests.Add(new RequestedTeam(world.Manager.Now(), world.NextRequestIndex(), teamToJoin, true, teamToLeave));
    }

    // RemoveFromRequestedTeams (TeamManager.cpp:724-749): null clears every request.
    internal bool RemoveFromRequestedTeams(Team? team)
    {
        if (team is null)
        {
            Requests.Clear();
            JoinType = JoinTeamType.NoTeam;
            return true;
        }
        var index = GetRequestedTeamIndexInternal(team);
        if (index < 0)
            return false;
        Requests.RemoveAt(index);
        if (Requests.Count == 0)
            JoinType = JoinTeamType.NoTeam;
        return true;
    }

    // AddToTeamList (TeamManager.cpp:753-757).
    internal void AddToTeamList(Team team)
    {
        team.Members.Add(this);
        Teams.Add(team);
    }

    // RemoveFromSpecificTeamInternal (TeamManager.cpp:761-780).
    internal void RemoveFromSpecificTeamInternal(Team team)
    {
        if (Teams.Remove(team))
            team.Members.Remove(this);
    }

    // RemoveFromAllTeamsInternal (TeamManager.cpp:784-802).
    internal void RemoveFromAllTeamsInternal()
    {
        foreach (var team in Teams)
            team.Members.Remove(this);
        Teams.Clear();
    }

    // StoreLastTeams (TeamManager.cpp:806-809).
    internal void StoreLastTeams() => _lastTeams = [.. Teams];

    private TeamWorld RequireWorld() =>
        WorldRef ?? throw new InvalidOperationException("The team member is not referenced in a world; call TeamWorld.ReferenceTeamMember first.");
}

// TM_TeamMember::RequestedTeam: when and in which order the request was made, for FillRequestedSlots.
internal sealed class RequestedTeam(ulong whenRequested, uint requestIndex, Team requested, bool isTeamSwitch, Team? teamToLeave)
{
    public ulong WhenRequested { get; } = whenRequested;

    public uint RequestIndex { get; } = requestIndex;

    public Team Requested { get; } = requested;

    public bool IsTeamSwitch { get; } = isTeamSwitch;

    public Team? TeamToLeave { get; } = teamToLeave;
}
