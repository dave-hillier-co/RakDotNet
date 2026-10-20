using System.Security.Cryptography;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>How the host picks a team for a member that asks for any: TeamBalancer::DefaultAssigmentAlgorithm.</summary>
public enum DefaultAssignmentAlgorithm
{
    /// <summary>SMALLEST_TEAM: the team with the fewest members, the first of equals.</summary>
    SmallestTeam,

    /// <summary>FILL_IN_ORDER: the first team with room.</summary>
    FillInOrder,
}

/// <summary>
/// TeamBalancer: team selection resolved by a host, with team size limits, switching, locking and even teams. RakNet
/// deprecates it in favour of <see cref="TeamManager"/>; the two cannot be attached together.
/// </summary>
/// <remarks>
/// <para>
/// Ported from TeamBalancer.cpp. Teams are numbered 0 to 254; <see cref="UnassignedTeamId"/> is no team. Each system
/// asks the host with ID_TEAM_BALANCER_INTERNAL, an operation byte and the member's 64-bit NetworkID (a team byte
/// follows for a specific team), which the host consumes. The host answers the member's system only, with
/// ID_TEAM_BALANCER_TEAM_ASSIGNED (team byte, then NetworkID), ID_TEAM_BALANCER_REQUESTED_TEAM_FULL (team byte, then
/// NetworkID) while a switch waits, or ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED (the team byte alone); each is consumed
/// unless it comes from the host, and ID_TEAM_BALANCER_TEAM_ASSIGNED also unless the member is this system's. Everything
/// is sent HIGH_PRIORITY and RELIABLE_ORDERED on channel 0.
/// </para>
/// <para>
/// The host is this system until ID_FCM2_NEW_HOST names another; each system then sends the new host its members' teams
/// and requests, so set the team size limits on every system that may become host. RakNet's behaviour is kept where it
/// surprises: team numbers are checked against the limits with &gt; rather than &gt;=; a status update stops at the first
/// member with neither a team nor a request; members moved to even teams or to a freed slot are picked at random; and a
/// member removed from the host's list is replaced by the last. Deliberate differences: a team number past the limits, which
/// RakNet would read beyond its lists for, is treated as full or ignored; and a truncated message, where RakNet would read
/// uninitialised values, is ignored, none of its members applied.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class TeamBalancer : IRakPlugin
{
    /// <summary>UNASSIGNED_TEAM_ID: no team.</summary>
    public const byte UnassignedTeamId = 255;

    private readonly Lock _gate = new();
    private readonly List<OwnMember> _myTeamMembers = [];
    private readonly List<ushort> _teamLimits = [];
    private readonly List<ushort> _teamMemberCounts = [];
    private readonly List<HostMember> _teamMembers = [];
    private IPluginHost? _host;
    private RakNetGuid _hostGuid = RakNetGuid.Unassigned;
    private DefaultAssignmentAlgorithm _defaultAssignmentAlgorithm = DefaultAssignmentAlgorithm.SmallestTeam;
    private bool _forceTeamsToBeEven;
    private bool _lockTeams;

    // TeamBalancerOperations: the byte after ID_TEAM_BALANCER_INTERNAL.
    private enum Operation : byte
    {
        StatusUpdateToNewHost,
        CancelTeamRequest,
        RequestAnyTeam,
        RequestSpecificTeam,
    }

    /// <summary>The host's GUID: this system until ID_FCM2_NEW_HOST names another.</summary>
    public RakNetGuid HostGuid
    {
        get
        {
            lock (_gate)
                return _hostGuid;
        }
    }

    /// <summary>SetDefaultAssignmentAlgorithm: used by the host; teams already set are not changed. Smallest team by default.</summary>
    public DefaultAssignmentAlgorithm DefaultAssignmentAlgorithm
    {
        get
        {
            lock (_gate)
                return _defaultAssignmentAlgorithm;
        }
        set
        {
            lock (_gate)
                _defaultAssignmentAlgorithm = value;
        }
    }

    /// <summary>
    /// SetTeamSizeLimit: the most members <paramref name="team"/> may have, enforced by the host. Setting a team past the
    /// last adds the teams between with a limit of 0.
    /// </summary>
    public void SetTeamSizeLimit(byte team, ushort limit)
    {
        lock (_gate)
        {
            Replace(_teamLimits, limit, 0, team);
            if (_teamLimits.Count > _teamMemberCounts.Count)
                Replace(_teamMemberCounts, 0, 0, _teamLimits.Count - 1);
        }
    }

    /// <summary>
    /// SetForceEvenTeams: whether the host keeps teams within one member of each other. Turning it on while teams are not
    /// locked moves members at random from the largest teams, preferring those that asked for the team they go to, and
    /// tells them with ID_TEAM_BALANCER_TEAM_ASSIGNED. Off by default.
    /// </summary>
    public void SetForceEvenTeams(bool force)
    {
        lock (_gate)
        {
            _forceTeamsToBeEven = force;
            if (_lockTeams)
                return;
            if (_forceTeamsToBeEven)
                EvenTeams();
        }
    }

    /// <summary>
    /// SetLockTeams: while locked, a member on a team asking for another is answered ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED,
    /// though the request is kept. Unlocking swaps members that want each other's teams, then evens the teams or moves
    /// waiting members into teams with room. Unlocked by default.
    /// </summary>
    public void SetLockTeams(bool lockTeams)
    {
        lock (_gate)
        {
            if (lockTeams == _lockTeams)
                return;
            _lockTeams = lockTeams;
            if (_lockTeams)
                return;
            // TeamBalancer.cpp:80-97: RakNet's TeamId counters wrap and never end past 255 members; int counters here.
            for (var i = 0; i < _teamMembers.Count; i++)
            {
                if (_teamMembers[i].RequestedTeam == UnassignedTeamId)
                    continue;
                for (var j = i + 1; j < _teamMembers.Count; j++)
                {
                    if (_teamMembers[j].RequestedTeam == _teamMembers[i].CurrentTeam && _teamMembers[i].RequestedTeam == _teamMembers[j].CurrentTeam)
                    {
                        SwapTeamMembersByRequest(i, j);
                        NotifyTeamAssignment(i);
                        NotifyTeamAssignment(j);
                    }
                }
            }
            if (_forceTeamsToBeEven)
            {
                EvenTeams();
                return;
            }
            for (var i = 0; i < _teamMembers.Count; i++)
            {
                var requested = _teamMembers[i].RequestedTeam;
                if (requested != UnassignedTeamId && requested < _teamLimits.Count && _teamMemberCounts[requested] < _teamLimits[requested])
                {
                    SwitchMemberTeam(i, requested);
                    NotifyTeamAssignment(i);
                }
            }
        }
    }

    /// <summary>
    /// RequestSpecificTeam: asks the host for <paramref name="desiredTeam"/> for the member <paramref name="memberId"/>
    /// (any consistent value with one member per system), or <see cref="UnassignedTeamId"/> for no team. The answer is
    /// ID_TEAM_BALANCER_TEAM_ASSIGNED, or ID_TEAM_BALANCER_REQUESTED_TEAM_FULL while the switch waits for room or a swap,
    /// or ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED.
    /// </summary>
    public void RequestSpecificTeam(ulong memberId, byte desiredTeam)
    {
        lock (_gate)
        {
            var found = false;
            foreach (var member in _myTeamMembers)
            {
                if (member.MemberId != memberId)
                    continue;
                found = true;
                if (member.RequestedTeam == desiredTeam && member.CurrentTeam == desiredTeam)
                    return;
                member.RequestedTeam = desiredTeam;
            }
            if (!found)
                _myTeamMembers.Add(new OwnMember(memberId, UnassignedTeamId, desiredTeam));
            var message = new byte[11];
            Header(message, Operation.RequestSpecificTeam, memberId);
            message[10] = desiredTeam;
            SendToHost(message);
        }
    }

    /// <summary>
    /// CancelRequestSpecificTeam: withdraws a waiting switch, keeping the current team. A switch the host is making at the
    /// same time may still happen.
    /// </summary>
    public void CancelRequestSpecificTeam(ulong memberId)
    {
        lock (_gate)
        {
            var member = _myTeamMembers.Find(m => m.MemberId == memberId);
            if (member is null)
                return;
            member.RequestedTeam = UnassignedTeamId;
            var message = new byte[10];
            Header(message, Operation.CancelTeamRequest, memberId);
            SendToHost(message);
        }
    }

    /// <summary>
    /// RequestAnyTeam: lets the host pick a team by <see cref="DefaultAssignmentAlgorithm"/>. Nothing happens for a member
    /// already on a team.
    /// </summary>
    public void RequestAnyTeam(ulong memberId)
    {
        lock (_gate)
        {
            var member = _myTeamMembers.Find(m => m.MemberId == memberId);
            if (member is not null)
            {
                if (member.CurrentTeam != UnassignedTeamId)
                    return;
                member.RequestedTeam = UnassignedTeamId;
            }
            else
            {
                _myTeamMembers.Add(new OwnMember(memberId, UnassignedTeamId, UnassignedTeamId));
            }
            var message = new byte[10];
            Header(message, Operation.RequestAnyTeam, memberId);
            SendToHost(message);
        }
    }

    /// <summary>GetMyTeam: the team the host last assigned the member, or <see cref="UnassignedTeamId"/>.</summary>
    public byte GetMyTeam(ulong memberId)
    {
        lock (_gate)
            return _myTeamMembers.Find(m => m.MemberId == memberId)?.CurrentTeam ?? UnassignedTeamId;
    }

    /// <summary>DeleteMember: forgets a member of this system, and on the host the first member with that NetworkID.</summary>
    public void DeleteMember(ulong memberId)
    {
        lock (_gate)
        {
            var own = _myTeamMembers.FindIndex(m => m.MemberId == memberId);
            if (own >= 0)
                RemoveAtFast(_myTeamMembers, own);
            var hosted = _teamMembers.FindIndex(m => m.MemberId == memberId);
            if (hosted >= 0)
                RemoveTeamMember(hosted);
        }
    }

    /// <summary>OnAttach: the host is this system (TeamBalancer.cpp:329-332).</summary>
    void IRakPlugin.OnAttached(IPluginHost host)
    {
        lock (_gate)
        {
            _host = host;
            _hostGuid = host.RakNetGuid;
        }
    }

    void IRakPlugin.OnDetached()
    {
        lock (_gate)
            _host = null;
    }

    /// <summary>OnClosedConnection: RemoveByGuid (TeamBalancer.cpp:322-368).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            if (!WeAreHost())
                return;
            var index = 0;
            while (index < _teamMembers.Count)
            {
                if (_teamMembers[index].MemberGuid != connection.RakNetGuid)
                {
                    index++;
                    continue;
                }
                var droppedTeam = _teamMembers[index].CurrentTeam;
                RemoveTeamMember(index);
                if (_lockTeams)
                    continue;
                if (_forceTeamsToBeEven)
                    EvenTeams();
                else if (droppedTeam < _teamLimits.Count && _teamMemberCounts[droppedTeam] == _teamLimits[droppedTeam] - 1)
                    MoveMemberThatWantsToJoinTeam(droppedTeam);
            }
        }
    }

    /// <summary>OnReceive (TeamBalancer.cpp:244-321).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.Fcm2NewHost:
                    OnNewHost(e.RakNetGuid);
                    return PluginResult.Continue;
                case MessageId.TeamBalancerInternal:
                    if (data.Length >= 2)
                        OnInternal(data, e.RakNetGuid);
                    return PluginResult.Consume;
                case MessageId.TeamBalancerTeamAssigned:
                    return OnTeamAssigned(data, e.RakNetGuid);
                case MessageId.TeamBalancerRequestedTeamFull:
                case MessageId.TeamBalancerRequestedTeamLocked:
                    return e.RakNetGuid == _hostGuid ? PluginResult.Continue : PluginResult.Consume;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    // ID_FCM2_NEW_HOST (TeamBalancer.cpp:248-268): the new host is sent every member's team and request.
    private void OnNewHost(RakNetGuid newHost)
    {
        _hostGuid = newHost;
        if (_myTeamMembers.Count == 0)
            return;
        var count = (byte)_myTeamMembers.Count;
        var message = new byte[3 + (count * 10)];
        message[0] = (byte)MessageId.TeamBalancerInternal;
        message[1] = (byte)Operation.StatusUpdateToNewHost;
        message[2] = count;
        for (var i = 0; i < count; i++)
        {
            var member = _myTeamMembers[i];
            var at = 3 + (i * 10);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(at), member.MemberId);
            message[at + 8] = member.CurrentTeam;
            message[at + 9] = member.RequestedTeam;
        }
        SendToHost(message);
    }

    private void OnInternal(ReadOnlySpan<byte> data, RakNetGuid sender)
    {
        if (!WeAreHost())
            return;
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(2);
        switch ((Operation)data[1])
        {
            case Operation.StatusUpdateToNewHost:
                OnStatusUpdateToNewHost(ref reader, sender);
                break;
            case Operation.CancelTeamRequest:
                if (reader.TryReadUInt64(out var cancelled))
                {
                    var index = GetMemberIndex(cancelled, sender);
                    if (index >= 0)
                        _teamMembers[index].RequestedTeam = UnassignedTeamId;
                }
                break;
            case Operation.RequestAnyTeam:
                if (reader.TryReadUInt64(out var anyMember))
                    OnRequestAnyTeam(anyMember, sender);
                break;
            case Operation.RequestSpecificTeam:
                if (reader.TryReadUInt64(out var specificMember) && reader.TryReadByte(out var requested))
                    OnRequestSpecificTeam(specificMember, requested, sender);
                break;
        }
    }

    // OnStatusUpdateToNewHost (TeamBalancer.cpp:369-437): the new host adds members it does not know, without balancing.
    private void OnStatusUpdateToNewHost(ref BitStreamReader reader, RakNetGuid sender)
    {
        if (!reader.TryReadByte(out var count))
            return;
        // Every entry is read before any is applied, so a truncated update adds nothing.
        var entries = new (ulong MemberId, byte CurrentTeam, byte RequestedTeam)[count];
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt64(out var id) || !reader.TryReadByte(out var current) || !reader.TryReadByte(out var requested))
                return;
            entries[i] = (id, current, requested);
        }
        foreach (var (memberId, team, requestedTeam) in entries)
        {
            var currentTeam = team;
            if (currentTeam != UnassignedTeamId && currentTeam > _teamLimits.Count)
                return;
            if (requestedTeam != UnassignedTeamId && requestedTeam > _teamLimits.Count)
                return;
            if (currentTeam == UnassignedTeamId && requestedTeam == UnassignedTeamId)
                return;
            if (GetMemberIndex(memberId, sender) >= 0)
                continue;
            if (currentTeam == UnassignedTeamId)
            {
                currentTeam = requestedTeam == UnassignedTeamId || TeamWouldBeOverpopulatedOnAddition(requestedTeam, _teamMembers.Count)
                    ? GetNextDefaultTeam()
                    : requestedTeam;
            }
            if (currentTeam == UnassignedTeamId)
                return;
            AddAndNotify(new HostMember(sender, memberId, currentTeam, requestedTeam));
        }
    }

    // OnRequestAnyTeam (TeamBalancer.cpp:452-477).
    private void OnRequestAnyTeam(ulong memberId, RakNetGuid sender)
    {
        if (GetMemberIndex(memberId, sender) >= 0)
            return;
        var team = GetNextDefaultTeam();
        if (team == UnassignedTeamId)
            return;
        AddAndNotify(new HostMember(sender, memberId, team, UnassignedTeamId));
    }

    // OnRequestSpecificTeam (TeamBalancer.cpp:478-564).
    private void OnRequestSpecificTeam(ulong memberId, byte requestedTeam, RakNetGuid sender)
    {
        var memberIndex = GetMemberIndex(memberId, sender);
        if (requestedTeam == UnassignedTeamId)
        {
            NotifyNoTeam(memberId, sender);
            if (memberIndex >= 0)
                RemoveTeamMember(memberIndex);
            return;
        }
        if (requestedTeam > _teamLimits.Count)
            return;
        if (memberIndex < 0)
        {
            var member = new HostMember(sender, memberId, UnassignedTeamId, requestedTeam);
            if (!TeamWouldBeOverpopulatedOnAddition(requestedTeam, _teamMembers.Count))
            {
                member.CurrentTeam = requestedTeam;
                member.RequestedTeam = UnassignedTeamId;
            }
            else
            {
                member.CurrentTeam = GetNextDefaultTeam();
            }
            if (member.CurrentTeam == UnassignedTeamId)
                return;
            AddAndNotify(member);
            return;
        }
        _teamMembers[memberIndex].RequestedTeam = requestedTeam;
        var oldTeam = _teamMembers[memberIndex].CurrentTeam;
        if (_lockTeams)
        {
            NotifyTeamsLocked(sender, requestedTeam);
            return;
        }
        if (TeamsWouldBeEvenOnSwitch(requestedTeam, oldTeam))
        {
            SwitchMemberTeam(memberIndex, requestedTeam);
            NotifyTeamAssignment(memberIndex);
            return;
        }
        // Someone on the team asked for wants ours: they swap.
        var swappable = _teamMembers.FindIndex(m => m.CurrentTeam == requestedTeam && m.RequestedTeam == oldTeam);
        if (swappable >= 0)
        {
            SwapTeamMembersByRequest(memberIndex, swappable);
            NotifyTeamAssignment(memberIndex);
            NotifyTeamAssignment(swappable);
        }
        else
        {
            NotifyTeamSwitchPending(sender, requestedTeam, memberId);
        }
    }

    // OnTeamAssigned (TeamBalancer.cpp:697-729): only the host's, and only for a member of this system, reaches the
    // application.
    private PluginResult OnTeamAssigned(ReadOnlySpan<byte> data, RakNetGuid sender)
    {
        if (sender != _hostGuid)
            return PluginResult.Consume;
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(1);
        if (!reader.TryReadByte(out var currentTeam) || !reader.TryReadUInt64(out var memberId))
            return PluginResult.Consume;
        var member = _myTeamMembers.Find(m => m.MemberId == memberId);
        if (member is null)
            return PluginResult.Consume;
        if (member.RequestedTeam == currentTeam)
            member.RequestedTeam = UnassignedTeamId;
        member.CurrentTeam = currentTeam;
        return PluginResult.Continue;
    }

    private void AddAndNotify(HostMember member)
    {
        var index = AddTeamMember(member);
        if (index >= 0)
            NotifyTeamAssignment(index);
    }

    private int GetMemberIndex(ulong memberId, RakNetGuid guid) =>
        _teamMembers.FindIndex(m => m.MemberGuid == guid && m.MemberId == memberId);

    // AddTeamMember (TeamBalancer.cpp:574-590). RakNet checks the team with > and then counts one past its lists for a
    // team equal to their size; such a member is not added here.
    private int AddTeamMember(HostMember member)
    {
        if (member.CurrentTeam >= _teamLimits.Count)
            return -1;
        _teamMembers.Add(member);
        _teamMemberCounts[member.CurrentTeam]++;
        return _teamMembers.Count - 1;
    }

    // RemoveTeamMember (TeamBalancer.cpp:591-596).
    private void RemoveTeamMember(int index)
    {
        var team = _teamMembers[index].CurrentTeam;
        if (team < _teamMemberCounts.Count)
            _teamMemberCounts[team]--;
        RemoveAtFast(_teamMembers, index);
    }

    // GetMinMaxTeamMembers (TeamBalancer.cpp:597-604).
    private (int Min, int Max) GetMinMaxTeamMembers()
    {
        var min = _teamMembers.Count / _teamLimits.Count;
        return (min, _teamMembers.Count % _teamLimits.Count == 0 ? min : min + 1);
    }

    // EvenTeams (TeamBalancer.cpp:605-630): teams below the minimum take members from teams at the maximum.
    private void EvenTeams()
    {
        if (_teamLimits.Count == 0)
            return;
        var (min, max) = GetMinMaxTeamMembers();
        for (var team = 0; team < _teamLimits.Count; team++)
        {
            while (_teamMemberCounts[team] < min && _teamMemberCounts[team] < _teamLimits[team])
            {
                var overpopulated = GetOverpopulatedTeams(max);
                var index = GetMemberIndexToSwitchTeams(overpopulated, (byte)team);
                if (index < 0)
                    break;
                SwitchMemberTeam(index, (byte)team);
                NotifyTeamAssignment(index);
            }
        }
    }

    // GetMemberIndexToSwitchTeams (TeamBalancer.cpp:631-663): at random, preferring members that asked for the target.
    private int GetMemberIndexToSwitchTeams(List<byte> sourceTeams, byte targetTeam)
    {
        List<int> preferred = [];
        List<int> potential = [];
        foreach (var source in sourceTeams)
        {
            for (var i = 0; i < _teamMembers.Count; i++)
            {
                if (_teamMembers[i].CurrentTeam != source)
                    continue;
                if (_teamMembers[i].RequestedTeam == targetTeam)
                    preferred.Add(i);
                else
                    potential.Add(i);
            }
        }
        if (preferred.Count > 0)
            return preferred[RandomNumberGenerator.GetInt32(preferred.Count)];
        if (potential.Count > 0)
            return potential[RandomNumberGenerator.GetInt32(potential.Count)];
        return -1;
    }

    // SwitchMemberTeam (TeamBalancer.cpp:664-671).
    private void SwitchMemberTeam(int index, byte destination)
    {
        var member = _teamMembers[index];
        if (member.CurrentTeam < _teamMemberCounts.Count)
            _teamMemberCounts[member.CurrentTeam]--;
        _teamMemberCounts[destination]++;
        member.CurrentTeam = destination;
        if (member.RequestedTeam == destination)
            member.RequestedTeam = UnassignedTeamId;
    }

    // GetOverpopulatedTeams (TeamBalancer.cpp:672-680).
    private List<byte> GetOverpopulatedTeams(int maxTeamSize)
    {
        List<byte> teams = [];
        for (var i = 0; i < _teamMemberCounts.Count; i++)
        {
            if (_teamMemberCounts[i] >= maxTeamSize)
                teams.Add((byte)i);
        }
        return teams;
    }

    // NotifyTeamAssigment (TeamBalancer.cpp:681-692): to the member's system only.
    private void NotifyTeamAssignment(int index)
    {
        if (index >= _teamMembers.Count)
            return;
        var member = _teamMembers[index];
        var message = new byte[10];
        message[0] = (byte)MessageId.TeamBalancerTeamAssigned;
        message[1] = member.CurrentTeam;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2), member.MemberId);
        Send(message, member.MemberGuid);
    }

    private bool WeAreHost() => _host is { } host && _hostGuid == host.RakNetGuid;

    // GetNextDefaultTeam (TeamBalancer.cpp:744-765).
    private byte GetNextDefaultTeam() => _defaultAssignmentAlgorithm == DefaultAssignmentAlgorithm.FillInOrder
        ? GetFirstNonFullTeam()
        : GetSmallestNonFullTeam();

    // TeamWouldBeOverpopulatedOnAddition (TeamBalancer.cpp:766-781).
    private bool TeamWouldBeOverpopulatedOnAddition(byte team, int teamMemberSize)
    {
        if (team >= _teamLimits.Count)
            return true;
        if (_teamMemberCounts[team] >= _teamLimits[team])
            return true;
        if (_forceTeamsToBeEven)
            return _teamMemberCounts[team] >= (teamMemberSize / _teamLimits.Count) + 1;
        return false;
    }

    // TeamWouldBeUnderpopulatedOnLeave (TeamBalancer.cpp:782-790), in RakNet's unsigned arithmetic.
    private bool TeamWouldBeUnderpopulatedOnLeave(byte team, int teamMemberSize)
    {
        if (!_forceTeamsToBeEven)
            return false;
        var min = unchecked((uint)(teamMemberSize - 1)) / (uint)_teamLimits.Count;
        return team < _teamLimits.Count && _teamMemberCounts[team] <= min;
    }

    // GetSmallestNonFullTeam (TeamBalancer.cpp:791-805).
    private byte GetSmallestNonFullTeam()
    {
        var smallest = int.MaxValue;
        var index = UnassignedTeamId;
        for (var i = 0; i < _teamLimits.Count && i < UnassignedTeamId; i++)
        {
            if (_teamMemberCounts[i] < smallest && _teamMemberCounts[i] < _teamLimits[i])
            {
                smallest = _teamMemberCounts[i];
                index = (byte)i;
            }
        }
        return index;
    }

    // GetFirstNonFullTeam (TeamBalancer.cpp:806-817).
    private byte GetFirstNonFullTeam()
    {
        for (var i = 0; i < _teamLimits.Count && i < UnassignedTeamId; i++)
        {
            if (_teamMemberCounts[i] < _teamLimits[i])
                return (byte)i;
        }
        return UnassignedTeamId;
    }

    // MoveMemberThatWantsToJoinTeam (TeamBalancer.cpp:818-846): a member waiting for the team, picked at random, moves
    // in, and the team it left is offered in turn.
    private void MoveMemberThatWantsToJoinTeam(byte team)
    {
        do
        {
            List<int> waiting = [];
            for (var i = 0; i < _teamMembers.Count; i++)
            {
                if (_teamMembers[i].RequestedTeam == team)
                    waiting.Add(i);
            }
            if (waiting.Count == 0)
                return;
            var index = waiting[RandomNumberGenerator.GetInt32(waiting.Count)];
            var oldTeam = _teamMembers[index].CurrentTeam;
            SwitchMemberTeam(index, team);
            NotifyTeamAssignment(index);
            team = oldTeam;
        }
        while (team != UnassignedTeamId);
    }

    // NotifyTeamsLocked (TeamBalancer.cpp:847-853).
    private void NotifyTeamsLocked(RakNetGuid target, byte requestedTeam) =>
        Send([(byte)MessageId.TeamBalancerRequestedTeamLocked, requestedTeam], target);

    // NotifyTeamSwitchPending (TeamBalancer.cpp:854-861).
    private void NotifyTeamSwitchPending(RakNetGuid target, byte requestedTeam, ulong memberId)
    {
        var message = new byte[10];
        message[0] = (byte)MessageId.TeamBalancerRequestedTeamFull;
        message[1] = requestedTeam;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2), memberId);
        Send(message, target);
    }

    // SwapTeamMembersByRequest (TeamBalancer.cpp:862-869).
    private void SwapTeamMembersByRequest(int first, int second)
    {
        (_teamMembers[first].CurrentTeam, _teamMembers[second].CurrentTeam) = (_teamMembers[second].CurrentTeam, _teamMembers[first].CurrentTeam);
        _teamMembers[first].RequestedTeam = UnassignedTeamId;
        _teamMembers[second].RequestedTeam = UnassignedTeamId;
    }

    // NotifyNoTeam (TeamBalancer.cpp:870-877).
    private void NotifyNoTeam(ulong memberId, RakNetGuid target)
    {
        var message = new byte[10];
        message[0] = (byte)MessageId.TeamBalancerTeamAssigned;
        message[1] = UnassignedTeamId;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2), memberId);
        Send(message, target);
    }

    // TeamsWouldBeEvenOnSwitch (TeamBalancer.cpp:878-883).
    private bool TeamsWouldBeEvenOnSwitch(byte joining, byte leaving) =>
        !TeamWouldBeOverpopulatedOnAddition(joining, _teamMembers.Count - 1) && !TeamWouldBeUnderpopulatedOnLeave(leaving, _teamMembers.Count - 1);

    private static void Header(byte[] message, Operation operation, ulong memberId)
    {
        message[0] = (byte)MessageId.TeamBalancerInternal;
        message[1] = (byte)operation;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(message.AsSpan(2), memberId);
    }

    private void SendToHost(byte[] message) => Send(message, _hostGuid);

    // rakPeerInterface->Send(..., HIGH_PRIORITY, RELIABLE_ORDERED, 0, target, false): nothing for an unassigned target.
    private void Send(byte[] message, RakNetGuid target)
    {
        if (_host is { } host && target.IsAssigned)
            host.Send(message, target, Reliability.ReliableOrdered, Priority.High, 0);
    }

    // DataStructures::List::Replace: the list grows to position + 1, the gap filled with filler.
    private static void Replace(List<ushort> list, ushort input, ushort filler, int position)
    {
        if (position < list.Count)
        {
            list[position] = input;
            return;
        }
        while (list.Count < position)
            list.Add(filler);
        list.Add(input);
    }

    // DataStructures::List::RemoveAtIndexFast: the last moves into the gap.
    private static void RemoveAtFast<T>(List<T> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    // TeamBalancer::MyTeamMembers
    private sealed class OwnMember(ulong memberId, byte currentTeam, byte requestedTeam)
    {
        public ulong MemberId { get; } = memberId;

        public byte CurrentTeam { get; set; } = currentTeam;

        public byte RequestedTeam { get; set; } = requestedTeam;
    }

    // TeamBalancer::TeamMember
    private sealed class HostMember(RakNetGuid memberGuid, ulong memberId, byte currentTeam, byte requestedTeam)
    {
        public RakNetGuid MemberGuid { get; } = memberGuid;

        public ulong MemberId { get; } = memberId;

        public byte CurrentTeam { get; set; } = currentTeam;

        public byte RequestedTeam { get; set; } = requestedTeam;
    }
}
