using System.Security.Cryptography;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>Where a client stands with one system of a verified join: FullyConnectedMesh2::JoinInProgressState.</summary>
public enum VerifiedJoinState : byte
{
    /// <summary>JIPS_PROCESSING: not connected yet, and no attempt has failed.</summary>
    Processing,

    /// <summary>JIPS_FAILED: the connection failed, was lost or is closing.</summary>
    Failed,

    /// <summary>JIPS_CONNECTED</summary>
    Connected,

    /// <summary>JIPS_UNNECESSARY: the host no longer lists the system.</summary>
    Unnecessary,
}

/// <summary>
/// A system a client must connect to for a verified join, as <see cref="FullyConnectedMesh2.GetVerifiedJoinRequiredProcessingList"/>
/// returns it, with what the host's <see cref="FullyConnectedMesh2.VerifiedJoinStartUserData"/> wrote for it.
/// </summary>
/// <param name="Address">The address the host sees the system at.</param>
/// <param name="RakNetGuid">The system's GUID.</param>
/// <param name="UserData">The user data bits, left-aligned; empty when the host wrote none (RakNet's null BitStream).</param>
/// <param name="UserDataBitLength">How many bits of <paramref name="UserData"/> were written.</param>
public sealed record VerifiedJoinSystem(SystemAddress Address, RakNetGuid RakNetGuid, ReadOnlyMemory<byte> UserData, int UserDataBitLength);

/// <summary>What <see cref="FullyConnectedMesh2.GetVerifiedJoinAcceptedAdditionalData"/> reads from ID_FCM2_VERIFIED_JOIN_ACCEPTED.</summary>
/// <param name="ThisSystemAccepted">This system is the one accepted; otherwise the message tells of another system.</param>
/// <param name="SystemsAccepted">
/// The systems added as participants: for this system, the mesh it was told to join followed by the host; otherwise the
/// one system accepted.
/// </param>
/// <param name="AdditionalData">What the host passed to <see cref="FullyConnectedMesh2.RespondOnVerifiedJoinCapable(ReadOnlySpan{byte}, SystemAddress, RakNetGuid, bool, ReadOnlySpan{byte})"/>.</param>
public sealed record VerifiedJoinAccepted(bool ThisSystemAccepted, IReadOnlyList<RakNetGuid> SystemsAccepted, ReadOnlyMemory<byte> AdditionalData);

/// <summary>
/// FullyConnectedMesh2: elects one host among a fully connected mesh of participants, the system that has been running
/// longest, and optionally connects to every system ConnectionGraph2 announces. Also runs RakNet's verified join, which
/// admits a client to the mesh only once it has connected to every participant.
/// </summary>
/// <remarks>
/// <para>
/// Ported from FullyConnectedMesh2.cpp. Each participant gets a 64-bit FCM2 GUID whose high half is the mesh's total
/// connection count when it joined and whose low half is random; the participant with the lowest is the host. A new
/// participant is asked for its GUID with ID_FCM2_REQUEST_FCMGUID (a bit saying whether ours follows, then either our
/// elapsed runtime in microseconds or the total connection count and our GUID); a system with no GUID is answered with
/// ID_FCM2_RESPOND_CONNECTION_COUNT, from which it makes its own, and GUIDs are spread with ID_FCM2_INFORM_FCMGUID and
/// ID_FCM2_UPDATE_MIN_TOTAL_CONNECTION_COUNT. These are consumed. When the host changes, ID_FCM2_NEW_HOST with the old
/// host's GUID is injected at the head of the stream from the new host's GUID, in RakNet's 17 bytes, the eight after the
/// old host zero where RakNet leaves them uninitialised; one that arrives from the network is consumed. Everything is sent HIGH_PRIORITY and RELIABLE_ORDERED on channel 0, and multi-byte values are big-endian.
/// </para>
/// <para>
/// The verified join messages and their bit layouts are RakNet's: ID_FCM2_VERIFIED_JOIN_START lists systems as GUID,
/// SystemAddress, a 32-bit bit count and that many bits of <see cref="VerifiedJoinStartUserData"/> padded to a byte;
/// ID_FCM2_VERIFIED_JOIN_CAPABLE lists GUID, SystemAddress and a <see cref="VerifiedJoinState"/> byte, followed by
/// <see cref="VerifiedJoinCapableUserData"/>. A client that loses the host mid-join gets ID_FCM2_VERIFIED_JOIN_FAILED
/// injected with RakNet's two-byte length, the second byte zero where RakNet leaves it uninitialised. A rejected or failed
/// join forgets the user data once, where RakNet frees it twice (FullyConnectedMesh2.cpp:1186-1196).
/// </para>
/// <para>
/// RakNet's behaviour is kept where it surprises: <see cref="ConnectToRemoteNewIncomingConnections"/> reads
/// ID_REMOTE_NEW_INCOMING_CONNECTION without the 16-bit ping after each entry, so it reliably reaches only the first
/// system listed, and a misread address leaves the previous one in place (FullyConnectedMesh2.cpp:757-773); the
/// participant list removes by moving the last entry into the hole; and <see cref="GetHostOrder"/> drops participants
/// whose FCM2 GUID another already has. A truncated message is ignored, where RakNet would read uninitialised or zero
/// values, and the remote-connection list stops at the end of its message.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class FullyConnectedMesh2 : IRakPlugin
{
    // sizeof(MessageID)+sizeof(RakNetGUID): the GUID's 64 bits, its 16-bit systemIndex and padding to 16 bytes.
    private const int NewHostPacketLength = 1 + 16;

    // AllocatePacketUnified(sizeof(MessageID)+sizeof(unsigned char)) with only the ID set (FullyConnectedMesh2.cpp:326-327).
    private static readonly byte[] FailedPacket = [(byte)MessageId.Fcm2VerifiedJoinFailed, 0];

    private readonly Lock _gate = new();
    private readonly List<Participant> _participants = [];
    private readonly List<JoinInProgress> _joinsInProgress = [];
    private IPluginHost? _host;
    private long? _startupTimestamp;
    private bool _autoParticipateConnections = true;
    private bool _connectOnNewRemoteConnections = true;
    private ReadOnlyMemory<byte> _connectionPassword;
    private uint _totalConnectionCount;
    private long _ourFcmGuid;
    private RakNetGuid _lastPushedHost = RakNetGuid.Unassigned;
    private RakNetGuid _hostRakNetGuid = RakNetGuid.Unassigned;

    /// <summary>
    /// WriteVJCUserData: writes data after the list of an outgoing ID_FCM2_VERIFIED_JOIN_CAPABLE, which the host reads
    /// after <see cref="SkipToVerifiedJoinCapableUserData"/>. Called on the dispatch step.
    /// </summary>
    public Action<BitStream>? VerifiedJoinCapableUserData { get; set; }

    /// <summary>
    /// WriteVJSUserData: writes the data sent for one system in ID_FCM2_VERIFIED_JOIN_START, on the caller of
    /// <see cref="StartVerifiedJoin"/> or <see cref="RespondOnVerifiedJoinCapable(ReadOnlySpan{byte}, SystemAddress, RakNetGuid, bool, ReadOnlySpan{byte})"/>
    /// or on the dispatch step.
    /// </summary>
    public Action<BitStream, RakNetGuid>? VerifiedJoinStartUserData { get; set; }

    /// <summary>
    /// SetAutoparticipateConnections: whether every new connection is added with <see cref="AddParticipant"/> as it
    /// connects. True by default.
    /// </summary>
    public bool AutoParticipateConnections
    {
        get
        {
            lock (_gate)
                return _autoParticipateConnections;
        }
        set
        {
            lock (_gate)
                _autoParticipateConnections = value;
        }
    }

    /// <summary>
    /// SetConnectOnNewRemoteConnection: whether every system in an arriving ID_REMOTE_NEW_INCOMING_CONNECTION, which
    /// ConnectionGraph2 sends, is connected to with <see cref="ConnectionPassword"/>. True by default.
    /// </summary>
    public bool ConnectOnNewRemoteConnection
    {
        get
        {
            lock (_gate)
                return _connectOnNewRemoteConnections;
        }
        set
        {
            lock (_gate)
                _connectOnNewRemoteConnections = value;
        }
    }

    /// <summary>The password <see cref="ConnectToRemoteNewIncomingConnections"/> connects with. Empty by default.</summary>
    public ReadOnlyMemory<byte> ConnectionPassword
    {
        get
        {
            lock (_gate)
                return _connectionPassword;
        }
        set
        {
            lock (_gate)
                _connectionPassword = value.ToArray();
        }
    }

    /// <summary>
    /// GetConnectedHost: the host, once this system has its FCM2 GUID; <see cref="RakNetGuid.Unassigned"/> before.
    /// </summary>
    public RakNetGuid ConnectedHost
    {
        get
        {
            lock (_gate)
                return _ourFcmGuid == 0 ? RakNetGuid.Unassigned : _hostRakNetGuid;
        }
    }

    /// <summary>GetConnectedHostAddr: the address of <see cref="ConnectedHost"/>.</summary>
    public SystemAddress ConnectedHostAddress
    {
        get
        {
            lock (_gate)
            {
                if (_ourFcmGuid == 0 || _host?.Peer is not { } peer)
                    return SystemAddress.Unassigned;
                return peer.GetAddressFromGuid(_hostRakNetGuid);
            }
        }
    }

    /// <summary>GetHostSystem: the host, which is this system until it has its FCM2 GUID.</summary>
    public RakNetGuid HostSystem
    {
        get
        {
            lock (_gate)
                return _ourFcmGuid == 0 ? RequireHost().RakNetGuid : _hostRakNetGuid;
        }
    }

    /// <summary>IsHostSystem</summary>
    public bool IsHostSystem => HostSystem == RequireHost().RakNetGuid;

    /// <summary>IsConnectedHost</summary>
    public bool IsConnectedHost => ConnectedHost == RequireHost().RakNetGuid;

    /// <summary>GetParticipantCount</summary>
    public int ParticipantCount
    {
        get
        {
            lock (_gate)
                return _participants.Count;
        }
    }

    /// <summary>GetTotalConnectionCount: the highest total connection count known, the high half of new FCM2 GUIDs.</summary>
    public uint TotalConnectionCount
    {
        get
        {
            lock (_gate)
                return _totalConnectionCount;
        }
    }

    /// <summary>GetElapsedRuntime: how long since the peer started or the plugin was attached, the measure of seniority.</summary>
    public TimeSpan ElapsedRuntime
    {
        get
        {
            lock (_gate)
                return TimeSpan.FromMicroseconds(ElapsedRuntimeUs());
        }
    }

    /// <summary>
    /// GetHostOrder: every participant with this system, from the lowest FCM2 GUID (the host) up; only this system
    /// before it has its FCM2 GUID or a participant.
    /// </summary>
    public IReadOnlyList<RakNetGuid> GetHostOrder()
    {
        lock (_gate)
        {
            var me = RequireHost().RakNetGuid;
            if (_ourFcmGuid == 0 || _participants.Count == 0)
                return [me];
            // An OrderedList keyed on the FCM2 GUID: a duplicate key is not inserted (DS_OrderedList.h:181-187).
            var ordered = new SortedList<long, RakNetGuid> { { _ourFcmGuid, me } };
            foreach (var participant in _participants)
                ordered.TryAdd(participant.FcmGuid, participant.RakNetGuid);
            return [.. ordered.Values];
        }
    }

    /// <summary>
    /// ResetHostCalculation: forgets this system's FCM2 GUID and seniority, as if it had just started, and asks every
    /// participant again. Call it just before creating or joining a session.
    /// </summary>
    public void ResetHostCalculation()
    {
        lock (_gate)
        {
            var host = RequireHost();
            _hostRakNetGuid = RakNetGuid.Unassigned;
            _startupTimestamp = host.TimeProvider.GetTimestamp();
            _totalConnectionCount = 0;
            _ourFcmGuid = 0;
            foreach (var participant in _participants)
                SendFcmGuidRequest(participant.RakNetGuid);
        }
    }

    /// <summary>
    /// AddParticipant: adds a connected system to the mesh whose host is tracked, and asks it for its FCM2 GUID. A system
    /// that is not connected is ignored. Called for each new connection while <see cref="AutoParticipateConnections"/>.
    /// </summary>
    public void AddParticipant(RakNetGuid rakNetGuid)
    {
        lock (_gate)
        {
            var peer = RequirePeer();
            // FullyConnectedMesh2.cpp:160: GetConnectionState(GetSystemAddressFromGuid(guid)).
            var address = peer.GetAddressFromGuid(rakNetGuid);
            if (!address.IsAssigned || peer.GetConnectionState(address) != ConnectionState.Connected)
                return;
            AddParticipantInternal(rakNetGuid, 0);
        }
    }

    /// <summary>GetParticipantList: the participants, in the order RakNet keeps them.</summary>
    public IReadOnlyList<RakNetGuid> GetParticipantList()
    {
        lock (_gate)
            return [.. _participants.Select(p => p.RakNetGuid)];
    }

    /// <summary>HasParticipant</summary>
    public bool HasParticipant(RakNetGuid participant)
    {
        lock (_gate)
            return IndexOfParticipant(participant) >= 0;
    }

    /// <summary>
    /// ConnectToRemoteNewIncomingConnections: connects to the systems in an ID_REMOTE_NEW_INCOMING_CONNECTION packet,
    /// reading it as RakNet does (see the remarks), with <see cref="ConnectionPassword"/>. For use when
    /// <see cref="ConnectOnNewRemoteConnection"/> is false.
    /// </summary>
    public void ConnectToRemoteNewIncomingConnections(ReadOnlySpan<byte> packetData)
    {
        lock (_gate)
        {
            var peer = RequirePeer();
            var reader = new BitStreamReader(packetData);
            reader.IgnoreBytes(1);
            if (!reader.TryReadUInt32(out var count))
                return;
            var remoteAddress = SystemAddress.Unassigned;
            var options = new ConnectOptions { Password = _connectionPassword };
            for (var i = 0u; i < count; i++)
            {
                if (!TryReadAddressAsRakNet(ref reader, ref remoteAddress))
                    return;
                var complete = reader.TryReadRakNetGuid(out _);
                // RakPeer::Connect returns INVALID_PARAMETER for these (RakPeer.cpp:993-1001).
                if (remoteAddress.IsAssigned && remoteAddress.Port != 0)
                    peer.Connect(remoteAddress, options);
                if (!complete)
                    return;
            }
        }
    }

    /// <summary>Clear: forgets every participant, join in progress, the total connection count and this system's FCM2 GUID.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _participants.Clear();
            _joinsInProgress.Clear();
            _totalConnectionCount = 0;
            _ourFcmGuid = 0;
            _lastPushedHost = RakNetGuid.Unassigned;
        }
    }

    /// <summary>
    /// StartVerifiedJoin: sends <paramref name="client"/> ID_FCM2_VERIFIED_JOIN_START with every participant, which it
    /// must connect to before it can be accepted with <see cref="RespondOnVerifiedJoinCapable(ReadOnlySpan{byte}, SystemAddress, RakNetGuid, bool, ReadOnlySpan{byte})"/>.
    /// </summary>
    public void StartVerifiedJoin(RakNetGuid client)
    {
        lock (_gate)
        {
            var host = RequireHost();
            using var message = new BitStream();
            message.WriteByte((byte)MessageId.Fcm2VerifiedJoinStart);
            message.WriteUInt16(unchecked((ushort)_participants.Count));
            foreach (var participant in _participants)
                WriteJoinStartEntry(message, participant.RakNetGuid);
            Send(host, message, client);
        }
    }

    /// <summary>
    /// RespondOnVerifiedJoinCapable: answers an ID_FCM2_VERIFIED_JOIN_CAPABLE. If participants joined since the client
    /// was told, it is sent ID_FCM2_VERIFIED_JOIN_START with them instead. Otherwise, with <paramref name="accept"/>, every
    /// participant and the client get ID_FCM2_VERIFIED_JOIN_ACCEPTED, the client becomes a participant here and the
    /// message is injected at the head of this peer's stream; without, the client gets ID_FCM2_VERIFIED_JOIN_REJECTED.
    /// <paramref name="additionalData"/> follows either message.
    /// </summary>
    /// <param name="packetData">The ID_FCM2_VERIFIED_JOIN_CAPABLE packet.</param>
    /// <param name="senderAddress">The packet's address.</param>
    /// <param name="sender">The packet's GUID: the client.</param>
    /// <param name="accept">Whether to admit the client.</param>
    /// <param name="additionalData">Bytes appended to the reply.</param>
    public void RespondOnVerifiedJoinCapable(ReadOnlySpan<byte> packetData, SystemAddress senderAddress, RakNetGuid sender, bool accept,
                                             ReadOnlySpan<byte> additionalData = default)
    {
        lock (_gate)
        {
            var host = RequireHost();
            if (!TryDecomposeJoinCapable(packetData, out var members))
                throw new ArgumentException("Not a well-formed ID_FCM2_VERIFIED_JOIN_CAPABLE packet.", nameof(packetData));
            var categories = Categorize(members);

            if (categories.ParticipatingNotOnClient.Count > 0)
            {
                SendJoinStart(host, categories.ParticipatingNotOnClient, sender);
                return;
            }

            using var reply = new BitStream();
            if (accept)
            {
                reply.WriteByte((byte)MessageId.Fcm2VerifiedJoinAccepted);
                reply.WriteRakNetGuid(sender);
                // Tell the client to disconnect from the systems that are not participants...
                reply.WriteUInt16(unchecked((ushort)categories.ClientNotParticipatingSucceeded.Count));
                foreach (var guid in categories.ClientNotParticipatingSucceeded)
                    reply.WriteRakNetGuid(guid);
                // ...and to add the participants it reached.
                reply.WriteUInt16(unchecked((ushort)categories.ParticipatingOnClientSucceeded.Count));
                foreach (var guid in categories.ParticipatingOnClientSucceeded)
                    reply.WriteRakNetGuid(guid);
                reply.WriteBytes(additionalData);

                foreach (var participant in _participants.ToList())
                    Send(host, reply, participant.RakNetGuid);

                // FullyConnectedMesh2.cpp:877-887: processed at once, so a buffered ID_FCM2_VERIFIED_JOIN_CAPABLE from
                // another client is answered with ID_FCM2_VERIFIED_JOIN_START.
                AddParticipant(sender);
                host.Inject(reply.Written, senderAddress, sender, atHead: true, generatedLocally: true);
            }
            else
            {
                // Rejected, which the client handles as ID_FCM2_VERIFIED_JOIN_FAILED.
                reply.WriteByte((byte)MessageId.Fcm2VerifiedJoinRejected);
                reply.WriteBytes(additionalData);
            }
            Send(host, reply, sender);
        }
    }

    /// <inheritdoc cref="RespondOnVerifiedJoinCapable(ReadOnlySpan{byte}, SystemAddress, RakNetGuid, bool, ReadOnlySpan{byte})"/>
    public void RespondOnVerifiedJoinCapable(PeerEvent packet, bool accept, ReadOnlySpan<byte> additionalData = default) =>
        RespondOnVerifiedJoinCapable(packet.Data, packet.Address, packet.RakNetGuid, accept, additionalData);

    /// <summary>
    /// GetVerifiedJoinRequiredProcessingList: on ID_FCM2_VERIFIED_JOIN_START from <paramref name="host"/>, the systems
    /// still to connect to, with the user data the host wrote for each. Empty when no join from that host is in progress.
    /// </summary>
    public IReadOnlyList<VerifiedJoinSystem> GetVerifiedJoinRequiredProcessingList(RakNetGuid host)
    {
        lock (_gate)
        {
            var join = FindJoin(host);
            if (join is null)
                return [];
            return [.. join.Members
                .Where(m => m.State == VerifiedJoinState.Processing)
                .Select(m => new VerifiedJoinSystem(m.Address, m.RakNetGuid, m.UserData, m.UserDataBitLength))];
        }
    }

    /// <summary>
    /// GetVerifiedJoinAcceptedAdditionalData: reads an ID_FCM2_VERIFIED_JOIN_ACCEPTED packet from <paramref name="sender"/>.
    /// </summary>
    public VerifiedJoinAccepted GetVerifiedJoinAcceptedAdditionalData(ReadOnlySpan<byte> packetData, RakNetGuid sender)
    {
        var me = RequireHost().RakNetGuid;
        var reader = new BitStreamReader(packetData);
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var systemToAdd);
        var thisSystemAccepted = systemToAdd == me;
        reader.TryReadUInt16(out var listSize);
        reader.IgnoreBytes(listSize * sizeof(ulong));
        reader.TryReadUInt16(out listSize);
        var accepted = new List<RakNetGuid>();
        if (thisSystemAccepted)
        {
            for (var i = 0; i < listSize; i++)
            {
                reader.TryReadRakNetGuid(out var guid);
                accepted.Add(guid);
            }
            accepted.Add(sender);
        }
        else
        {
            accepted.Add(systemToAdd);
            reader.IgnoreBytes(listSize * sizeof(ulong));
        }
        return new VerifiedJoinAccepted(thisSystemAccepted, accepted, RemainingBytes(packetData, reader));
    }

    /// <summary>GetVerifiedJoinRejectedAdditionalData: what follows the ID of an ID_FCM2_VERIFIED_JOIN_REJECTED packet.</summary>
    public static ReadOnlyMemory<byte> GetVerifiedJoinRejectedAdditionalData(ReadOnlySpan<byte> packetData) =>
        packetData.Length > 1 ? packetData[1..].ToArray() : ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// SkipToVJCUserData: moves a reader at the start of an ID_FCM2_VERIFIED_JOIN_CAPABLE packet to the data
    /// <see cref="VerifiedJoinCapableUserData"/> wrote. False when the packet is truncated.
    /// </summary>
    public static bool SkipToVerifiedJoinCapableUserData(ref BitStreamReader reader)
    {
        if (!reader.IgnoreBytes(1) || !reader.TryReadUInt16(out var listSize))
            return false;
        // RakNetGUID::size(), SystemAddress::size() and the state byte (FullyConnectedMesh2.cpp:1079-1084).
        return reader.IgnoreBytes(listSize * (sizeof(ulong) + SystemAddress.WireSize + 1));
    }

    void IRakPlugin.OnAttached(IPluginHost host)
    {
        lock (_gate)
        {
            _host = host;
            Clear();
            // In case the peer was started first (FullyConnectedMesh2.cpp:302-308).
            if (host.Peer is { IsActive: true })
                _startupTimestamp = host.TimeProvider.GetTimestamp();
        }
    }

    void IRakPlugin.OnDetached()
    {
        lock (_gate)
            _host = null;
    }

    void IRakPlugin.OnStartup()
    {
        lock (_gate)
        {
            Clear();
            _startupTimestamp = RequireHost().TimeProvider.GetTimestamp();
        }
    }

    void IRakPlugin.OnShutdown()
    {
        lock (_gate)
        {
            Clear();
            _startupTimestamp = null;
        }
    }

    /// <summary>OnNewConnection (FullyConnectedMesh2.cpp:389-399).</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        lock (_gate)
        {
            UpdateJoinMember(connection.RakNetGuid, connection.RakNetGuid, VerifiedJoinState.Connected);
            if (_autoParticipateConnections)
                AddParticipant(connection.RakNetGuid);
        }
    }

    /// <summary>OnFailedConnectionAttempt (FullyConnectedMesh2.cpp:400-410).</summary>
    void IRakPlugin.OnConnectFailed(PeerEvent e, ConnectFailure failure)
    {
        lock (_gate)
        {
            if (failure == ConnectFailure.AlreadyConnected)
                UpdateJoinMember(e.RakNetGuid, e.RakNetGuid, VerifiedJoinState.Connected);
            else
                UpdateJoinMember(e.Address, RakNetGuid.Unassigned, VerifiedJoinState.Failed);
        }
    }

    /// <summary>OnClosedConnection (FullyConnectedMesh2.cpp:314-380).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            var host = RequireHost();
            var closed = connection.RakNetGuid;
            for (var index = 0; index < _joinsInProgress.Count;)
            {
                if (_joinsInProgress[index].Requester == closed)
                {
                    // The host went away mid-join: the client is told locally.
                    host.Inject(FailedPacket, connection.Address, closed, atHead: true, generatedLocally: true);
                    _joinsInProgress.RemoveAt(index);
                }
                else
                {
                    index++;
                }
            }

            UpdateJoinMember(closed, RakNetGuid.Unassigned, VerifiedJoinState.Failed);

            var participantIndex = IndexOfParticipant(closed);
            if (participantIndex < 0)
                return;
            RemoveParticipantAt(participantIndex);
            if (closed == _hostRakNetGuid && _ourFcmGuid != 0)
            {
                if (_participants.Count == 0)
                    _hostRakNetGuid = host.RakNetGuid;
                else
                    _hostRakNetGuid = CalculateHost(out _);
                PushNewHost(_hostRakNetGuid, closed);
            }
        }
    }

    /// <summary>OnReceive (FullyConnectedMesh2.cpp:235-296).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.RemoteNewIncomingConnection:
                    if (_connectOnNewRemoteConnections)
                        ConnectToRemoteNewIncomingConnections(data);
                    return PluginResult.Continue;
                case MessageId.Fcm2RequestFcmguid:
                    OnRequestFcmGuid(e.GetReader(), e.RakNetGuid, e.Address);
                    return PluginResult.Consume;
                case MessageId.Fcm2RespondConnectionCount:
                    OnRespondConnectionCount(e.GetReader());
                    return PluginResult.Consume;
                case MessageId.Fcm2InformFcmguid:
                    OnInformFcmGuid(e.GetReader(), e.RakNetGuid);
                    return PluginResult.Consume;
                case MessageId.Fcm2UpdateMinTotalConnectionCount:
                    OnUpdateMinTotalConnectionCount(e.GetReader());
                    return PluginResult.Consume;
                case MessageId.Fcm2NewHost:
                    return e.IsLocal ? PluginResult.Continue : PluginResult.Consume;
                case MessageId.Fcm2VerifiedJoinStart:
                    return OnVerifiedJoinStart(e.GetReader(), e.RakNetGuid);
                case MessageId.Fcm2VerifiedJoinCapable:
                    return OnVerifiedJoinCapable(data, e.RakNetGuid);
                case MessageId.Fcm2VerifiedJoinFailed:
                    OnVerifiedJoinFailed(e.RakNetGuid, closeConnections: true);
                    return PluginResult.Continue;
                case MessageId.Fcm2VerifiedJoinAccepted:
                    if (!e.IsLocal)
                        OnVerifiedJoinAccepted(e.GetReader(), e.RakNetGuid);
                    return PluginResult.Continue;
                case MessageId.Fcm2VerifiedJoinRejected:
                    OnVerifiedJoinFailed(e.RakNetGuid, closeConnections: true);
                    return PluginResult.Continue;
                case MessageId.NatTargetUnresponsive:
                case MessageId.NatTargetNotConnected:
                case MessageId.NatConnectionToTargetLost:
                {
                    var reader = e.GetReader();
                    reader.IgnoreBits(8);
                    if (reader.TryReadRakNetGuid(out var target))
                        UpdateJoinMember(target, RakNetGuid.Unassigned, VerifiedJoinState.Failed);
                    return PluginResult.Continue;
                }
                case MessageId.NatPunchthroughFailed:
                    UpdateJoinMember(e.RakNetGuid, RakNetGuid.Unassigned, VerifiedJoinState.Failed);
                    return PluginResult.Continue;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    // OnRequestFCMGuid (FullyConnectedMesh2.cpp:531-600).
    private void OnRequestFcmGuid(BitStreamReader reader, RakNetGuid sender, SystemAddress senderAddress)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadBool(out var hasRemoteFcmGuid))
            return;
        ulong senderElapsedRuntime = 0;
        uint remoteTotalConnectionCount = 0;
        long theirFcmGuid = 0;
        if (hasRemoteFcmGuid)
        {
            if (!reader.TryReadUInt32(out remoteTotalConnectionCount) || !reader.TryReadInt64(out theirFcmGuid))
                return;
        }
        else if (!reader.TryReadUInt64(out senderElapsedRuntime))
        {
            return;
        }

        AddParticipantInternal(sender, theirFcmGuid);
        if (_ourFcmGuid == 0)
        {
            if (!hasRemoteFcmGuid)
            {
                // Nobody has an FCM2 GUID: the one running longer is probably the host.
                SendConnectionCountResponse(senderAddress, ElapsedRuntimeUs() > senderElapsedRuntime ? 2u : 1u);
            }
            else
            {
                // They have one and we do not.
                IncrementTotalConnectionCount(remoteTotalConnectionCount + 1);
                AssignOurFcmGuid();
                SendOurFcmGuidToParticipants();
            }
        }
        else if (!hasRemoteFcmGuid)
        {
            // We have one and they do not.
            SendConnectionCountResponse(senderAddress, _totalConnectionCount + 1);
        }
        else
        {
            // We both have one.
            IncrementTotalConnectionCount(remoteTotalConnectionCount);
            SendOurFcmGuid(senderAddress);
        }
        CalculateAndPushHost();
    }

    // OnRespondConnectionCount (FullyConnectedMesh2.cpp:620-661).
    private void OnRespondConnectionCount(BitStreamReader reader)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadUInt32(out var responseTotalConnectionCount))
            return;
        IncrementTotalConnectionCount(responseTotalConnectionCount);
        var wasAssigned = _ourFcmGuid == 0;
        if (wasAssigned)
            AssignOurFcmGuid();
        // 1 is answered to give the asker lower priority, but the actual minimum is 2.
        IncrementTotalConnectionCount(2);
        if (wasAssigned)
        {
            SendOurFcmGuidToParticipants();
            CalculateAndPushHost();
        }
    }

    // OnInformFCMGuid (FullyConnectedMesh2.cpp:662-701).
    private void OnInformFcmGuid(BitStreamReader reader, RakNetGuid sender)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadInt64(out var theirFcmGuid) || !reader.TryReadUInt32(out var theirTotalConnectionCount))
            return;
        IncrementTotalConnectionCount(theirTotalConnectionCount);
        if (AddParticipantInternal(sender, theirFcmGuid))
        {
            // Relay the increased total connection count in case the new participant only connects to part of the mesh.
            Span<byte> update = stackalloc byte[1 + sizeof(uint)];
            var writer = new BitStreamWriter(update);
            writer.WriteByte((byte)MessageId.Fcm2UpdateMinTotalConnectionCount);
            writer.WriteUInt32(_totalConnectionCount);
            var host = RequireHost();
            foreach (var participant in _participants.ToList())
            {
                if (participant.RakNetGuid != sender)
                    Send(host, writer, participant.RakNetGuid);
            }
        }
        if (_ourFcmGuid == 0)
        {
            AssignOurFcmGuid();
            SendOurFcmGuidToParticipants();
        }
        CalculateAndPushHost();
    }

    // OnUpdateMinTotalConnectionCount (FullyConnectedMesh2.cpp:702-709).
    private void OnUpdateMinTotalConnectionCount(BitStreamReader reader)
    {
        reader.IgnoreBytes(1);
        if (reader.TryReadUInt32(out var newMinimum))
            IncrementTotalConnectionCount(newMinimum);
    }

    // OnVerifiedJoinStart (FullyConnectedMesh2.cpp:963-1073).
    private PluginResult OnVerifiedJoinStart(BitStreamReader reader, RakNetGuid sender)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadUInt16(out var listSize))
            return PluginResult.Continue;
        var listed = new List<JoinMember>(listSize);
        for (var i = 0; i < listSize; i++)
        {
            if (!TryReadJoinMember(ref reader, out var member))
                return PluginResult.Consume;
            listed.Add(member);
        }

        var join = FindJoin(sender);
        if (join is not null)
        {
            // The host has an updated list of participants.
            foreach (var member in join.Members)
                member.WorkingFlag = false;
            foreach (var member in listed)
            {
                var existing = member.RakNetGuid.IsAssigned ? FindJoinMember(join, member.RakNetGuid) : FindJoinMember(join, member.Address);
                if (existing is null)
                {
                    member.WorkingFlag = true;
                    join.Members.Add(member);
                }
                else
                {
                    existing.WorkingFlag = true;
                }
            }
            foreach (var member in join.Members)
            {
                if (!member.WorkingFlag)
                    member.State = VerifiedJoinState.Unnecessary;
            }
            return SendJoinCapableIfCompleted(join) ? PluginResult.Consume : PluginResult.Continue;
        }

        join = new JoinInProgress(sender);
        _joinsInProgress.Add(join);
        if (listSize == 0)
        {
            using var capable = new BitStream();
            capable.WriteByte((byte)MessageId.Fcm2VerifiedJoinCapable);
            capable.WriteUInt16(0);
            VerifiedJoinCapableUserData?.Invoke(capable);
            Send(RequireHost(), capable, sender);
            return PluginResult.Consume;
        }
        join.Members.AddRange(listed);
        // ReadVerifiedJoinInProgressMember may have found every system connected already.
        return SendJoinCapableIfCompleted(join) ? PluginResult.Consume : PluginResult.Continue;
    }

    // OnVerifiedJoinCapable (FullyConnectedMesh2.cpp:1105-1169).
    private PluginResult OnVerifiedJoinCapable(ReadOnlySpan<byte> data, RakNetGuid sender)
    {
        if (!TryDecomposeJoinCapable(data, out var members))
            return PluginResult.Consume;
        var categories = Categorize(members);
        var host = RequireHost();
        if (categories.ParticipatingOnClientFailed.Count > 0)
        {
            Span<byte> failed = [(byte)MessageId.Fcm2VerifiedJoinFailed];
            Send(host, failed, sender);
            return PluginResult.Consume;
        }
        if (categories.ParticipatingNotOnClient.Count > 0)
        {
            SendJoinStart(host, categories.ParticipatingNotOnClient, sender);
            return PluginResult.Consume;
        }
        // The application decides with RespondOnVerifiedJoinCapable.
        return PluginResult.Continue;
    }

    // OnVerifiedJoinFailed (FullyConnectedMesh2.cpp:1170-1203).
    private void OnVerifiedJoinFailed(RakNetGuid hostGuid, bool closeConnections)
    {
        var join = FindJoin(hostGuid);
        if (join is null)
            return;
        if (closeConnections)
        {
            var peer = RequirePeer();
            foreach (var member in join.Members)
            {
                if (member.State != VerifiedJoinState.Failed && member.RakNetGuid.IsAssigned)
                    peer.Close(member.RakNetGuid, notify: true);
            }
        }
        _joinsInProgress.Remove(join);
    }

    // OnVerifiedJoinAccepted (FullyConnectedMesh2.cpp:1204-1259).
    private void OnVerifiedJoinAccepted(BitStreamReader reader, RakNetGuid sender)
    {
        reader.IgnoreBytes(1);
        if (!reader.TryReadRakNetGuid(out var systemToAdd))
            return;
        var peer = RequirePeer();
        if (systemToAdd == peer.RakNetGuid)
        {
            var join = FindJoin(sender);
            if (join is null)
                return;
            if (!reader.TryReadUInt16(out var listSize))
                return;
            for (var i = 0; i < listSize; i++)
            {
                // The systems that are not participants.
                if (!reader.TryReadRakNetGuid(out var guid))
                    return;
                if (guid.IsAssigned)
                    peer.Close(guid, notify: true);
            }
            if (!reader.TryReadUInt16(out listSize))
                return;
            for (var i = 0; i < listSize; i++)
            {
                // The participants this system reached.
                if (!reader.TryReadRakNetGuid(out var guid))
                    return;
                AddParticipant(guid);
            }
            AddParticipant(sender);
            _joinsInProgress.Remove(join);
        }
        else if (peer.GetConnectionState(systemToAdd) == ConnectionState.Connected)
        {
            // Another system joined.
            AddParticipant(systemToAdd);
        }
    }

    // AddParticipantInternal (FullyConnectedMesh2.cpp:122-157): true when the system was not a participant.
    private bool AddParticipantInternal(RakNetGuid rakNetGuid, long theirFcmGuid)
    {
        var index = IndexOfParticipant(rakNetGuid);
        if (index >= 0)
        {
            if (theirFcmGuid != 0)
                _participants[index].FcmGuid = theirFcmGuid;
            return false;
        }
        _participants.Add(new Participant(rakNetGuid) { FcmGuid = theirFcmGuid });
        SendFcmGuidRequest(rakNetGuid);
        return true;
    }

    // SendFCMGuidRequest (FullyConnectedMesh2.cpp:452-473): myContext, never set in RakNet 4.081, adds no bits.
    private void SendFcmGuidRequest(RakNetGuid rakNetGuid)
    {
        var host = RequireHost();
        if (rakNetGuid == host.RakNetGuid)
            return;
        Span<byte> buffer = stackalloc byte[1 + 1 + sizeof(uint) + sizeof(long)];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2RequestFcmguid);
        if (_ourFcmGuid == 0)
        {
            writer.WriteBool(false);
            writer.WriteUInt64(ElapsedRuntimeUs());
        }
        else
        {
            writer.WriteBool(true);
            writer.WriteUInt32(_totalConnectionCount);
            writer.WriteInt64(_ourFcmGuid);
        }
        Send(host, writer, rakNetGuid);
    }

    // SendOurFCMGuid (FullyConnectedMesh2.cpp:474-484).
    private void SendOurFcmGuid(SystemAddress address)
    {
        Span<byte> buffer = stackalloc byte[1 + sizeof(long) + sizeof(uint)];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2InformFcmguid);
        writer.WriteInt64(_ourFcmGuid);
        writer.WriteUInt32(_totalConnectionCount);
        Send(RequireHost(), writer, address);
    }

    private void SendOurFcmGuidToParticipants()
    {
        var peer = RequirePeer();
        foreach (var participant in _participants.ToList())
            SendOurFcmGuid(peer.GetAddressFromGuid(participant.RakNetGuid));
    }

    // SendConnectionCountResponse (FullyConnectedMesh2.cpp:485-493).
    private void SendConnectionCountResponse(SystemAddress address, uint responseTotalConnectionCount)
    {
        Span<byte> buffer = stackalloc byte[1 + sizeof(uint)];
        var writer = new BitStreamWriter(buffer);
        writer.WriteByte((byte)MessageId.Fcm2RespondConnectionCount);
        writer.WriteUInt32(responseTotalConnectionCount);
        Send(RequireHost(), writer, address);
    }

    // AssignOurFCMGuid (FullyConnectedMesh2.cpp:494-504): randomMT() is replaced by a CSPRNG; the low half is random
    // either way.
    private void AssignOurFcmGuid()
    {
        var host = RequireHost();
        var random = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(sizeof(uint)));
        random ^= unchecked((uint)NowUs(host));
        random ^= unchecked((uint)host.RakNetGuid.Value);
        _ourFcmGuid |= random;
        _ourFcmGuid |= (long)((ulong)_totalConnectionCount << 32);
    }

    // CalculateHost (FullyConnectedMesh2.cpp:505-530): the lowest assigned FCM2 GUID.
    private RakNetGuid CalculateHost(out long fcmGuid)
    {
        var lowest = _ourFcmGuid;
        var guid = RequireHost().RakNetGuid;
        foreach (var participant in _participants)
        {
            if (participant.FcmGuid != 0 && participant.FcmGuid < lowest)
            {
                lowest = participant.FcmGuid;
                guid = participant.RakNetGuid;
            }
        }
        fcmGuid = lowest;
        return guid;
    }

    // CalculateAndPushHost (FullyConnectedMesh2.cpp:719-733).
    private void CalculateAndPushHost()
    {
        if (!_participants.TrueForAll(p => p.FcmGuid != 0))
            return;
        var newHost = CalculateHost(out _);
        if (newHost == _lastPushedHost)
            return;
        _hostRakNetGuid = newHost;
        PushNewHost(_hostRakNetGuid, _lastPushedHost);
    }

    // PushNewHost (FullyConnectedMesh2.cpp:437-451): ID_FCM2_NEW_HOST and the old host, from the new host, at the head.
    // The packet is sizeof(MessageID)+sizeof(RakNetGUID) long, and RakNetGUID's in-memory size includes its 16-bit
    // systemIndex and padding, so eight bytes the BitStream never writes follow the GUID.
    private void PushNewHost(RakNetGuid newHost, RakNetGuid oldHost)
    {
        var host = RequireHost();
        Span<byte> packet = stackalloc byte[NewHostPacketLength];
        packet.Clear();
        var writer = new BitStreamWriter(packet);
        writer.WriteByte((byte)MessageId.Fcm2NewHost);
        writer.WriteRakNetGuid(oldHost);
        var address = host.Peer?.GetAddressFromGuid(newHost) ?? SystemAddress.Unassigned;
        host.Inject(packet, address, newHost, atHead: true, generatedLocally: true);
        _lastPushedHost = newHost;
    }

    private void IncrementTotalConnectionCount(uint count)
    {
        if (count > _totalConnectionCount)
            _totalConnectionCount = count;
    }

    // UpdateVerifiedJoinInProgressMember (FullyConnectedMesh2.cpp:1273-1303).
    private void UpdateJoinMember(AddressOrGuid system, RakNetGuid guidToAssign, VerifiedJoinState newState)
    {
        foreach (var join in _joinsInProgress.ToList())
        {
            var member = FindJoinMember(join, system);
            if (member is null)
                continue;
            if (!member.RakNetGuid.IsAssigned && guidToAssign.IsAssigned)
                member.RakNetGuid = guidToAssign;
            if (member.State == VerifiedJoinState.Processing)
            {
                member.State = newState;
                SendJoinCapableIfCompleted(join);
            }
        }
    }

    // ProcessVerifiedJoinInProgressIfCompleted (FullyConnectedMesh2.cpp:1304-1332): once nothing is processing, the
    // results go to the host. Sent again for every later completion, as RakNet does.
    private bool SendJoinCapableIfCompleted(JoinInProgress join)
    {
        if (join.Members.Exists(m => m.State == VerifiedJoinState.Processing))
            return false;
        using var capable = new BitStream();
        capable.WriteByte((byte)MessageId.Fcm2VerifiedJoinCapable);
        capable.WriteUInt16(unchecked((ushort)join.Members.Count));
        foreach (var member in join.Members)
        {
            capable.WriteRakNetGuid(member.RakNetGuid);
            capable.WriteAddress(member.Address);
            capable.WriteByte((byte)member.State);
        }
        VerifiedJoinCapableUserData?.Invoke(capable);
        Send(RequireHost(), capable, join.Requester);
        return true;
    }

    // ReadVerifiedJoinInProgressMember (FullyConnectedMesh2.cpp:1346-1368).
    private bool TryReadJoinMember(ref BitStreamReader reader, out JoinMember member)
    {
        member = null!;
        if (!reader.TryReadRakNetGuid(out var guid) || !reader.TryReadSystemAddress(out var address) || !reader.TryReadUInt32(out var bitLength))
            return false;
        var state = RequirePeer().GetConnectionState(guid) switch
        {
            ConnectionState.Connected => VerifiedJoinState.Connected,
            ConnectionState.Disconnecting or ConnectionState.SilentlyDisconnecting => VerifiedJoinState.Failed,
            _ => VerifiedJoinState.Processing,
        };
        var userData = ReadOnlyMemory<byte>.Empty;
        if (bitLength > 0)
        {
            if (bitLength > reader.BitsRemaining)
                return false;
            var bytes = new byte[(bitLength + 7) / 8];
            if (!reader.TryReadBits(bytes, (int)bitLength, alignBitsToRight: false))
                return false;
            userData = bytes;
        }
        reader.AlignToByte();
        member = new JoinMember(address, guid) { State = state, UserData = userData, UserDataBitLength = (int)bitLength };
        return true;
    }

    // DecomposeJoinCapable (FullyConnectedMesh2.cpp:1086-1104).
    private static bool TryDecomposeJoinCapable(ReadOnlySpan<byte> data, out List<JoinMember> members)
    {
        members = [];
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(1);
        if (!reader.TryReadUInt16(out var listSize))
            return false;
        for (var i = 0; i < listSize; i++)
        {
            if (!reader.TryReadRakNetGuid(out var guid) || !reader.TryReadSystemAddress(out var address) || !reader.TryReadByte(out var state))
                return false;
            members.Add(new JoinMember(address, guid) { State = (VerifiedJoinState)state });
        }
        return true;
    }

    // CategorizeVJIP (FullyConnectedMesh2.cpp:1383-1421).
    private Categories Categorize(List<JoinMember> members)
    {
        var categories = new Categories();
        foreach (var member in members)
            member.WorkingFlag = false;
        foreach (var participant in _participants)
        {
            var member = FindJoinMember(members, participant.RakNetGuid);
            if (member is null)
            {
                categories.ParticipatingNotOnClient.Add(participant.RakNetGuid);
                continue;
            }
            if (member.State == VerifiedJoinState.Failed)
                categories.ParticipatingOnClientFailed.Add(participant.RakNetGuid);
            else
                categories.ParticipatingOnClientSucceeded.Add(participant.RakNetGuid);
            member.WorkingFlag = true;
        }
        foreach (var member in members)
        {
            if (member.WorkingFlag)
                continue;
            if (member.State == VerifiedJoinState.Failed)
                categories.ClientNotParticipatingFailed.Add(member.RakNetGuid);
            else
                categories.ClientNotParticipatingSucceeded.Add(member.RakNetGuid);
        }
        return categories;
    }

    // ID_FCM2_VERIFIED_JOIN_START for the participants the client did not list (FullyConnectedMesh2.cpp:819-850).
    private void SendJoinStart(IPluginHost host, List<RakNetGuid> systems, RakNetGuid client)
    {
        using var message = new BitStream();
        message.WriteByte((byte)MessageId.Fcm2VerifiedJoinStart);
        message.WriteUInt16(unchecked((ushort)systems.Count));
        foreach (var guid in systems)
            WriteJoinStartEntry(message, guid);
        Send(host, message, client);
    }

    // GUID, SystemAddress, then the user data as a 32-bit bit count and the bits, padded to a byte.
    private void WriteJoinStartEntry(BitStream message, RakNetGuid participant)
    {
        message.WriteRakNetGuid(participant);
        message.WriteAddress(RequirePeer().GetAddressFromGuid(participant));
        using var userData = new BitStream();
        VerifiedJoinStartUserData?.Invoke(userData, participant);
        message.WriteUInt32((uint)userData.BitsWritten);
        var reader = userData.CreateReader();
        message.Write(ref reader);
        message.AlignToByte();
    }

    private static ReadOnlyMemory<byte> RemainingBytes(ReadOnlySpan<byte> data, BitStreamReader reader)
    {
        var offset = Math.Min(data.Length, reader.BitPosition >> 3);
        return data[offset..].ToArray();
    }

    // BitStream::Read(SystemAddress) without IPv6: the version byte is consumed, and an address that is not version 4
    // leaves the destination unchanged (BitStream.h:1494-1521). False at the end of the message.
    private static bool TryReadAddressAsRakNet(ref BitStreamReader reader, ref SystemAddress address)
    {
        var start = reader.BitPosition;
        if (!reader.TryReadByte(out var version))
            return false;
        if (version != 4)
            return true;
        reader.BitPosition = start;
        if (!reader.TryReadSystemAddress(out var read))
            return false;
        address = read;
        return true;
    }

    private ulong ElapsedRuntimeUs()
    {
        if (_host is not { } host)
            return 0;
        if (_startupTimestamp is not { } start)
            return NowUs(host);
        return (ulong)host.TimeProvider.GetElapsedTime(start).Ticks / (ulong)TimeSpan.TicksPerMicrosecond;
    }

    // RakNet::GetTimeUS: the peer's clock, which starts with the TimeProvider's origin.
    private static ulong NowUs(IPluginHost host) => host.GetTime() * 1000;

    private int IndexOfParticipant(RakNetGuid rakNetGuid) => _participants.FindIndex(p => p.RakNetGuid == rakNetGuid);

    // The last participant moves into the hole (FullyConnectedMesh2.cpp:357-362).
    private void RemoveParticipantAt(int index)
    {
        _participants[index] = _participants[^1];
        _participants.RemoveAt(_participants.Count - 1);
    }

    private JoinInProgress? FindJoin(RakNetGuid requester) => _joinsInProgress.Find(j => j.Requester == requester);

    private static JoinMember? FindJoinMember(JoinInProgress join, AddressOrGuid system) => FindJoinMember(join.Members, system);

    // GetVerifiedJoinInProgressMemberIndex (FullyConnectedMesh2.cpp:1370-1381).
    private static JoinMember? FindJoinMember(List<JoinMember> members, AddressOrGuid system) =>
        members.Find(m => (system.RakNetGuid.IsAssigned && m.RakNetGuid == system.RakNetGuid)
                          || (system.Address.IsAssigned && m.Address == system.Address));

    // SendUnified / Send with broadcast false: an undefined target sends nothing (RakPeer.cpp:1323-1324).
    private static void Send(IPluginHost host, OutgoingMessage message, AddressOrGuid target)
    {
        if (!target.IsUndefined)
            host.Send(message, target, Reliability.ReliableOrdered, Priority.High, 0);
    }

    private IPluginHost RequireHost() =>
        _host ?? throw new InvalidOperationException("The FullyConnectedMesh2 is not attached to a peer.");

    private RakPeer RequirePeer() =>
        RequireHost().Peer ?? throw new InvalidOperationException("FullyConnectedMesh2 needs a RakPeer host.");

    // FCM2Participant: the FCM2 GUID is 0 until known.
    private sealed class Participant(RakNetGuid rakNetGuid)
    {
        public RakNetGuid RakNetGuid { get; } = rakNetGuid;

        public long FcmGuid { get; set; }
    }

    // VerifiedJoinInProgress
    private sealed class JoinInProgress(RakNetGuid requester)
    {
        public RakNetGuid Requester { get; } = requester;

        public List<JoinMember> Members { get; } = [];
    }

    // VerifiedJoinInProgressMember
    private sealed class JoinMember(SystemAddress address, RakNetGuid rakNetGuid)
    {
        public SystemAddress Address { get; } = address;

        public RakNetGuid RakNetGuid { get; set; } = rakNetGuid;

        public VerifiedJoinState State { get; set; }

        public ReadOnlyMemory<byte> UserData { get; init; }

        public int UserDataBitLength { get; init; }

        public bool WorkingFlag { get; set; }
    }

    private sealed class Categories
    {
        public List<RakNetGuid> ParticipatingOnClientSucceeded { get; } = [];

        public List<RakNetGuid> ParticipatingOnClientFailed { get; } = [];

        public List<RakNetGuid> ParticipatingNotOnClient { get; } = [];

        public List<RakNetGuid> ClientNotParticipatingSucceeded { get; } = [];

        public List<RakNetGuid> ClientNotParticipatingFailed { get; } = [];
    }
}
