using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// TeamManager: teams and team members networked across a peer-to-peer mesh or from a server, with join rules, member
/// limits, team switches and balancing, resolved by one host per world.
/// </summary>
/// <remarks>
/// <para>
/// Ported from TeamManager.cpp. Make a <see cref="TeamWorld"/> with <see cref="AddWorld"/>, register the game's
/// <see cref="Team"/>s and <see cref="TeamMember"/>s in it, replicate them to new systems with their
/// SerializeConstruction and DeserializeConstruction, and operate on the members. Changes travel as
/// ID_TEAM_BALANCER_INTERNAL followed by an operation byte and the world ID, which are consumed; the application sees
/// ID_TEAM_BALANCER_TEAM_ASSIGNED (world ID, member NetworkID, a 16-bit team count and their NetworkIDs, the no-team
/// subcategory byte and the 32-bit JoinTeamType), ID_TEAM_BALANCER_REQUESTED_TEAM_FULL and _LOCKED (read with
/// <see cref="TryDecomposeTeamFull"/>) and ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED. What a system decides itself it
/// injects at the head of its own stream, from an unassigned address and GUID. Everything is sent HIGH_PRIORITY and
/// RELIABLE_ORDERED on channel 0; NetworkIDs are 64-bit and multi-byte values big-endian.
/// </para>
/// <para>
/// With <see cref="TeamManagerTopology.PeerToPeer"/> each world's host follows ID_FCM2_NEW_HOST from
/// FullyConnectedMesh2. RakNet's behaviour is kept where it surprises: a request the host makes of itself is answered
/// without ID_TEAM_BALANCER_REQUESTED_TEAM_FULL or _LOCKED here (TeamManager.cpp:2440-2441); a locally made
/// ID_TEAM_BALANCER_TEAM_ASSIGNED is read again as it passes, saving the member's last teams once more; and balancing
/// applied while a team is referenced runs on any system, not only the host. Deliberate differences: a truncated message
/// is ignored where RakNet reads uninitialised values; a request for any team that no team allows is refused with
/// ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED naming team UNASSIGNED_NETWORK_ID with zero counts, where RakNet dereferences a
/// null team (TeamManager.cpp:2436, 1935); a deserialised request naming no team to join is dropped; and
/// ID_RUN_UpdateTeamsRequestedToNoneAndAddTeam for a team the member is already on does not add it a second time, where
/// RakNet lists the member on the team again with each repeat.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step. RakNet's TeamBalancer and this plugin cannot be attached together.</para>
/// </remarks>
public sealed class TeamManager : IRakPlugin
{
    private const ulong UnassignedNetworkId = ulong.MaxValue;

    // worldsArray[255] in RakNet: world IDs 0 to 254.
    private readonly TeamWorld?[] _worldsById = new TeamWorld?[255];
    private readonly List<TeamWorld> _worlds = [];
    private IPluginHost? _host;
    private bool _autoAddParticipants = true;
    private TeamManagerTopology _topology = TeamManagerTopology.PeerToPeer;

    // TeamManagerOperations: the byte after ID_TEAM_BALANCER_INTERNAL.
    internal enum Operation : byte
    {
        UpdateListsToNoTeam,
        UpdateTeamsRequestedToAny,
        JoinAnyTeam,
        JoinRequestedTeam,
        UpdateTeamsRequestedToNoneAndAddTeam,
        RemoveFromTeamsRequestedAndAddTeam,
        AddToRequestedTeams,
        LeaveTeam,
        SetMemberLimit,
        SetJoinPermissions,
        SetBalanceTeams,
        SetBalanceTeamsInitial,
        SerializeWorld,
    }

    /// <summary>GetWorldCount</summary>
    public int WorldCount
    {
        get
        {
            lock (Gate)
                return _worlds.Count;
        }
    }

    /// <summary>
    /// SetAutoManageConnections: whether every world adds each new connection as a participant. True by default; turn it
    /// off with several worlds, or to add participants once the host is known.
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
            {
                _autoAddParticipants = value;
                foreach (var world in _worlds)
                    world.AutoManageConnections = value;
            }
        }
    }

    /// <summary>SetTopology: whether the host relays operations between participants. Peer to peer by default.</summary>
    public TeamManagerTopology Topology
    {
        get
        {
            lock (Gate)
                return _topology;
        }
        set
        {
            lock (Gate)
                _topology = value;
        }
    }

    internal Lock Gate { get; } = new();

    // GetMyGUIDUnified
    internal RakNetGuid MyGuid => _host?.RakNetGuid ?? RakNetGuid.Unassigned;

    /// <summary>AddWorld: a world with the given ID, 0 to 254, whose host is this system.</summary>
    public TeamWorld AddWorld(byte worldId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(worldId, byte.MaxValue);
        lock (Gate)
        {
            if (_worldsById[worldId] is not null)
                throw new InvalidOperationException($"World {worldId} is already in use.");
            var world = new TeamWorld(this, worldId, MyGuid);
            _worldsById[worldId] = world;
            _worlds.Add(world);
            return world;
        }
    }

    /// <summary>RemoveWorld: forgets the world; the last world in <see cref="GetWorldAtIndex"/> order takes its index.</summary>
    public void RemoveWorld(byte worldId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(worldId, byte.MaxValue);
        lock (Gate)
        {
            var world = _worldsById[worldId] ?? throw new InvalidOperationException($"World {worldId} is not in use.");
            var index = _worlds.IndexOf(world);
            world.Clear();
            _worlds[index] = _worlds[^1];
            _worlds.RemoveAt(_worlds.Count - 1);
            _worldsById[worldId] = null;
        }
    }

    /// <summary>GetWorldAtIndex</summary>
    public TeamWorld GetWorldAtIndex(int index)
    {
        lock (Gate)
            return _worlds[index];
    }

    /// <summary>GetWorldWithId: null when there is no such world.</summary>
    public TeamWorld? GetWorldWithId(byte worldId)
    {
        lock (Gate)
            return worldId < _worldsById.Length ? _worldsById[worldId] : null;
    }

    /// <summary>Clear: forgets every world, which leaves their teams and members in no world.</summary>
    public void Clear()
    {
        lock (Gate)
        {
            foreach (var world in _worlds)
            {
                _worldsById[world.WorldId] = null;
                world.Clear();
            }
            _worlds.Clear();
        }
    }

    /// <summary>
    /// DecodeTeamAssigned: the world and member an ID_TEAM_BALANCER_TEAM_ASSIGNED is about; null for either this system
    /// does not know. The member's teams are already updated when the application reads the message.
    /// </summary>
    public void DecodeTeamAssigned(ReadOnlySpan<byte> data, out TeamWorld? world, out TeamMember? teamMember)
    {
        world = null;
        teamMember = null;
        var reader = new BitStreamReader(data);
        if (!reader.IgnoreBytes(1) || !reader.TryReadByte(out var worldId))
            return;
        lock (Gate)
        {
            world = GetWorldWithId(worldId);
            if (world is not null && reader.TryReadUInt64(out var memberId))
                teamMember = world.FindMember(memberId);
        }
    }

    /// <summary>
    /// DecodeTeamCancelled: the world and member an ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED is about, and the team no
    /// longer waited for, null for all of them; null for anything this system does not know.
    /// </summary>
    public void DecodeTeamCancelled(ReadOnlySpan<byte> data, out TeamWorld? world, out TeamMember? teamMember, out Team? teamCancelled)
    {
        world = null;
        teamMember = null;
        teamCancelled = null;
        var reader = new BitStreamReader(data);
        if (!reader.IgnoreBytes(1) || !reader.TryReadByte(out var worldId))
            return;
        lock (Gate)
        {
            world = GetWorldWithId(worldId);
            if (!reader.TryReadUInt64(out var memberId))
                return;
            teamMember = world?.FindMember(memberId);
            if (reader.TryReadBool(out var specific) && specific && reader.TryReadUInt64(out var teamId))
                teamCancelled = world?.FindTeam(teamId);
        }
    }

    /// <summary>DecomposeTeamFull: reads an ID_TEAM_BALANCER_REQUESTED_TEAM_FULL. False when it is truncated.</summary>
    public bool TryDecomposeTeamFull(ReadOnlySpan<byte> data, out TeamRequestRefusal? refusal) => TryDecomposeTeamFullOrLocked(data, out refusal);

    /// <summary>DecomposeTeamLocked: reads an ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED. False when it is truncated.</summary>
    public bool TryDecomposeTeamLocked(ReadOnlySpan<byte> data, out TeamRequestRefusal? refusal) => TryDecomposeTeamFullOrLocked(data, out refusal);

    void IRakPlugin.OnAttached(IPluginHost host)
    {
        lock (Gate)
            _host = host;
    }

    void IRakPlugin.OnDetached()
    {
        lock (Gate)
            _host = null;
    }

    /// <summary>OnClosedConnection (TeamManager.cpp:2261-2267).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (Gate)
        {
            foreach (var world in _worlds)
                world.OnClosedConnection(connection.RakNetGuid);
        }
    }

    /// <summary>OnNewConnection (TeamManager.cpp:2271-2277).</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        lock (Gate)
        {
            foreach (var world in _worlds)
                world.OnNewConnection(connection.RakNetGuid);
        }
    }

    /// <summary>OnReceive (TeamManager.cpp:2167-2257).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        lock (Gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.Fcm2NewHost:
                    foreach (var world in _worlds)
                        world.SetHost(e.RakNetGuid);
                    return PluginResult.Continue;
                case MessageId.TeamBalancerTeamAssigned:
                    {
                        var reader = new BitStreamReader(data);
                        reader.IgnoreBytes(1);
                        ProcessTeamAssigned(ref reader);
                        return PluginResult.Continue;
                    }
                case MessageId.TeamBalancerTeamRequestedCancelled:
                    {
                        if (data.Length < 2 || GetWorldWithId(data[1]) is not { } world)
                            return PluginResult.Consume;
                        return OnRemoveFromRequestedTeams(data, e.RakNetGuid, world) ? PluginResult.Continue : PluginResult.Consume;
                    }
                case MessageId.TeamBalancerInternal:
                    {
                        if (data.Length >= 3 && GetWorldWithId(data[2]) is { } world)
                            OnInternal(data, e.RakNetGuid, world);
                        return PluginResult.Consume;
                    }
                default:
                    return PluginResult.Continue;
            }
        }
    }

    internal static BitStream Internal(Operation operation, byte worldId)
    {
        var message = new BitStream(64);
        message.WriteByte((byte)MessageId.TeamBalancerInternal);
        message.WriteByte((byte)operation);
        message.WriteByte(worldId);
        return message;
    }

    // EncodeTeamAssigned (TeamManager.cpp:1984-1995).
    internal static void EncodeTeamAssigned(BitStream message, TeamMember member)
    {
        message.WriteByte(member.WorldRef!.WorldId);
        message.WriteUInt64(member.Id);
        message.WriteUInt16(unchecked((ushort)member.Teams.Count));
        foreach (var team in member.Teams)
            message.WriteUInt64(team.Id);
        message.WriteByte(member.NoTeamSubcategory);
        // BitStream::Write of the enum: its 32 bits, big-endian.
        message.WriteUInt32((uint)member.JoinType);
    }

    internal ulong Now() => _host?.GetTime() ?? 0;

    // SendUnified to one system, HIGH_PRIORITY and RELIABLE_ORDERED on channel 0; an unassigned target sends nothing
    // (RakPeer.cpp:1323-1324), as does a detached plugin.
    internal void SendTo(OutgoingMessage message, RakNetGuid target)
    {
        if (_host is { } host && target.IsAssigned)
            host.Send(message, target, Reliability.ReliableOrdered, Priority.High, 0);
    }

    internal void SendTo(BitStream message, RakNetGuid target) => SendTo((OutgoingMessage)message, target);

    // PushTeamAssigned (TeamManager.cpp:2310-2318).
    internal void PushTeamAssigned(TeamMember member)
    {
        using var message = new BitStream(64);
        message.WriteByte((byte)MessageId.TeamBalancerTeamAssigned);
        EncodeTeamAssigned(message, member);
        PushBitStream(message);
    }

    // PushBitStream (TeamManager.cpp:2322-2331): at the head, from nobody, generated locally.
    internal void PushBitStream(BitStream message) =>
        _host?.Inject(message.Written, SystemAddress.Unassigned, RakNetGuid.Unassigned, atHead: true, generatedLocally: true);

    // RemoveFromTeamsRequestedAndAddTeam (TeamManager.cpp:2288-2306).
    internal void RemoveFromTeamsRequestedAndAddTeam(TeamMember member, Team team, bool isTeamSwitch, Team? teamToLeave)
    {
        member.RemoveFromRequestedTeams(team);
        if (isTeamSwitch)
        {
            if (teamToLeave is null)
                member.RemoveFromAllTeamsInternal();
            else
                member.RemoveFromSpecificTeamInternal(teamToLeave);
        }
        member.AddToTeamList(team);
        PushTeamAssigned(member);
    }

    // ProcessTeamAssigned and DecodeTeamAssigned (TeamManager.cpp:1999-2027, 2088-2144): the member leaves the teams no
    // longer listed and joins the new ones. False when truncated.
    internal bool ProcessTeamAssigned(ref BitStreamReader reader)
    {
        if (!reader.TryReadByte(out var worldId) || !reader.TryReadUInt64(out var memberId))
            return false;
        if (GetWorldWithId(worldId) is not { } world)
            return true;
        var member = world.FindMember(memberId);
        if (!reader.TryReadUInt16(out var count))
            return false;
        var newTeams = new List<Team>(count);
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadUInt64(out var teamId))
                return false;
            if (world.FindTeam(teamId) is { } team)
                newTeams.Add(team);
        }
        if (!reader.TryReadByte(out var noTeamId) || !reader.TryReadUInt32(out var joinTeamType))
            return false;
        // RakNet dereferences a member it does not know when teams are listed (TeamManager.cpp:2133); here it is skipped.
        if (member is null)
            return true;
        var left = member.Teams.Where(t => !newTeams.Contains(t)).ToList();
        var joined = newTeams.Where(t => !member.Teams.Contains(t)).ToList();
        member.StoreLastTeams();
        foreach (var team in left)
            member.RemoveFromSpecificTeamInternal(team);
        foreach (var team in joined)
        {
            if (!member.IsOnTeamInternal(team))
            {
                member.RemoveFromRequestedTeams(team);
                member.AddToTeamList(team);
            }
        }
        member.NoTeamSubcategory = noTeamId;
        member.JoinType = (JoinTeamType)joinTeamType;
        return true;
    }

    private bool TryDecomposeTeamFullOrLocked(ReadOnlySpan<byte> data, out TeamRequestRefusal? refusal)
    {
        refusal = null;
        var reader = new BitStreamReader(data);
        if (!reader.IgnoreBytes(1) || !reader.TryReadByte(out var worldId) || !reader.TryReadUInt64(out var memberId)
            || !reader.TryReadUInt64(out var teamId) || !reader.TryReadUInt16(out var currentMembers)
            || !reader.TryReadUInt16(out var limit) || !reader.TryReadBool(out var balancing) || !reader.TryReadByte(out var permissions))
            return false;
        lock (Gate)
        {
            var world = GetWorldWithId(worldId);
            refusal = new TeamRequestRefusal(worldId, memberId, teamId, world, world?.FindMember(memberId), world?.FindTeam(teamId),
                currentMembers, limit, balancing, (JoinPermissions)permissions);
            return true;
        }
    }

    // EncodeTeamFull / EncodeTeamLocked and EncodeTeamFullOrLocked (TeamManager.cpp:1904-1940).
    private static BitStream EncodeTeamFullOrLocked(MessageId id, TeamMember member, Team? team)
    {
        var message = new BitStream(64);
        message.WriteByte((byte)id);
        message.WriteByte(member.WorldRef!.WorldId);
        message.WriteUInt64(member.Id);
        message.WriteUInt64(team?.Id ?? UnassignedNetworkId);
        message.WriteUInt16(unchecked((ushort)(team?.Members.Count ?? 0)));
        message.WriteUInt16(team?.GetMemberLimit() ?? 0);
        message.WriteBool(team?.Balancing ?? false);
        message.WriteByte((byte)(team?.Permissions ?? JoinPermissions.None));
        return message;
    }

    private void OnInternal(ReadOnlySpan<byte> data, RakNetGuid sender, TeamWorld world)
    {
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(3);
        switch ((Operation)data[1])
        {
            case Operation.UpdateListsToNoTeam:
                OnUpdateListsToNoTeam(data, ref reader, sender, world);
                break;
            case Operation.UpdateTeamsRequestedToAny:
                if (reader.TryReadUInt64(out var anyId))
                    world.FindMember(anyId)?.UpdateTeamsRequestedToAny();
                break;
            case Operation.JoinAnyTeam:
                OnJoinAnyTeam(ref reader, sender, world);
                break;
            case Operation.JoinRequestedTeam:
                OnJoinRequestedTeam(ref reader, sender, world);
                break;
            case Operation.UpdateTeamsRequestedToNoneAndAddTeam:
                OnUpdateTeamsRequestedToNoneAndAddTeam(ref reader, world);
                break;
            case Operation.RemoveFromTeamsRequestedAndAddTeam:
                OnRemoveFromTeamsRequestedAndAddTeam(ref reader, world);
                break;
            case Operation.AddToRequestedTeams:
                OnAddToRequestedTeams(ref reader, world);
                break;
            case Operation.LeaveTeam:
                OnLeaveTeam(data, ref reader, sender, world);
                break;
            case Operation.SetMemberLimit:
                OnSetMemberLimit(data, ref reader, sender, world);
                break;
            case Operation.SetJoinPermissions:
                OnSetJoinPermissions(data, ref reader, sender, world);
                break;
            case Operation.SetBalanceTeams:
                OnSetBalanceTeams(data, ref reader, sender, world);
                break;
            case Operation.SetBalanceTeamsInitial:
                if (reader.TryReadBool(out var initial))
                    world.BalanceTeamsActive = initial;
                break;
        }
    }

    // A message the host relays as it came, to every participant but its sender.
    private static void Relay(TeamWorld world, ReadOnlySpan<byte> data, RakNetGuid sender) => world.BroadcastToParticipants(data, sender);

    // OnUpdateListsToNoTeam (TeamManager.cpp:2335-2362).
    private void OnUpdateListsToNoTeam(ReadOnlySpan<byte> data, ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadByte(out var noTeamId))
            return;
        if (world.FindMember(memberId) is not { } member)
            return;
        member.StoreLastTeams();
        member.UpdateListsToNoTeam(noTeamId);
        PushTeamAssigned(member);
        if (world.IsHostHere)
        {
            world.FillRequestedSlots();
            world.EnforceTeamBalance(noTeamId);
            if (_topology == TeamManagerTopology.ClientServer)
                Relay(world, data, sender);
        }
    }

    // OnJoinAnyTeam (TeamManager.cpp:2381-2444): on the host.
    private void OnJoinAnyTeam(ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || world.FindMember(memberId) is not { } member)
            return;
        member.UpdateTeamsRequestedToAny();
        var team = world.JoinAnyTeam(member, out var resultCode);
        if (resultCode == 1)
        {
            using (var message = Internal(Operation.UpdateTeamsRequestedToNoneAndAddTeam, world.WorldId))
            {
                message.WriteUInt64(memberId);
                message.WriteUInt64(team!.Id);
                world.BroadcastToParticipants(message, sender);
            }
            SendAssignedTo(member, sender);
            return;
        }
        using (var message = Internal(Operation.UpdateTeamsRequestedToAny, world.WorldId))
        {
            message.WriteUInt64(memberId);
            world.BroadcastToParticipants(message, sender);
        }
        using var refusal = EncodeTeamFullOrLocked(
            resultCode == -2 ? MessageId.TeamBalancerRequestedTeamFull : MessageId.TeamBalancerRequestedTeamLocked, member, team);
        world.BroadcastToParticipants(refusal, RakNetGuid.Unassigned);
        if (sender != MyGuid)
            PushBitStream(refusal);
    }

    // OnJoinRequestedTeam (TeamManager.cpp:2448-2572): on the host.
    private void OnJoinRequestedTeam(ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadUInt64(out var teamToJoinId)
            || !TryReadSwitch(ref reader, world, out var isTeamSwitch, out var switchSpecificTeam, out var teamToLeaveId, out var teamToLeave))
            return;
        var member = world.FindMember(memberId);
        var teamToJoin = world.FindTeam(teamToJoinId);
        if (teamToJoin is null || member is null)
            return;
        var fromHere = sender == MyGuid;
        if (isTeamSwitch)
        {
            if (!member.SwitchSpecificTeamCheck(teamToJoin, teamToLeave, fromHere))
                return;
            member.AddToRequestedTeams(teamToJoin, teamToLeave);
        }
        else
        {
            if (!member.JoinSpecificTeamCheck(teamToJoin, fromHere))
                return;
            member.AddToRequestedTeams(teamToJoin);
        }
        List<Team> leaving = [];
        if (isTeamSwitch)
        {
            if (teamToLeave is null)
                leaving.AddRange(member.Teams);
            else if (member.IsOnTeamInternal(teamToLeave))
                leaving.Add(teamToLeave);
            if (leaving.Count == 0)
                isTeamSwitch = false;
        }
        var resultCode = world.JoinSpecificTeam(member, teamToJoin, isTeamSwitch, teamToLeave, leaving);
        var operation = resultCode == 1 ? Operation.RemoveFromTeamsRequestedAndAddTeam : Operation.AddToRequestedTeams;
        using (var message = Internal(operation, world.WorldId))
        {
            message.WriteUInt64(memberId);
            message.WriteUInt64(teamToJoin.Id);
            message.WriteBool(isTeamSwitch);
            if (isTeamSwitch)
            {
                message.WriteBool(switchSpecificTeam);
                if (switchSpecificTeam)
                    message.WriteUInt64(teamToLeaveId);
            }
            world.BroadcastToParticipants(message, sender);
        }
        if (resultCode == 1)
        {
            SendAssignedTo(member, sender);
            return;
        }
        using var refusal = EncodeTeamFullOrLocked(
            resultCode == -2 ? MessageId.TeamBalancerRequestedTeamFull : MessageId.TeamBalancerRequestedTeamLocked, member, teamToJoin);
        world.BroadcastToParticipants(refusal, RakNetGuid.Unassigned);
        if (!fromHere)
            PushBitStream(refusal);
    }

    // The requester is told its new teams directly, unless it is this system (TeamManager.cpp:2410-2417).
    private void SendAssignedTo(TeamMember member, RakNetGuid sender)
    {
        if (sender == MyGuid)
            return;
        using var assigned = new BitStream(64);
        assigned.WriteByte((byte)MessageId.TeamBalancerTeamAssigned);
        EncodeTeamAssigned(assigned, member);
        SendTo(assigned, sender);
    }

    // The switch fields of JoinRequestedTeam and AddToRequestedTeams: a switch naming a team this system does not know is
    // not a switch (TeamManager.cpp:2462-2473).
    private static bool TryReadSwitch(ref BitStreamReader reader, TeamWorld world, out bool isTeamSwitch, out bool switchSpecificTeam,
                                      out ulong teamToLeaveId, out Team? teamToLeave)
    {
        switchSpecificTeam = false;
        teamToLeaveId = UnassignedNetworkId;
        teamToLeave = null;
        if (!reader.TryReadBool(out isTeamSwitch))
            return false;
        if (!isTeamSwitch)
            return true;
        if (!reader.TryReadBool(out switchSpecificTeam))
            return false;
        if (!switchSpecificTeam)
            return true;
        if (!reader.TryReadUInt64(out teamToLeaveId))
            return false;
        teamToLeave = world.FindTeam(teamToLeaveId);
        if (teamToLeave is null)
            isTeamSwitch = false;
        return true;
    }

    // OnUpdateTeamsRequestedToNoneAndAddTeam (TeamManager.cpp:2576-2594).
    private void OnUpdateTeamsRequestedToNoneAndAddTeam(ref BitStreamReader reader, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadUInt64(out var teamId))
            return;
        if (world.FindMember(memberId) is not { } member || world.FindTeam(teamId) is not { } team)
            return;
        member.StoreLastTeams();
        member.UpdateTeamsRequestedToNone();
        // RakNet adds the team again when the member is already on it (TeamManager.cpp:2591), so each repeat of this
        // message would grow both lists; here a member is on a team once.
        if (!member.IsOnTeamInternal(team))
            member.AddToTeamList(team);
        PushTeamAssigned(member);
    }

    // OnRemoveFromTeamsRequestedAndAddTeam (TeamManager.cpp:2598-2631): unlike the host's own messages, an unknown team to
    // leave still counts as a switch here, leaving nothing.
    private void OnRemoveFromTeamsRequestedAndAddTeam(ref BitStreamReader reader, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadUInt64(out var teamId) || !reader.TryReadBool(out var isTeamSwitch))
            return;
        var switchSpecificTeam = false;
        Team? teamToLeave = null;
        if (isTeamSwitch)
        {
            if (!reader.TryReadBool(out switchSpecificTeam))
                return;
            if (switchSpecificTeam)
            {
                if (!reader.TryReadUInt64(out var leaveId))
                    return;
                teamToLeave = world.FindTeam(leaveId);
            }
        }
        if (world.FindMember(memberId) is not { } member || world.FindTeam(teamId) is not { } team)
            return;
        member.StoreLastTeams();
        if (teamToLeave is not null)
            member.RemoveFromSpecificTeamInternal(teamToLeave);
        else if (isTeamSwitch && !switchSpecificTeam)
            member.RemoveFromAllTeamsInternal();
        RemoveFromTeamsRequestedAndAddTeam(member, team, isTeamSwitch: false, null);
    }

    // OnAddToRequestedTeams (TeamManager.cpp:2635-2670).
    private static void OnAddToRequestedTeams(ref BitStreamReader reader, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadUInt64(out var teamId)
            || !TryReadSwitch(ref reader, world, out var isTeamSwitch, out _, out _, out var teamToLeave))
            return;
        if (world.FindMember(memberId) is not { } member || world.FindTeam(teamId) is not { } team)
            return;
        if (isTeamSwitch)
            member.AddToRequestedTeams(team, teamToLeave);
        else
            member.AddToRequestedTeams(team);
    }

    // OnRemoveFromRequestedTeams (TeamManager.cpp:2674-2712): false, and the message is consumed, when the team or member
    // is unknown.
    private bool OnRemoveFromRequestedTeams(ReadOnlySpan<byte> data, RakNetGuid sender, TeamWorld world)
    {
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(2);
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadBool(out var hasSpecificTeam))
            return false;
        Team? team = null;
        if (hasSpecificTeam)
        {
            if (!reader.TryReadUInt64(out var teamId))
                return false;
            team = world.FindTeam(teamId);
            if (team is null)
                return false;
        }
        if (world.FindMember(memberId) is not { } member)
            return false;
        member.RemoveFromRequestedTeams(team);
        if (world.IsHostHere && _topology == TeamManagerTopology.ClientServer)
            Relay(world, data, sender);
        return true;
    }

    // OnLeaveTeam (TeamManager.cpp:2716-2754).
    private void OnLeaveTeam(ReadOnlySpan<byte> data, ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var memberId) || !reader.TryReadUInt64(out var teamId) || !reader.TryReadByte(out var noTeamId))
            return;
        if (world.FindMember(memberId) is not { } member || world.FindTeam(teamId) is not { } team)
            return;
        if (!member.IsOnTeamInternal(team))
            return;
        member.StoreLastTeams();
        member.RemoveFromSpecificTeamInternal(team);
        if (member.Teams.Count == 0)
        {
            member.NoTeamSubcategory = noTeamId;
            member.JoinType = JoinTeamType.NoTeam;
        }
        PushTeamAssigned(member);
        if (world.IsHostHere)
        {
            world.FillRequestedSlots();
            world.EnforceTeamBalance(noTeamId);
            if (_topology == TeamManagerTopology.ClientServer)
                Relay(world, data, sender);
        }
    }

    // The host passes a setting on: to every participant but itself when it made the change, otherwise to every
    // participant, the sender included (TeamManager.cpp:2776-2779).
    private void PassOnSetting(TeamWorld world, ReadOnlySpan<byte> data, RakNetGuid sender) =>
        world.BroadcastToParticipants(data, sender == MyGuid ? sender : RakNetGuid.Unassigned);

    // OnSetMemberLimit (TeamManager.cpp:2758-2784).
    private void OnSetMemberLimit(ReadOnlySpan<byte> data, ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var teamId) || !reader.TryReadUInt16(out var limit) || !reader.TryReadByte(out var noTeamId))
            return;
        if (world.FindTeam(teamId) is not { } team)
            return;
        team.Limit = limit;
        if (!world.IsHostHere)
            return;
        PassOnSetting(world, data, sender);
        world.FillRequestedSlots();
        world.KickExcessMembers(noTeamId);
    }

    // OnSetJoinPermissions (TeamManager.cpp:2788-2811).
    private void OnSetJoinPermissions(ReadOnlySpan<byte> data, ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadUInt64(out var teamId) || !reader.TryReadByte(out var permissions))
            return;
        if (world.FindTeam(teamId) is not { } team)
            return;
        team.Permissions = (JoinPermissions)permissions;
        if (!world.IsHostHere)
            return;
        PassOnSetting(world, data, sender);
        world.FillRequestedSlots();
    }

    // OnSetBalanceTeams (TeamManager.cpp:2815-2837).
    private void OnSetBalanceTeams(ReadOnlySpan<byte> data, ref BitStreamReader reader, RakNetGuid sender, TeamWorld world)
    {
        if (!reader.TryReadBool(out var balanceTeams) || !reader.TryReadByte(out var noTeamId))
            return;
        world.BalanceTeamsActive = balanceTeams;
        if (!world.IsHostHere)
            return;
        PassOnSetting(world, data, sender);
        if (balanceTeams)
            world.EnforceTeamBalance(noTeamId);
        else
            world.FillRequestedSlots();
    }
}
