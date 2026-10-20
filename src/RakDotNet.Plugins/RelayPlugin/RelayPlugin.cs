using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RelayPlugin: a server relays messages between its clients, which name each other by a string key rather than by
/// address or GUID, and between the members of named groups. The same plugin is the server and the client.
/// </summary>
/// <remarks>
/// <para>
/// Ported from RelayPlugin.cpp. Every message is ID_RELAY_PLUGIN and a <see cref="RelayPluginMessage"/>; strings are
/// RakStrings compressed with the string compressor. A client becomes a participant under a key when the server calls
/// <see cref="AddParticipantOnServer"/>, or when the server accepts requests (<see cref="AcceptAddParticipantRequests"/>)
/// and the client asks with <see cref="AddParticipantRequestFromClient"/>, which is answered with the result and the
/// key. <see cref="SendToParticipant(RakNetGuid, string, BitStream, Priority, Reliability, byte)"/> sends data to the
/// participant with a key, which gets RPE_MESSAGE_TO_CLIENT_FROM_SERVER, the sender's key, alignment to a byte and the
/// data, with the priority, reliability and channel the sender chose; only participants' messages are relayed.
/// Participants join one group at a time; the others in it are told RPE_USER_ENTERED_ROOM and RPE_USER_LEFT_ROOM, the
/// joiner gets RPE_JOIN_GROUP_SUCCESS and the members' keys, and group messages go to everyone else in the group. A
/// participant that disconnects is removed. Requests are HIGH_PRIORITY and RELIABLE_ORDERED on channel 0.
/// </para>
/// <para>
/// RakNet's quirks are kept. The relayed data is every bit after the key up to the end of the last byte, so the
/// padding of the sender's last byte goes with it. A GUID added under a second key loses its first without leaving its
/// group, which keeps listing it. A relayed message whose priority, reliability or channel is out of range is dropped,
/// where RakNet only asserts.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class RelayPlugin : IRakPlugin
{
    private const int CompressedStringHintBytes = 64;

    private readonly Lock _gate = new();

    // strToGuidHash and guidToStrHash: keys are RakStrings, compared as bytes, held here as Latin-1 strings.
    private readonly Dictionary<string, Participant> _strToGuid = new(StringComparer.Ordinal);
    private readonly Dictionary<RakNetGuid, Participant> _guidToStr = [];
    private readonly List<Group> _chatRooms = [];
    private IPluginHost? _host;
    private bool _acceptAddParticipantRequests;

    /// <summary>
    /// SetAcceptAddParticipantRequests: whether a client's <see cref="AddParticipantRequestFromClient"/> is granted as if
    /// the server had called <see cref="AddParticipantOnServer"/>; false by default.
    /// </summary>
    public bool AcceptAddParticipantRequests
    {
        get
        {
            lock (_gate)
                return _acceptAddParticipantRequests;
        }
        set
        {
            lock (_gate)
                _acceptAddParticipantRequests = value;
        }
    }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The RelayPlugin is not attached to a RakPeer.");

    /// <summary>
    /// AddParticipantOnServer: messages for <paramref name="key"/> go to <paramref name="rakNetGuid"/>, which must be
    /// connected. A GUID added before loses its old key.
    /// </summary>
    /// <returns>
    /// <see cref="RelayPluginMessage.AddClientTargetNotConnected"/>, <see cref="RelayPluginMessage.AddClientNameAlreadyInUse"/>
    /// or <see cref="RelayPluginMessage.AddClientSuccess"/>.
    /// </returns>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public RelayPluginMessage AddParticipantOnServer(string key, RakNetGuid rakNetGuid)
    {
        ArgumentNullException.ThrowIfNull(key);
        var peer = Peer;
        lock (_gate)
            return AddParticipant(peer, ToKey(key), rakNetGuid);
    }

    /// <summary>RemoveParticipantOnServer: the participant leaves its group and its key is freed.</summary>
    public void RemoveParticipantOnServer(RakNetGuid rakNetGuid)
    {
        lock (_gate)
            RemoveParticipant(rakNetGuid);
    }

    /// <summary>AddParticipantRequestFromClient: asks the server to add this system under <paramref name="key"/>.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void AddParticipantRequestFromClient(string key, RakNetGuid relayPluginServerGuid)
    {
        ArgumentNullException.ThrowIfNull(key);
        using var stream = Begin(RelayPluginMessage.AddClientRequestFromClient);
        WriteCompressed(stream, ToKey(key));
        SendRequest(stream, relayPluginServerGuid);
    }

    /// <summary>RemoveParticipantRequestFromClient: asks the server to remove this system as a participant.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void RemoveParticipantRequestFromClient(RakNetGuid relayPluginServerGuid)
    {
        using var stream = Begin(RelayPluginMessage.RemoveClientRequestFromClient);
        SendRequest(stream, relayPluginServerGuid);
    }

    /// <summary>
    /// SendToParticipant: asks the server to send every bit of <paramref name="data"/> to the participant with
    /// <paramref name="key"/>, with <paramref name="priority"/>, <paramref name="reliability"/> and
    /// <paramref name="orderingChannel"/>, which are also used to reach the server. Discarded unless this system and
    /// the key are participants.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void SendToParticipant(RakNetGuid relayPluginServerGuid, string key, BitStream data, Priority priority, Reliability reliability, byte orderingChannel)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(data);
        using var stream = Begin(RelayPluginMessage.MessageToServerFromClient);
        stream.WriteByte((byte)priority);
        stream.WriteByte((byte)reliability);
        stream.WriteByte(orderingChannel);
        WriteCompressed(stream, ToKey(key));
        var reader = data.CreateReader();
        stream.Write(ref reader);
        if (relayPluginServerGuid.IsAssigned)
            Peer.Send(stream, relayPluginServerGuid, reliability, priority, orderingChannel);
    }

    /// <summary>SendToParticipant with whole bytes of data.</summary>
    /// <inheritdoc cref="SendToParticipant(RakNetGuid, string, BitStream, Priority, Reliability, byte)"/>
    public void SendToParticipant(RakNetGuid relayPluginServerGuid, string key, ReadOnlySpan<byte> data, Priority priority, Reliability reliability, byte orderingChannel)
    {
        using var stream = new BitStream(data.Length);
        stream.WriteBytes(data);
        SendToParticipant(relayPluginServerGuid, key, stream, priority, reliability, orderingChannel);
    }

    /// <summary>
    /// SendGroupMessage: asks the server to send every bit of <paramref name="data"/> to the others in this system's
    /// group, with the priority, reliability and channel given.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void SendGroupMessage(RakNetGuid relayPluginServerGuid, BitStream data, Priority priority, Reliability reliability, byte orderingChannel)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var stream = Begin(RelayPluginMessage.GroupMessageFromClient);
        stream.WriteByte((byte)priority);
        stream.WriteByte((byte)reliability);
        stream.WriteByte(orderingChannel);
        var reader = data.CreateReader();
        stream.Write(ref reader);
        if (relayPluginServerGuid.IsAssigned)
            Peer.Send(stream, relayPluginServerGuid, reliability, priority, orderingChannel);
    }

    /// <summary>SendGroupMessage with whole bytes of data.</summary>
    /// <inheritdoc cref="SendGroupMessage(RakNetGuid, BitStream, Priority, Reliability, byte)"/>
    public void SendGroupMessage(RakNetGuid relayPluginServerGuid, ReadOnlySpan<byte> data, Priority priority, Reliability reliability, byte orderingChannel)
    {
        using var stream = new BitStream(data.Length);
        stream.WriteBytes(data);
        SendGroupMessage(relayPluginServerGuid, stream, priority, reliability, orderingChannel);
    }

    /// <summary>JoinGroupRequest: asks the server to move this system, a participant, into <paramref name="groupName"/>.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void JoinGroupRequest(RakNetGuid relayPluginServerGuid, string groupName)
    {
        ArgumentNullException.ThrowIfNull(groupName);
        using var stream = Begin(RelayPluginMessage.JoinGroupRequestFromClient);
        WriteCompressed(stream, ToKey(groupName));
        SendRequest(stream, relayPluginServerGuid);
    }

    /// <summary>LeaveGroup: asks the server to take this system out of its group.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void LeaveGroup(RakNetGuid relayPluginServerGuid)
    {
        using var stream = Begin(RelayPluginMessage.LeaveGroupRequestFromClient);
        SendRequest(stream, relayPluginServerGuid);
    }

    /// <summary>GetGroupList: asks the server for its groups and how many are in each.</summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void GetGroupList(RakNetGuid relayPluginServerGuid)
    {
        using var stream = Begin(RelayPluginMessage.GetGroupListRequestFromClient);
        SendRequest(stream, relayPluginServerGuid);
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnClosedConnection: the system is removed as a participant.</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
            RemoveParticipant(connection.RakNetGuid);
    }

    /// <summary>OnReceive (RelayPlugin.cpp:153-246).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.Length < 2 || data[0] != (byte)MessageId.RelayPlugin || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        // BitStream(packet->data, packet->length): whole bytes, the last one's padding included.
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(2);
        lock (_gate)
        {
            switch ((RelayPluginMessage)data[1])
            {
                case RelayPluginMessage.MessageToServerFromClient:
                    OnMessageToServerFromClient(peer, reader, e.RakNetGuid);
                    return PluginResult.Consume;
                case RelayPluginMessage.AddClientRequestFromClient:
                {
                    var key = reader.TryReadCompressedRakStringBytes(out var bytes) ? bytes : [];
                    using var stream = Begin(_acceptAddParticipantRequests ? AddParticipant(peer, Latin1(key), e.RakNetGuid) : RelayPluginMessage.AddClientNotAllowed);
                    WriteCompressed(stream, key);
                    if (e.Address.IsAssigned)
                        peer.Send(stream, e.Address, Reliability.ReliableOrdered, Priority.High, 0);
                    return PluginResult.Consume;
                }
                case RelayPluginMessage.RemoveClientRequestFromClient:
                    RemoveParticipant(e.RakNetGuid);
                    return PluginResult.Consume;
                case RelayPluginMessage.GroupMessageFromClient:
                    OnGroupMessageFromClient(peer, reader, e.RakNetGuid);
                    return PluginResult.Consume;
                case RelayPluginMessage.JoinGroupRequestFromClient:
                    OnJoinGroupRequestFromClient(peer, reader, e.RakNetGuid);
                    return PluginResult.Consume;
                case RelayPluginMessage.LeaveGroupRequestFromClient:
                    if (_guidToStr.TryGetValue(e.RakNetGuid, out var leaver))
                        LeaveGroup(peer, leaver);
                    return PluginResult.Consume;
                case RelayPluginMessage.GetGroupListRequestFromClient:
                    SendChatRoomsList(peer, e.RakNetGuid);
                    return PluginResult.Consume;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    // RPE_MESSAGE_TO_SERVER_FROM_CLIENT (RelayPlugin.cpp:159-190).
    private void OnMessageToServerFromClient(RakPeer peer, BitStreamReader reader, RakNetGuid sender)
    {
        reader.TryReadByte(out var priority);
        reader.TryReadByte(out var reliability);
        reader.TryReadByte(out var orderingChannel);
        var key = reader.TryReadCompressedRakStringBytes(out var bytes) ? bytes : [];
        if (!_strToGuid.TryGetValue(Latin1(key), out var target) || !_guidToStr.TryGetValue(sender, out var from))
            return;
        if (!IsSendable(priority, reliability, orderingChannel))
            return;
        using var stream = Begin(RelayPluginMessage.MessageToClientFromServer);
        WriteCompressed(stream, from.Key);
        stream.AlignToByte();
        stream.Write(ref reader);
        peer.Send(stream, target.Guid, (Reliability)reliability, (Priority)priority, orderingChannel);
    }

    // OnGroupMessageFromClient and SendMessageToRoom (RelayPlugin.cpp:367-397, 412-439).
    private void OnGroupMessageFromClient(RakPeer peer, BitStreamReader reader, RakNetGuid sender)
    {
        reader.TryReadByte(out var priority);
        reader.TryReadByte(out var reliability);
        reader.TryReadByte(out var orderingChannel);
        if (!_guidToStr.TryGetValue(sender, out var from) || from.CurrentRoom.Length == 0)
            return;
        // The priority, reliability and channel are read and not used: group messages go as other notifications.
        _ = (priority, reliability, orderingChannel);
        var room = _chatRooms.Find(r => r.RoomName == from.CurrentRoom);
        if (room is null)
            return;
        using var stream = Begin(RelayPluginMessage.GroupMsgFromServer);
        WriteCompressed(stream, from.Key);
        stream.AlignToByte();
        stream.Write(ref reader);
        foreach (var user in room.UsersInRoom)
        {
            if (user.Guid != from.Guid)
                Send(peer, stream, user.Guid);
        }
    }

    // OnJoinGroupRequestFromClient (RelayPlugin.cpp:441-468).
    private void OnJoinGroupRequestFromClient(RakPeer peer, BitStreamReader reader, RakNetGuid sender)
    {
        var groupName = reader.TryReadCompressedRakStringBytes(out var bytes) ? Latin1(bytes) : "";
        var groupJoined = JoinGroup(peer, sender, groupName);
        using var stream = Begin(groupJoined is null ? RelayPluginMessage.JoinGroupFailure : RelayPluginMessage.JoinGroupSuccess);
        if (groupJoined is not null)
        {
            stream.WriteUInt16((ushort)groupJoined.UsersInRoom.Count);
            foreach (var user in groupJoined.UsersInRoom)
                WriteCompressed(stream, user.Key);
        }
        Send(peer, stream, sender);
    }

    // AddParticipantOnServer (RelayPlugin.cpp:46-73).
    private RelayPluginMessage AddParticipant(RakPeer peer, string key, RakNetGuid guid)
    {
        if (peer.GetConnectionState(guid) != ConnectionState.Connected)
            return RelayPluginMessage.AddClientTargetNotConnected;
        if (_strToGuid.ContainsKey(key))
            return RelayPluginMessage.AddClientNameAlreadyInUse;
        // A GUID already in use loses its old key.
        if (_guidToStr.Remove(guid, out var existing))
            _strToGuid.Remove(existing.Key);
        var participant = new Participant(key, guid);
        _strToGuid.Add(key, participant);
        _guidToStr.Add(guid, participant);
        return RelayPluginMessage.AddClientSuccess;
    }

    // RemoveParticipantOnServer (RelayPlugin.cpp:74-83).
    private void RemoveParticipant(RakNetGuid guid)
    {
        if (!_guidToStr.Remove(guid, out var participant))
            return;
        LeaveGroup(_host?.Peer, participant);
        _strToGuid.Remove(participant.Key);
    }

    // JoinGroup(userGuid, roomName) (RelayPlugin.cpp:290-322).
    private Group? JoinGroup(RakPeer? peer, RakNetGuid userGuid, string roomName)
    {
        if (!_guidToStr.TryGetValue(userGuid, out var sender))
            return null;
        if (roomName.Length == 0 || sender.CurrentRoom == roomName)
            return null;
        if (sender.CurrentRoom.Length != 0)
            LeaveGroup(peer, sender);
        var room = _chatRooms.Find(r => r.RoomName == roomName);
        if (room is null)
        {
            room = new Group(roomName);
            _chatRooms.Add(room);
        }
        // JoinGroup(room, sender) (RelayPlugin.cpp:263-276): the members are told before the joiner is added.
        NotifyUsersInRoom(peer, room, RelayPluginMessage.UserEnteredRoom, sender.Key);
        room.UsersInRoom.Add(new Member(sender.Key, sender.Guid));
        sender.CurrentRoom = room.RoomName;
        return room;
    }

    // LeaveGroup(strAndGuidSender) (RelayPlugin.cpp:323-353).
    private void LeaveGroup(RakPeer? peer, Participant sender)
    {
        var userName = sender.Key;
        for (var i = 0; i < _chatRooms.Count; i++)
        {
            if (_chatRooms[i].RoomName != sender.CurrentRoom)
                continue;
            sender.CurrentRoom = "";
            var room = _chatRooms[i];
            for (var j = 0; j < room.UsersInRoom.Count; j++)
            {
                if (room.UsersInRoom[j].Guid != sender.Guid)
                    continue;
                RemoveAtIndexFast(room.UsersInRoom, j);
                if (room.UsersInRoom.Count == 0)
                {
                    RemoveAtIndexFast(_chatRooms, i);
                    return;
                }
            }
            NotifyUsersInRoom(peer, room, RelayPluginMessage.UserLeftRoom, userName);
            return;
        }
    }

    // NotifyUsersInRoom (RelayPlugin.cpp:354-366); nothing is sent while detached.
    private static void NotifyUsersInRoom(RakPeer? peer, Group room, RelayPluginMessage message, string userName)
    {
        foreach (var user in room.UsersInRoom)
        {
            using var stream = Begin(message);
            WriteCompressed(stream, userName);
            Send(peer, stream, user.Guid);
        }
    }

    // SendChatRoomsList (RelayPlugin.cpp:398-411).
    private void SendChatRoomsList(RakPeer peer, RakNetGuid target)
    {
        using var stream = Begin(RelayPluginMessage.GetGroupListReplyFromServer);
        stream.WriteUInt16((ushort)_chatRooms.Count);
        foreach (var room in _chatRooms)
        {
            WriteCompressed(stream, room.RoomName);
            stream.WriteUInt16((ushort)room.UsersInRoom.Count);
        }
        Send(peer, stream, target);
    }

    private void SendRequest(BitStream stream, RakNetGuid relayPluginServerGuid) => Send(Peer, stream, relayPluginServerGuid);

    // HIGH_PRIORITY, RELIABLE_ORDERED, channel 0; RakPeer::Send sends nothing to UNASSIGNED_RAKNET_GUID.
    private static void Send(RakPeer? peer, BitStream stream, RakNetGuid target)
    {
        if (peer is not null && target.IsAssigned)
            peer.Send(stream, target, Reliability.ReliableOrdered, Priority.High, 0);
    }

    // RakPeer::Send asserts these; out of range they are not sent here.
    private static bool IsSendable(byte priority, byte reliability, byte orderingChannel) =>
        priority <= (byte)Priority.Low && reliability <= (byte)Reliability.ReliableOrderedWithAckReceipt && orderingChannel < 32;

    private static BitStream Begin(RelayPluginMessage message)
    {
        var stream = new BitStream();
        stream.WriteByte((byte)MessageId.RelayPlugin);
        stream.WriteByte((byte)message);
        return stream;
    }

    // BitStream::WriteCompressed(const RakString&): RakString::SerializeCompressed with language 0 and no language ID.
    private static void WriteCompressed(BitStream stream, ReadOnlySpan<byte> value)
    {
        var writer = stream.BeginWrite(CompressedStringHintBytes + (value.Length * 4));
        writer.WriteCompressedRakString(value);
        stream.EndWrite(writer);
    }

    private static void WriteCompressed(BitStream stream, string key) => WriteCompressed(stream, Encoding.Latin1.GetBytes(key));

    // A key from the API is the UTF-8 of the string; held as the Latin-1 string of those bytes.
    private static string ToKey(string value) => Latin1(Encoding.UTF8.GetBytes(value));

    private static string Latin1(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);

    private static void RemoveAtIndexFast<T>(List<T> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    /// <summary>RelayPlugin::StrAndGuidAndRoom (RelayPlugin.h:136-141).</summary>
    private sealed class Participant(string key, RakNetGuid guid)
    {
        public string Key { get; } = key;

        public RakNetGuid Guid { get; } = guid;

        public string CurrentRoom { get; set; } = "";
    }

    /// <summary>RelayPlugin::StrAndGuid (RelayPlugin.h:143-147).</summary>
    private readonly record struct Member(string Key, RakNetGuid Guid);

    /// <summary>RelayPlugin::RP_Group (RelayPlugin.h:149-153).</summary>
    private sealed class Group(string roomName)
    {
        public string RoomName { get; } = roomName;

        public List<Member> UsersInRoom { get; } = [];
    }
}
