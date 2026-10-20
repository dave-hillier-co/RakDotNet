using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// NatPunchthroughServer, the facilitator: every connected system is a user, and a <see cref="NatPunchthroughClient"/>
/// asking to reach another user gets both told the other's most recent public address and a time to start sending.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NatPunchthroughServer.cpp. ID_NAT_PUNCHTHROUGH_REQUEST and the recipient's GUID starts an attempt with
/// the next 16-bit session ID; a recipient that is not a user gets ID_NAT_TARGET_NOT_CONNECTED, and a pair that already
/// has an attempt ID_NAT_ALREADY_IN_PROGRESS. When both users are ready (one attempt at a time each), both are sent
/// ID_NAT_GET_MOST_RECENT_PORT and the session ID; once both have answered with their port, each is sent
/// ID_TIMESTAMP, a start time, ID_NAT_CONNECT_AT_TIME, the session ID, the other's address with that port, the other's
/// ten internal addresses, the other's GUID and whether it is the sender, one bit. The start time is 1.5 s away when
/// either average ping is unknown, otherwise four times the larger ping and at least 100 ms. An attempt still without
/// both ports after 10 s ends with ID_NAT_TARGET_UNRESPONSIVE to both, checked every 250 ms; a user that disconnects
/// ends its attempts with ID_NAT_CONNECTION_TO_TARGET_LOST to the other. Every message is HIGH_PRIORITY and
/// RELIABLE_ORDERED on channel 0.
/// </para>
/// <para>
/// It also answers ID_NAT_REQUEST_BOUND_ADDRESSES with the peer's socket addresses, and an out-of-band ID_NAT_PING with
/// an ID_NAT_PONG holding the port in the ping and the port it came from, which lets a client behind a NAT that assigns
/// ports in sequence learn the stride. RakNet's quirks are kept, among them that the reply goes from the first socket
/// whichever socket the ping reached, and that a request naming the sender itself starts an attempt with itself.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class NatPunchthroughServer : IRakPlugin
{
    private const ulong UpdateIntervalMs = 250;
    private const ulong UnresponsiveTimeoutMs = 10_000;

    private readonly Lock _gate = new();

    // DataStructures::OrderedList<RakNetGUID, User*>: users sorted by GUID.
    private readonly SortedList<RakNetGuid, User> _users = [];
    private readonly SystemAddress[] _boundAddresses = new SystemAddress[RakNetConstants.MaximumNumberOfInternalIds];
    private byte _boundAddressCount;
    private ushort _sessionId;
    private ulong _lastUpdate;
    private IPluginHost? _host;

    /// <summary>SetDebugInterface: receives RakNet's diagnostic messages; null for none.</summary>
    public Action<string>? DebugMessage { get; set; }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The NatPunchthroughServer is not attached to a RakPeer.");

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnNewConnection (NatPunchthroughServer.cpp:364-378): the system becomes a ready user.</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        lock (_gate)
        {
            var guid = connection.RakNetGuid;
            // OrderedList::Insert with assertOnDuplicate: an existing key is kept.
            _users.TryAdd(guid, new User(guid, connection.Address));
        }
    }

    /// <summary>OnClosedConnection (NatPunchthroughServer.cpp:286-362).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            var guid = connection.RakNetGuid;
            if (_host?.Peer is not { } peer || !_users.TryGetValue(guid, out var user))
                return;
            var freedUpInProgressUsers = new List<User>();
            Span<byte> lost = stackalloc byte[1 + sizeof(ulong) + sizeof(ushort)];
            // By index, as RakNet does: an attempt with itself is removed from this list as it is walked.
            for (var i = 0; i < user.ConnectionAttempts.Count; i++)
            {
                var attempt = user.ConnectionAttempts[i];
                var otherUser = attempt.Recipient == user ? attempt.Sender : attempt.Recipient;
                // Both sides are told, as the recipient may be waiting too.
                var writer = new BitStreamWriter(lost);
                writer.WriteByte((byte)MessageId.NatConnectionToTargetLost);
                writer.WriteRakNetGuid(guid);
                writer.WriteUInt16(attempt.SessionId);
                Send(peer, writer, otherUser.SystemAddress);

                if (attempt.AttemptPhase == AttemptPhase.GettingRecentPorts)
                {
                    otherUser.IsReady = true;
                    freedUpInProgressUsers.Add(otherUser);
                }
                otherUser.DeleteConnectionAttempt(attempt);
            }
            _users.Remove(guid);
            foreach (var freed in freedUpInProgressUsers)
                StartPunchthroughForUser(peer, freed);
        }
    }

    /// <summary>Update (NatPunchthroughServer.cpp:152-223).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        lock (_gate)
        {
            var time = peer.GetTime();
            if (!(time > _lastUpdate + UpdateIntervalMs))
                return;
            _lastUpdate = time;
            for (var i = 0; i < _users.Count; i++)
            {
                var user = _users.Values[i];
                for (var j = 0; j < user.ConnectionAttempts.Count; j++)
                {
                    var attempt = user.ConnectionAttempts[j];
                    if (attempt.Sender != user || attempt.AttemptPhase == AttemptPhase.NotStarted
                        || !(time > attempt.StartTime) || !(time > UnresponsiveTimeoutMs + attempt.StartTime))
                        continue;

                    // The other system might not be running the plugin; both are told.
                    SendUnresponsive(peer, attempt.Recipient.RakNetGuid, attempt.SessionId, attempt.Sender.SystemAddress);
                    SendUnresponsive(peer, attempt.Sender.RakNetGuid, attempt.SessionId, attempt.Recipient.SystemAddress);
                    attempt.Sender.IsReady = true;
                    attempt.Recipient.IsReady = true;
                    var recipient = attempt.Recipient;
                    DebugMessage?.Invoke($"Sending ID_NAT_TARGET_UNRESPONSIVE to sender {attempt.Sender.SystemAddress} and recipient {attempt.Recipient.SystemAddress}.");
                    attempt.Sender.DerefConnectionAttempt(attempt);
                    attempt.Recipient.DeleteConnectionAttempt(attempt);
                    StartPunchthroughForUser(peer, user);
                    StartPunchthroughForUser(peer, recipient);
                    break;
                }
            }
        }
    }

    /// <summary>OnReceive (NatPunchthroughServer.cpp:224-285).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.NatPunchthroughRequest:
                    OnNatPunchthroughRequest(peer, e.GetReader(), e.RakNetGuid, e.Address);
                    return PluginResult.Consume;
                case MessageId.NatGetMostRecentPort:
                    OnGetMostRecentPort(peer, e.GetReader(), e.RakNetGuid, e.Address);
                    return PluginResult.Consume;
                case MessageId.NatClientReady:
                    OnClientReady(peer, e.RakNetGuid);
                    return PluginResult.Consume;
                case MessageId.NatRequestBoundAddresses:
                    RespondBoundAddresses(peer, e.Address);
                    return PluginResult.Consume;
                case MessageId.OutOfBandInternal:
                    if (data.Length >= 2 && data[1] == (byte)OutOfBandId.NatPing)
                    {
                        OnNatPing(peer, e.GetReader(), e.Address);
                        return PluginResult.Consume;
                    }
                    break;
            }
        }
        return PluginResult.Continue;
    }

    // OnNATPunchthroughRequest (NatPunchthroughServer.cpp:379-422).
    private void OnNatPunchthroughRequest(RakPeer peer, BitStreamReader reader, RakNetGuid senderGuid, SystemAddress senderAddress)
    {
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var recipientGuid);
        // RakNet asserts the sender is a user and reads past the list when it is not.
        if (!_users.TryGetValue(senderGuid, out var sender))
            return;
        var attempt = new ConnectionAttempt(sender, _sessionId++);
        // RakNet compares the sender with the recipient before setting it, so the comparison never holds.
        if (!_users.TryGetValue(recipientGuid, out var recipient))
        {
            SendTargetGuid(peer, MessageId.NatTargetNotConnected, recipientGuid, senderAddress);
            return;
        }
        attempt.Recipient = recipient;
        if (recipient.HasConnectionAttemptToUser(sender))
        {
            SendTargetGuid(peer, MessageId.NatAlreadyInProgress, recipientGuid, senderAddress);
            return;
        }
        sender.ConnectionAttempts.Add(attempt);
        recipient.ConnectionAttempts.Add(attempt);
        StartPunchthroughForUser(peer, sender);
    }

    // OnClientReady (NatPunchthroughServer.cpp:423-433).
    private void OnClientReady(RakPeer peer, RakNetGuid guid)
    {
        if (!_users.TryGetValue(guid, out var user))
            return;
        user.IsReady = true;
        StartPunchthroughForUser(peer, user);
    }

    // OnGetMostRecentPort (NatPunchthroughServer.cpp:434-566).
    private void OnGetMostRecentPort(RakPeer peer, BitStreamReader reader, RakNetGuid guid, SystemAddress address)
    {
        reader.IgnoreBytes(1);
        reader.TryReadUInt16(out var sessionId);
        reader.TryReadUInt16(out var mostRecentPort);
        var found = _users.TryGetValue(guid, out var user);
        DebugMessage?.Invoke($"Got ID_NAT_GET_MOST_RECENT_PORT from systemAddress {address} guid {guid}. port={mostRecentPort}. sessionId={sessionId}. userFound={(found ? 1 : 0)}.");
        if (user is null)
        {
            DebugMessage?.Invoke($"Ignoring ID_NAT_GET_MOST_RECENT_PORT from systemAddress {address} guid {guid}");
            return;
        }
        user.MostRecentPort = mostRecentPort;
        var time = peer.GetTime();
        foreach (var attempt in user.ConnectionAttempts)
        {
            if (attempt.AttemptPhase != AttemptPhase.GettingRecentPorts || attempt.Sender.MostRecentPort == 0
                || attempt.Recipient.MostRecentPort == 0 || attempt.SessionId != sessionId)
                continue;

            var senderSystemAddress = attempt.Sender.SystemAddress;
            var recipientSystemAddress = attempt.Recipient.SystemAddress;
            var recipientTargetAddress = recipientSystemAddress.WithPort(attempt.Recipient.MostRecentPort);
            var senderTargetAddress = senderSystemAddress.WithPort(attempt.Sender.MostRecentPort);

            // A time far enough ahead that both systems have the message. The recipient's ping is looked up at its
            // address with the reported port, as RakNet does.
            var targetPing = AveragePing(peer, recipientTargetAddress);
            var senderPing = AveragePing(peer, senderSystemAddress);
            ulong simultaneousAttemptTime;
            if (targetPing == -1 || senderPing == -1)
            {
                simultaneousAttemptTime = time + 1500;
            }
            else
            {
                var largerPing = Math.Max(targetPing, senderPing);
                simultaneousAttemptTime = largerPing * 4 < 100 ? time + 100 : time + (ulong)(largerPing * 4);
            }

            DebugMessage?.Invoke($"Sending ID_NAT_CONNECT_AT_TIME to recipient systemAddress {recipientSystemAddress} guid {attempt.Recipient.RakNetGuid}");
            SendConnectAtTime(peer, simultaneousAttemptTime, attempt.SessionId, senderTargetAddress, senderSystemAddress,
                attempt.Sender.RakNetGuid, weAreSender: false, recipientSystemAddress);
            DebugMessage?.Invoke($"Sending ID_NAT_CONNECT_AT_TIME to sender systemAddress {senderSystemAddress} guid {attempt.Sender.RakNetGuid}");
            SendConnectAtTime(peer, simultaneousAttemptTime, attempt.SessionId, recipientTargetAddress, recipientSystemAddress,
                attempt.Recipient.RakNetGuid, weAreSender: true, senderSystemAddress);

            attempt.Recipient.DerefConnectionAttempt(attempt);
            attempt.Sender.DeleteConnectionAttempt(attempt);
            return;
        }
    }

    // StartPunchthroughForUser (NatPunchthroughServer.cpp:567-622): the first attempt whose other user is ready.
    private void StartPunchthroughForUser(RakPeer peer, User user)
    {
        if (!user.IsReady)
            return;
        foreach (var attempt in user.ConnectionAttempts)
        {
            User sender, recipient, otherUser;
            if (attempt.Sender == user)
            {
                otherUser = attempt.Recipient;
                sender = user;
                recipient = otherUser;
            }
            else
            {
                otherUser = attempt.Sender;
                recipient = user;
                sender = otherUser;
            }
            if (!otherUser.IsReady)
                continue;

            DebugMessage?.Invoke($"Sending NAT_ATTEMPT_PHASE_GETTING_RECENT_PORTS to sender {sender.SystemAddress} and recipient {recipient.SystemAddress}.");
            sender.IsReady = false;
            recipient.IsReady = false;
            attempt.AttemptPhase = AttemptPhase.GettingRecentPorts;
            attempt.StartTime = peer.GetTime();
            sender.MostRecentPort = 0;
            recipient.MostRecentPort = 0;

            Span<byte> request = stackalloc byte[1 + sizeof(ushort)];
            var writer = new BitStreamWriter(request);
            writer.WriteByte((byte)MessageId.NatGetMostRecentPort);
            // The session ID, so a returned port is not used for another attempt.
            writer.WriteUInt16(attempt.SessionId);
            Send(peer, writer, sender.SystemAddress);
            Send(peer, writer, recipient.SystemAddress);
            break;
        }
    }

    // ID_NAT_REQUEST_BOUND_ADDRESSES (NatPunchthroughServer.cpp:237-261): the addresses are taken once.
    private void RespondBoundAddresses(RakPeer peer, SystemAddress requester)
    {
        if (!_boundAddresses[0].IsAssigned)
        {
            for (var i = 0; i < peer.Sockets.Count && i < _boundAddresses.Length; i++)
            {
                _boundAddresses[i] = peer.Sockets[i].LocalAddress;
                _boundAddressCount++;
            }
        }
        Span<byte> response = stackalloc byte[2 + (_boundAddressCount * SystemAddress.WireSize)];
        var writer = new BitStreamWriter(response);
        writer.WriteByte((byte)MessageId.NatRespondBoundAddresses);
        writer.WriteByte(_boundAddressCount);
        for (var i = 0; i < _boundAddressCount; i++)
            writer.WriteAddress(_boundAddresses[i]);
        Send(peer, writer, requester);
    }

    // ID_OUT_OF_BAND_INTERNAL, ID_NAT_PING (NatPunchthroughServer.cpp:266-282).
    private static void OnNatPing(RakPeer peer, BitStreamReader reader, SystemAddress sender)
    {
        reader.IgnoreBytes(2);
        reader.TryReadUInt16(out var externalPort);
        Span<byte> pong = stackalloc byte[1 + (2 * sizeof(ushort))];
        var writer = new BitStreamWriter(pong);
        writer.WriteByte((byte)OutOfBandId.NatPong);
        writer.WriteUInt16(externalPort);
        writer.WriteUInt16(sender.Port);
        peer.LowLevel.SendOutOfBand(sender, writer.Written);
    }

    private static void SendConnectAtTime(RakPeer peer, ulong time, ushort sessionId, SystemAddress targetAddress,
                                          SystemAddress internalIdsOf, RakNetGuid targetGuid, bool weAreSender, SystemAddress destination)
    {
        const int internalIds = RakNetConstants.MaximumNumberOfInternalIds;
        Span<byte> message = stackalloc byte[1 + sizeof(ulong) + 1 + sizeof(ushort) + ((1 + internalIds) * SystemAddress.WireSize) + sizeof(ulong) + 1];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.Timestamp);
        writer.WriteUInt64(time);
        writer.WriteByte((byte)MessageId.NatConnectAtTime);
        writer.WriteUInt16(sessionId);
        // Public IP, using the most recent port.
        writer.WriteAddress(targetAddress);
        // Internal IPs.
        for (var j = 0; j < internalIds; j++)
            writer.WriteAddress(InternalId(peer, internalIdsOf, j));
        writer.WriteRakNetGuid(targetGuid);
        writer.WriteBool(weAreSender);
        Send(peer, writer, destination);
    }

    private static void SendUnresponsive(RakPeer peer, RakNetGuid guid, ushort sessionId, SystemAddress destination)
    {
        Span<byte> message = stackalloc byte[1 + sizeof(ulong) + sizeof(ushort)];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)MessageId.NatTargetUnresponsive);
        writer.WriteRakNetGuid(guid);
        writer.WriteUInt16(sessionId);
        Send(peer, writer, destination);
    }

    private static void SendTargetGuid(RakPeer peer, MessageId id, RakNetGuid guid, SystemAddress destination)
    {
        Span<byte> message = stackalloc byte[1 + sizeof(ulong)];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)id);
        writer.WriteRakNetGuid(guid);
        Send(peer, writer, destination);
    }

    // HIGH_PRIORITY, RELIABLE_ORDERED, channel 0; RakPeer::Send returns without sending to UNASSIGNED_SYSTEM_ADDRESS.
    private static void Send(RakPeer peer, BitStreamWriter writer, SystemAddress destination)
    {
        if (destination.IsAssigned)
            peer.Send(writer, destination, Reliability.ReliableOrdered, Priority.High, 0);
    }

    // RakPeer::GetAveragePing: -1 when the address has no connection or no ping yet.
    private static int AveragePing(RakPeer peer, SystemAddress address) =>
        peer.TryGetConnection(address, out var connection) && connection.AveragePing is { } ping ? (int)ping.TotalMilliseconds : -1;

    // RakPeer::GetInternalID(systemAddress, index): UNASSIGNED_SYSTEM_ADDRESS without a connection.
    private static SystemAddress InternalId(RakPeer peer, SystemAddress address, int index) =>
        peer.TryGetConnection(address, out var connection) && index < connection.RemoteInternalAddresses.Count
            ? connection.RemoteInternalAddresses[index]
            : SystemAddress.Unassigned;

    private enum AttemptPhase
    {
        NotStarted,
        GettingRecentPorts,
    }

    /// <summary>NatPunchthroughServer::ConnectionAttempt (NatPunchthroughServer.h:99-111).</summary>
    private sealed class ConnectionAttempt(User sender, ushort sessionId)
    {
        public User Sender { get; } = sender;

        public User Recipient { get; set; } = null!;

        public ushort SessionId { get; } = sessionId;

        public ulong StartTime { get; set; }

        public AttemptPhase AttemptPhase { get; set; }
    }

    /// <summary>NatPunchthroughServer::User (NatPunchthroughServer.h:112-124).</summary>
    private sealed class User(RakNetGuid rakNetGuid, SystemAddress systemAddress)
    {
        public RakNetGuid RakNetGuid { get; } = rakNetGuid;

        public SystemAddress SystemAddress { get; } = systemAddress;

        public ushort MostRecentPort { get; set; }

        public bool IsReady { get; set; } = true;

        public List<ConnectionAttempt> ConnectionAttempts { get; } = [];

        public bool HasConnectionAttemptToUser(User user) =>
            ConnectionAttempts.Exists(a => a.Recipient.RakNetGuid == user.RakNetGuid || a.Sender.RakNetGuid == user.RakNetGuid);

        // DeleteConnectionAttempt and DerefConnectionAttempt differ only in freeing the attempt.
        public void DerefConnectionAttempt(ConnectionAttempt attempt) => ConnectionAttempts.Remove(attempt);

        public void DeleteConnectionAttempt(ConnectionAttempt attempt) => ConnectionAttempts.Remove(attempt);
    }
}
