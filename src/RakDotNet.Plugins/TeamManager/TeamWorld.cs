using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// A set of teams and members, with the systems they are networked to and the host that resolves requests: TM_World,
/// made by <see cref="TeamManager.AddWorld"/>. Worlds are independent, so one process can hold several games.
/// </summary>
/// <remarks>
/// Ported from TeamManager.cpp:1063-1808. The host starts as this system; with FullyConnectedMesh2 it follows
/// ID_FCM2_NEW_HOST, otherwise set <see cref="Host"/> on every system. Members may be called from any thread.
/// </remarks>
public sealed class TeamWorld
{
    private readonly List<RakNetGuid> _participants = [];
    private readonly List<Team> _teams = [];
    private readonly List<TeamMember> _members = [];
    // DataStructures::Hash pushes at the head of a bucket and finds and removes the newest entry for a key.
    private readonly Dictionary<ulong, List<Team>> _teamsById = [];
    private readonly Dictionary<ulong, List<TeamMember>> _membersById = [];
    private RakNetGuid _host;
    private bool _autoAddParticipants = true;
    private uint _teamRequestIndex;

    internal TeamWorld(TeamManager manager, byte worldId, RakNetGuid host)
    {
        Manager = manager;
        WorldId = worldId;
        _host = host;
    }

    /// <summary>GetTeamManager</summary>
    public TeamManager TeamManager => Manager;

    /// <summary>GetWorldId: the ID given to <see cref="TeamManager.AddWorld"/>.</summary>
    public byte WorldId { get; }

    /// <summary>
    /// SetHost / GetHost: the system that resolves requests and balances teams. Setting it on the host itself lets waiting
    /// members join. <see cref="TeamManager"/> sets it from ID_FCM2_NEW_HOST.
    /// </summary>
    public RakNetGuid Host
    {
        get
        {
            lock (Gate)
                return _host;
        }
        set
        {
            if (!value.IsAssigned)
                throw new ArgumentException("The host must be an assigned GUID.", nameof(value));
            SetHost(value);
        }
    }

    /// <summary>
    /// SetAutoManageConnections: whether each new connection becomes a participant. True by default;
    /// <see cref="TeamManager.AutoManageConnections"/> sets it on every world.
    /// </summary>
    public bool AutoManageConnections
    {
        get
        {
            lock (Gate)
                return _autoAddParticipants;
        }
        set
        {
            lock (Gate)
                _autoAddParticipants = value;
        }
    }

    /// <summary>GetBalanceTeams: whether the host keeps balanced teams even.</summary>
    public bool BalanceTeams
    {
        get
        {
            lock (Gate)
                return BalanceTeamsActive;
        }
    }

    /// <summary>GetTeamCount</summary>
    public int TeamCount
    {
        get
        {
            lock (Gate)
                return _teams.Count;
        }
    }

    /// <summary>GetTeamMemberCount</summary>
    public int TeamMemberCount
    {
        get
        {
            lock (Gate)
                return _members.Count;
        }
    }

    internal TeamManager Manager { get; }

    internal Lock Gate => Manager.Gate;

    internal bool BalanceTeamsActive { get; set; }

    internal bool IsHostHere => _host == Manager.MyGuid;

    internal List<Team> Teams => _teams;

    /// <summary>
    /// AddParticipant: a system that team operations are sent to. The host tells it whether teams are balanced.
    /// </summary>
    public void AddParticipant(RakNetGuid rakNetGuid)
    {
        lock (Gate)
        {
            _participants.Add(rakNetGuid);
            if (IsHostHere)
            {
                using var message = TeamManager.Internal(TeamManager.Operation.SetBalanceTeamsInitial, WorldId);
                message.WriteBool(BalanceTeamsActive);
                Manager.SendTo(message, rakNetGuid);
            }
        }
    }

    /// <summary>RemoveParticipant: done for each closed connection.</summary>
    public void RemoveParticipant(RakNetGuid rakNetGuid)
    {
        lock (Gate)
            _participants.Remove(rakNetGuid);
    }

    /// <summary>GetParticipantList, in the order they were added.</summary>
    public IReadOnlyList<RakNetGuid> GetParticipantList()
    {
        lock (Gate)
            return [.. _participants];
    }

    /// <summary>
    /// ReferenceTeam: registers <paramref name="team"/> under <paramref name="networkId"/>, the same on every system, and
    /// says whether balancing applies to it. A team already registered is left alone.
    /// </summary>
    public void ReferenceTeam(Team team, ulong networkId, bool applyBalancing)
    {
        ArgumentNullException.ThrowIfNull(team);
        lock (Gate)
        {
            if (_teams.Contains(team))
                return;
            team.Id = networkId;
            team.Balancing = applyBalancing;
            team.WorldRef = this;
            _teams.Add(team);
            Push(_teamsById, networkId, team);
            // TeamManager.cpp:1151-1155: on any system, as RakNet's host check is only an assertion.
            if (applyBalancing && BalanceTeamsActive)
                EnforceTeamBalance(0);
        }
    }

    /// <summary>
    /// DereferenceTeam: every member leaves the team, newest first, as with <see cref="TeamMember.LeaveTeam"/>, and the team
    /// is forgotten.
    /// </summary>
    public void DereferenceTeam(Team team, byte noTeamSubcategory)
    {
        ArgumentNullException.ThrowIfNull(team);
        lock (Gate)
        {
            var index = _teams.IndexOf(team);
            if (index < 0)
                return;
            while (team.Members.Count > 0)
            {
                var member = team.Members[^1];
                if (!member.LeaveTeam(team, noTeamSubcategory))
                    team.Members.RemoveAt(team.Members.Count - 1);
            }
            _teams.RemoveAt(index);
            Remove(_teamsById, team.Id);
        }
    }

    /// <summary>GetTeamByIndex, in reference order.</summary>
    public Team GetTeamByIndex(int index)
    {
        lock (Gate)
            return _teams[index];
    }

    /// <summary>GetTeamByNetworkID: the team last referenced with that ID, or null.</summary>
    public Team? GetTeamByNetworkId(ulong teamId)
    {
        lock (Gate)
            return FindTeam(teamId);
    }

    /// <summary>GetTeamIndex: -1 when the team is not in this world.</summary>
    public int GetTeamIndex(Team team)
    {
        lock (Gate)
            return _teams.IndexOf(team);
    }

    /// <summary>
    /// ReferenceTeamMember: registers <paramref name="teamMember"/> under <paramref name="networkId"/>, the same on every
    /// system. A member already registered is left alone.
    /// </summary>
    public void ReferenceTeamMember(TeamMember teamMember, ulong networkId)
    {
        ArgumentNullException.ThrowIfNull(teamMember);
        lock (Gate)
        {
            if (_members.Contains(teamMember))
                return;
            teamMember.WorldRef = this;
            teamMember.Id = networkId;
            _members.Add(teamMember);
            Push(_membersById, networkId, teamMember);
        }
    }

    /// <summary>
    /// DereferenceTeamMember: takes the member off every team and request, here only and without messages, and forgets it,
    /// as when its replica is destroyed.
    /// </summary>
    public void DereferenceTeamMember(TeamMember teamMember)
    {
        ArgumentNullException.ThrowIfNull(teamMember);
        lock (Gate)
        {
            var index = _members.IndexOf(teamMember);
            if (index < 0)
                return;
            teamMember.UpdateListsToNoTeam(0);
            Remove(_membersById, teamMember.Id);
            _members.RemoveAt(index);
        }
    }

    /// <summary>GetTeamMemberByIndex, in reference order.</summary>
    public TeamMember GetTeamMemberByIndex(int index)
    {
        lock (Gate)
            return _members[index];
    }

    /// <summary>GetTeamMemberIDByIndex</summary>
    public ulong GetTeamMemberIdByIndex(int index)
    {
        lock (Gate)
            return _members[index].Id;
    }

    /// <summary>GetTeamMemberByNetworkID: the member last referenced with that ID, or null.</summary>
    public TeamMember? GetTeamMemberByNetworkId(ulong teamMemberId)
    {
        lock (Gate)
            return FindMember(teamMemberId);
    }

    /// <summary>GetTeamMemberIndex: -1 when the member is not in this world.</summary>
    public int GetTeamMemberIndex(TeamMember teamMember)
    {
        lock (Gate)
            return _members.IndexOf(teamMember);
    }

    /// <summary>
    /// SetBalanceTeams: turns balancing on or off here and asks the host to apply it. On, the host keeps every balanced
    /// team within one member of the others, moving the newest members of a larger team to an emptier one that allows
    /// rebalancing, or to no team with <paramref name="noTeamSubcategory"/>; members may then join a balanced team only
    /// without unbalancing it, or by swapping. Off, members waiting for a team may join it. False when unchanged.
    /// </summary>
    public bool SetBalanceTeams(bool balanceTeams, byte noTeamSubcategory)
    {
        lock (Gate)
        {
            if (balanceTeams == BalanceTeamsActive)
                return false;
            BalanceTeamsActive = balanceTeams;
            using var message = TeamManager.Internal(TeamManager.Operation.SetBalanceTeams, WorldId);
            message.WriteBool(balanceTeams);
            message.WriteByte(noTeamSubcategory);
            Manager.SendTo(message, _host);
            return true;
        }
    }

    /// <summary>
    /// Clear: forgets every participant, team and member; the teams and members are no longer in a world. Unlike RakNet,
    /// the NetworkID lookups are emptied too, where RakNet keeps them pointing at objects with no world.
    /// </summary>
    public void Clear()
    {
        lock (Gate)
        {
            foreach (var team in _teams)
                team.WorldRef = null;
            foreach (var member in _members)
                member.WorldRef = null;
            _participants.Clear();
            _teams.Clear();
            _members.Clear();
            _teamsById.Clear();
            _membersById.Clear();
        }
    }

    // SetHost (TeamManager.cpp:1328-1339); RakNet only asserts that the host is assigned.
    internal void SetHost(RakNetGuid host)
    {
        lock (Gate)
        {
            if (_host == host)
                return;
            _host = host;
            if (IsHostHere)
                FillRequestedSlots();
        }
    }

    internal Team? FindTeam(ulong teamId) => _teamsById.TryGetValue(teamId, out var list) ? list[^1] : null;

    internal TeamMember? FindMember(ulong memberId) => _membersById.TryGetValue(memberId, out var list) ? list[^1] : null;

    internal uint NextRequestIndex() => _teamRequestIndex++;

    // OnClosedConnection / OnNewConnection (TeamManager.cpp:1374-1391).
    internal void OnClosedConnection(RakNetGuid rakNetGuid) => RemoveParticipant(rakNetGuid);

    internal void OnNewConnection(RakNetGuid rakNetGuid)
    {
        if (_autoAddParticipants)
            AddParticipant(rakNetGuid);
    }

    // EnforceTeamBalance (TeamManager.cpp:1395-1401).
    internal void EnforceTeamBalance(byte noTeamSubcategory) => KickExcessMembers(noTeamSubcategory);

    // KickExcessMembers (TeamManager.cpp:1406-1459): the newest member of a team over its limit, or over the balanced
    // limit while balancing, moves to the emptiest team that allows rebalancing, or leaves to no team.
    internal void KickExcessMembers(byte noTeamSubcategory)
    {
        var balancedTeamLimit = BalanceTeamsActive ? GetBalancedTeamLimit() : ushort.MaxValue;
        for (var i = 0; i < _teams.Count; i++)
        {
            var team = _teams[i];
            while (team.Limit < team.Members.Count || (balancedTeamLimit < team.Members.Count && team.Balancing))
            {
                var member = team.Members[^1];
                var teamIndex = GetAvailableTeamIndexWithFewestMembers(balancedTeamLimit, JoinPermissions.Rebalancing);
                if (teamIndex < 0)
                {
                    member.LeaveTeam(team, noTeamSubcategory);
                    Manager.PushTeamAssigned(member);
                }
                else
                {
                    var teamToJoin = _teams[teamIndex];
                    member.StoreLastTeams();
                    Manager.RemoveFromTeamsRequestedAndAddTeam(member, teamToJoin, isTeamSwitch: true, team);
                    using var message = TeamManager.Internal(TeamManager.Operation.RemoveFromTeamsRequestedAndAddTeam, WorldId);
                    message.WriteUInt64(member.Id);
                    message.WriteUInt64(teamToJoin.Id);
                    message.WriteBool(true);
                    message.WriteBool(true);
                    message.WriteUInt64(team.Id);
                    BroadcastToParticipants(message, RakNetGuid.Unassigned);
                }
            }
        }
    }

    // FillRequestedSlots (TeamManager.cpp:1462-1583): the host meets waiting requests oldest first.
    internal void FillRequestedSlots()
    {
        var balancedTeamLimit = BalanceTeamsActive ? GetBalancedTeamLimit() : ushort.MaxValue;
        foreach (var joinRequest in GetSortedJoinRequests())
        {
            var member = joinRequest.Member;
            // RakNet reads each request at the index it had when the list was made, which after one of the member's
            // requests is met reads a different or stale request (TeamManager.cpp:1493-1495); here the request itself is
            // looked up again, and skipped when it has gone.
            RequestedTeam? request = null;
            int teamIndex;
            if (joinRequest.Request is null)
            {
                if (member.Requests.Count != 0 || member.JoinType != JoinTeamType.AnyAvailableTeam)
                    continue;
                teamIndex = GetAvailableTeamIndexWithFewestMembers(balancedTeamLimit, JoinPermissions.AnyAvailableTeam);
            }
            else
            {
                if (!member.Requests.Contains(joinRequest.Request))
                    continue;
                request = joinRequest.Request;
                var requested = request.Requested;
                teamIndex = requested.Members.Count < balancedTeamLimit && requested.Members.Count < requested.Limit
                            && (requested.Permissions & JoinPermissions.SpecificTeam) != 0
                    ? _teams.IndexOf(requested)
                    : -1;
            }
            if (teamIndex < 0)
                continue;
            var team = _teams[teamIndex];
            if (request is null)
            {
                member.StoreLastTeams();
                member.UpdateTeamsRequestedToNone();
                member.AddToTeamList(team);
                Manager.PushTeamAssigned(member);
                using var message = TeamManager.Internal(TeamManager.Operation.UpdateTeamsRequestedToNoneAndAddTeam, WorldId);
                message.WriteUInt64(member.Id);
                message.WriteUInt64(team.Id);
                BroadcastToParticipants(message, RakNetGuid.Unassigned);
                continue;
            }
            var isSwitch = request.IsTeamSwitch;
            Team? teamToLeave = null;
            List<Team> leaving = [];
            if (isSwitch)
            {
                teamToLeave = request.TeamToLeave;
                if (teamToLeave is not null)
                {
                    if (member.IsOnTeamInternal(teamToLeave))
                    {
                        leaving.Add(teamToLeave);
                    }
                    else
                    {
                        teamToLeave = null;
                        isSwitch = false;
                    }
                }
                else
                {
                    leaving.AddRange(member.Teams);
                }
            }
            if (JoinSpecificTeam(member, team, isSwitch, teamToLeave, leaving) == 1)
            {
                using var message = TeamManager.Internal(TeamManager.Operation.RemoveFromTeamsRequestedAndAddTeam, WorldId);
                message.WriteUInt64(member.Id);
                message.WriteUInt64(team.Id);
                message.WriteBool(isSwitch);
                TeamMember.WriteOptionalTeam(message, teamToLeave);
                BroadcastToParticipants(message, RakNetGuid.Unassigned);
            }
        }
    }

    // GetAvailableTeamIndexWithFewestMembers (TeamManager.cpp:1587-1607): the first of the emptiest.
    internal int GetAvailableTeamIndexWithFewestMembers(ushort secondaryLimit, JoinPermissions permissions)
    {
        var lowestCount = int.MaxValue;
        var lowestIndex = -1;
        for (var i = 0; i < _teams.Count; i++)
        {
            var team = _teams[i];
            var count = team.Members.Count;
            if (count < secondaryLimit && count < team.Limit && count < lowestCount && (permissions & team.Permissions) != 0)
            {
                lowestCount = count;
                lowestIndex = i;
            }
        }
        return lowestIndex;
    }

    // GetSortedJoinRequests (TeamManager.cpp:1611-1644): by time, then request index; an OrderedList keeps only the first
    // of equal keys.
    private List<JoinRequest> GetSortedJoinRequests()
    {
        var requests = new List<JoinRequest>();
        foreach (var member in _members)
        {
            if (member.Requests.Count == 0)
            {
                if (member.JoinType == JoinTeamType.AnyAvailableTeam)
                    Insert(new JoinRequest(member.WhenJoinAnyRequested, member.JoinAnyRequestIndex, member, null));
            }
            else
            {
                foreach (var request in member.Requests)
                    Insert(new JoinRequest(request.WhenRequested, request.RequestIndex, member, request));
            }
        }
        return requests;

        void Insert(JoinRequest request)
        {
            var index = requests.FindIndex(r => (r.WhenRequested, r.RequestIndex).CompareTo((request.WhenRequested, request.RequestIndex)) >= 0);
            if (index < 0)
            {
                requests.Add(request);
            }
            else if (requests[index].WhenRequested != request.WhenRequested || requests[index].RequestIndex != request.RequestIndex)
            {
                requests.Insert(index, request);
            }
        }
    }

    // BroadcastToParticipants (TeamManager.cpp:1647-1667).
    internal void BroadcastToParticipants(OutgoingMessage message, RakNetGuid exclusion)
    {
        foreach (var participant in _participants)
        {
            if (participant != exclusion)
                Manager.SendTo(message, participant);
        }
    }

    internal void BroadcastToParticipants(BitStream message, RakNetGuid exclusion) => BroadcastToParticipants((OutgoingMessage)message, exclusion);

    // JoinAnyTeam (TeamManager.cpp:1671-1706): 1 joined, -2 full (with a full team that allows any), -1 locked.
    internal Team? JoinAnyTeam(TeamMember member, out int resultCode)
    {
        var balancedLimit = GetBalancedTeamLimit();
        var index = GetAvailableTeamIndexWithFewestMembers(balancedLimit, JoinPermissions.AnyAvailableTeam);
        if (index < 0)
        {
            foreach (var team in _teams)
            {
                if ((team.Members.Count >= balancedLimit || team.Members.Count >= team.Limit) && team.Limit != 0
                    && (team.Permissions & JoinPermissions.AnyAvailableTeam) != 0)
                {
                    resultCode = -2;
                    return team;
                }
            }
            resultCode = -1;
            return null;
        }
        var lowest = _teams[index];
        member.StoreLastTeams();
        member.UpdateTeamsRequestedToNone();
        member.AddToTeamList(lowest);
        Manager.PushTeamAssigned(member);
        resultCode = 1;
        return lowest;
    }

    // JoinSpecificTeam (TeamManager.cpp:1710-1766): 1 joined, -2 full, -1 locked. While balancing, a switch only happens
    // as a swap with a member on the team wanting ours.
    internal int JoinSpecificTeam(TeamMember member, Team team, bool isTeamSwitch, Team? teamToLeave, List<Team> leaving)
    {
        if ((team.Permissions & JoinPermissions.SpecificTeam) == 0)
            return -1;
        if (!BalanceTeamsActive || leaving.Count == 0)
        {
            if (team.GetMemberLimit() <= team.Members.Count)
                return -2;
            member.StoreLastTeams();
            Manager.RemoveFromTeamsRequestedAndAddTeam(member, team, isTeamSwitch, teamToLeave);
            return 1;
        }
        if (leaving.Count == 1)
        {
            var j = team.GetMemberWithRequestedSingleTeamSwitch(leaving[0]);
            if (j >= 0)
            {
                var swapping = team.Members[j];
                member.StoreLastTeams();
                swapping.StoreLastTeams();
                Manager.RemoveFromTeamsRequestedAndAddTeam(member, team, isTeamSwitch: true, null);
                Manager.RemoveFromTeamsRequestedAndAddTeam(swapping, leaving[0], isTeamSwitch: true, null);
                // The caller's message pushes ID_TEAM_BALANCER_TEAM_ASSIGNED for the member; the swapped one is told here.
                using var assigned = new BitStream(64);
                assigned.WriteByte((byte)MessageId.TeamBalancerTeamAssigned);
                TeamManager.EncodeTeamAssigned(assigned, swapping);
                BroadcastToParticipants(assigned, RakNetGuid.Unassigned);
                return 1;
            }
        }
        return -2;
    }

    // GetBalancedTeamLimit (TeamManager.cpp:1770-1808): members spread over the teams, leaving out teams whose own limit
    // is lower and the members they hold; in RakNet's unsigned arithmetic, cut to 16 bits.
    internal ushort GetBalancedTeamLimit()
    {
        if (_teams.Count == 0)
            return 0;
        if (!BalanceTeamsActive)
            return ushort.MaxValue;
        var teamsCount = (uint)_teams.Count;
        var membersCount = (uint)_members.Count;
        var considered = new List<Team>(_teams);
        ushort balancedLimit;
        bool excluded;
        do
        {
            excluded = false;
            balancedLimit = unchecked((ushort)((membersCount + (teamsCount - 1)) / teamsCount));
            var i = 0;
            while (i < considered.Count)
            {
                if (considered[i].Limit < balancedLimit)
                {
                    excluded = true;
                    membersCount = unchecked(membersCount - considered[i].Limit);
                    teamsCount--;
                    // RemoveAtIndexFast: the last moves into the gap.
                    considered[i] = considered[^1];
                    considered.RemoveAt(considered.Count - 1);
                }
                else
                {
                    i++;
                }
            }
        }
        while (excluded && teamsCount > 0);
        return balancedLimit;
    }

    private static void Push<T>(Dictionary<ulong, List<T>> index, ulong key, T item)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = [];
        list.Add(item);
    }

    private static void Remove<T>(Dictionary<ulong, List<T>> index, ulong key)
    {
        if (!index.TryGetValue(key, out var list))
            return;
        list.RemoveAt(list.Count - 1);
        if (list.Count == 0)
            index.Remove(key);
    }

    // TM_World::JoinRequestHelper, with the request itself rather than its index.
    private sealed record JoinRequest(ulong WhenRequested, uint RequestIndex, TeamMember Member, RequestedTeam? Request);
}
