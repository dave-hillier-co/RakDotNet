using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Peer;

/// <summary>
/// The sans-IO core of a RakNet peer, ported from RakPeer.cpp: it routes <c>(endpoint, bytes)</c> in and out across
/// many connections, runs the offline and connected handshakes in both roles, and produces one ordered
/// <see cref="PeerEvent"/> stream.
/// </summary>
/// <remarks>
/// <para>
/// Times are microseconds and always passed in; RakNet::Time values written on the wire are those times in
/// milliseconds.
/// </para>
/// <para>
/// Inputs: <see cref="Receive"/> for each datagram the socket reads (the offline half of ProcessNetworkPacket runs
/// immediately; connected datagrams go to the connection's <see cref="ReliabilityLayer"/>), <see cref="Update"/> for
/// each tick of RunUpdateCycle (connection attempts, keepalive and connected pings, reliability updates, failure checks,
/// then the messages each connection delivered), and the commands <see cref="Connect"/>, <see cref="Send(IPEndPoint, ReadOnlySpan{byte}, Reliability, Priority, byte, long)"/>,
/// <see cref="CloseConnection(IPEndPoint, bool, byte, Priority, long)"/> and <see cref="Ping"/>. Commands apply at
/// once rather than being buffered for the next tick as RakPeer does across threads; their effects still leave on
/// the next <see cref="Update"/>, as RakPeer's do.
/// </para>
/// <para>
/// Outputs: <see cref="TryDequeueDatagram"/> and <see cref="TryDequeueEvent"/>.
/// </para>
/// <para>
/// The client probes a fixed MTU of 1492; MTU discovery, ban lists, connection rate limits, out-of-band messages and
/// security are not implemented yet.
/// </para>
/// </remarks>
internal sealed class PeerCore
{
    /// <summary>The MTU probed by OPEN_CONNECTION_REQUEST_1, the first of mtuSizes (RakPeer.cpp:104).</summary>
    public const int ProbeMtu = RakNetConstants.MaximumMtuSize;

    // How long a connection may stay half-open, RakPeer.cpp:5877-5881.
    private const long HandshakeTimeoutMs = 10_000;

    // RakPeer.cpp:5926: ping every 5 s until the first pong.
    private const long PingIntervalMs = 5_000;

    // defaultMTUSize, mtuSizes[NUM_MTU_SIZES-1] (RakPeer.cpp:206).
    private const int DefaultMtu = 576;

    private readonly PeerOptions _options;
    private readonly RemoteSystem?[] _slots;
    private readonly List<RemoteSystem> _active = [];
    private readonly List<ConnectionAttempt> _attempts = [];
    private readonly Queue<OutgoingDatagram> _datagrams = new();
    private readonly Queue<PeerEvent> _events = new();
    private byte[] _offlinePingResponse = [];
    private uint _nextSendReceipt = 1;

    public PeerCore(PeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConnections);
        if (options.LocalAddresses.Count != RakNetConstants.MaximumNumberOfInternalIds)
            throw new ArgumentException($"The local address list must have {RakNetConstants.MaximumNumberOfInternalIds} entries.", nameof(options));
        _options = options;
        _slots = new RemoteSystem?[options.MaxConnections];
        MaxIncomingConnections = Math.Min(options.MaxIncomingConnections, options.MaxConnections);
    }

    public ulong Guid => _options.Guid;

    /// <summary>SetMaximumIncomingConnections.</summary>
    public int MaxIncomingConnections { get; set; }

    /// <summary>SetOfflinePingResponse: the data appended to ID_UNCONNECTED_PONG, under 400 bytes (RakPeer.cpp:2271).</summary>
    public void SetOfflinePingResponse(ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(data.Length, RakNetConstants.MaxOfflineDataLength, nameof(data));
        _offlinePingResponse = data.ToArray();
    }

    public bool TryDequeueDatagram(out OutgoingDatagram datagram) => _datagrams.TryDequeue(out datagram);

    public bool TryDequeueEvent([NotNullWhen(true)] out PeerEvent? peerEvent) => _events.TryDequeue(out peerEvent);

    /// <summary>
    /// RakPeer::Connect and SendConnectionRequest (RakPeer.cpp:990-1018, 3266-3321): queue an offline handshake with
    /// <paramref name="remote"/>. The first OPEN_CONNECTION_REQUEST_1 leaves on the first <see cref="Update"/> after
    /// <paramref name="nowUs"/>.
    /// </summary>
    /// <param name="remote">The server.</param>
    /// <param name="password">Sent in ID_CONNECTION_REQUEST; at most 255 bytes are used.</param>
    /// <param name="attemptCount">sendConnectionAttemptCount: requests are sent until this many plus one have gone.</param>
    /// <param name="intervalMs">timeBetweenSendConnectionAttemptsMS.</param>
    /// <param name="timeoutMs">The connection's timeout, or 0 for <see cref="PeerOptions.TimeoutMs"/>.</param>
    /// <param name="nowUs">The current time.</param>
    public ConnectionAttemptResult Connect(IPEndPoint remote, ReadOnlySpan<byte> password, int attemptCount, int intervalMs, long timeoutMs, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (remote.Port == 0)
            return ConnectionAttemptResult.InvalidParameter;
        if (password.Length > 255)
            password = password[..255];
        if (FindActive(remote) is not null)
            return ConnectionAttemptResult.AlreadyConnectedToEndpoint;
        if (FindAttempt(remote) is not null)
            return ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress;

        _attempts.Add(new ConnectionAttempt(remote, password.ToArray(), attemptCount, intervalMs, timeoutMs, nowUs / 1000));
        return ConnectionAttemptResult.ConnectionAttemptStarted;
    }

    /// <summary>
    /// RakPeer::Ping(host, port, onlyReplyOnAcceptingConnections) (RakPeer.cpp:2090-2142): an unconnected ping.
    /// </summary>
    public bool Ping(IPEndPoint remote, bool onlyIfAccepting, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        SendOffline(remote, new UnconnectedPing(onlyIfAccepting, (ulong)(nowUs / 1000), Guid));
        return true;
    }

    /// <summary>
    /// RakPeer::Send (RakPeer.cpp:1308-1352): sends to a connected system and returns the receipt number used.
    /// Like RakPeer, a receipt is returned even when the target is not connected or the data is empty and nothing is
    /// sent (RakPeer.cpp:1317 rejects only a negative length; ReliabilityLayer.cpp:1567-1570 drops a 0-bit message).
    /// </summary>
    public uint Send(IPEndPoint remote, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var receipt = NextSendReceipt();
        var system = FindActive(remote);
        if (system is not null)
            SendImmediate(system, data, reliability, priority, channel, receipt, nowUs);
        return receipt;
    }

    /// <summary>Sends to the system with <paramref name="guid"/>.</summary>
    public uint Send(ulong guid, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, long nowUs)
    {
        // RakPeer.cpp:1323-1324: an undefined target returns 0 without using a receipt number.
        if (guid == RakNetConstants.UnassignedGuid)
            return 0;
        var system = FindActive(guid);
        if (system is null)
            return NextSendReceipt();
        return Send(system.Address, data, reliability, priority, channel, nowUs);
    }

    /// <summary>
    /// RakPeer::CloseConnection (RakPeer.cpp:1650-1679, 4117-4187, 3557-3571). With notification, ID_DISCONNECTION_NOTIFICATION
    /// is sent RELIABLE_ORDERED and the connection closes once everything sent has been acknowledged; both sides then
    /// see <see cref="Disconnected"/>. Without, the connection is dropped at once and this peer sees
    /// <see cref="ConnectionLost"/> if it was connected.
    /// </summary>
    public void CloseConnection(IPEndPoint remote, bool sendDisconnectionNotification, byte channel, Priority priority, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var system = FindActive(remote);
        if (system is null)
            return;

        if (sendDisconnectionNotification)
        {
            SendImmediate(system, Encode(new DisconnectionNotification()), Reliability.ReliableOrdered, priority, channel, 0, nowUs);
            system.Mode = ConnectMode.DisconnectAsap;
            return;
        }

        var wasConnected = system.Mode == ConnectMode.Connected;
        var guid = system.Guid;
        Deactivate(system);
        if (wasConnected)
            _events.Enqueue(new ConnectionLost(remote, guid));
    }

    /// <summary>Closes the connection to the system with <paramref name="guid"/>.</summary>
    public void CloseConnection(ulong guid, bool sendDisconnectionNotification, byte channel, Priority priority, long nowUs)
    {
        var system = FindActive(guid);
        if (system is not null)
            CloseConnection(system.Address, sendDisconnectionNotification, channel, priority, nowUs);
    }

    /// <summary>The number of remote systems with an active slot, in any connect mode.</summary>
    public int ActiveSystemCount => _active.Count;

    /// <summary>
    /// The first half of RakPeer::Shutdown (RakPeer.cpp:1050-1058): NotifyAndFlagForShutdown for every active system,
    /// so each closes once its ID_DISCONNECTION_NOTIFICATION is delivered. The caller keeps updating until
    /// <see cref="ActiveSystemCount"/> is 0 or its block time runs out.
    /// </summary>
    public void NotifyAllForShutdown(byte channel, Priority priority, long nowUs)
    {
        foreach (var system in _active.ToList())
            CloseConnection(system.Address, sendDisconnectionNotification: true, channel, priority, nowUs);
    }

    /// <summary>RakPeer::GetConnectionState for an address (RakPeer.cpp:1717-1770).</summary>
    public ConnectionState GetConnectionState(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (FindAttempt(remote) is not null)
            return ConnectionState.IsPending;
        // GetIndexFromSystemAddress(address, false) prefers an active slot, then one previously used by the address.
        var system = FindActive(remote) ?? Array.Find(_slots, s => s is not null && s.Address.Equals(remote));
        return StateOf(system);
    }

    /// <summary>RakPeer::GetConnectionState for a GUID; a closed connection's GUID is forgotten.</summary>
    public ConnectionState GetConnectionState(ulong guid) => StateOf(FindActive(guid));

    /// <summary>The address of the connected system with <paramref name="guid"/>.</summary>
    public bool TryGetAddress(ulong guid, [NotNullWhen(true)] out IPEndPoint? address)
    {
        address = FindActive(guid)?.Address;
        return address is not null;
    }

    /// <summary>ProcessNetworkPacket (RakPeer.cpp:5429-5470) for one datagram read from the socket.</summary>
    public void Receive(IPEndPoint from, ReadOnlySpan<byte> datagram, long nowUs)
    {
        ArgumentNullException.ThrowIfNull(from);
        if (ProcessOffline(from, datagram, nowUs))
            return;
        FindActive(from)?.Session.Receive(datagram, nowUs);
    }

    /// <summary>One RunUpdateCycle (RakPeer.cpp:5567-6253), after the datagrams it would have read were received.</summary>
    public void Update(long nowUs)
    {
        var nowMs = nowUs / 1000;
        UpdateConnectionAttempts(nowMs);

        foreach (var system in _active.ToList())
        {
            // Keepalive, RakPeer.cpp:5857-5869: a reliable ping so a dead connection is noticed.
            if (system.Mode == ConnectMode.Connected
                && nowMs > system.LastReliableSendMs
                && nowMs - system.LastReliableSendMs > system.Session.TimeoutMs / 2
                && system.Session.MessagesInResendBuffer == 0)
            {
                PingInternal(system, Reliability.Reliable, nowUs);
                system.LastReliableSendMs = nowMs;
            }

            system.Session.Update(nowUs);
            while (system.Session.TryDequeueDatagram(out var datagram))
                _datagrams.Enqueue(new OutgoingDatagram(system.Address, datagram));

            if (HasFailed(system, nowMs))
            {
                ReportClosed(system);
                Deactivate(system);
                continue;
            }

            // RakPeer.cpp:5924-5931: ping until the first pong arrives (occasionalPing is off by default).
            if (system.Mode == ConnectMode.Connected && nowMs > system.NextPingTimeMs && system.LowestPing == ushort.MaxValue)
            {
                system.NextPingTimeMs = nowMs + PingIntervalMs;
                PingInternal(system, Reliability.Unreliable, nowUs);
            }

            while (system.IsActive && system.Session.TryDequeueEvent(out var sessionEvent))
                ProcessSessionEvent(system, sessionEvent, nowUs);
        }
    }

    // RakPeer.cpp:5691-5829.
    private void UpdateConnectionAttempts(long nowMs)
    {
        foreach (var attempt in _attempts.ToList())
        {
            if (attempt.NextRequestTimeMs >= nowMs)
                continue;
            if (attempt.RequestsMade == attempt.AttemptCount + 1)
            {
                _attempts.Remove(attempt);
                _events.Enqueue(new ConnectionAttemptFailed(attempt.Address, RakNetConstants.UnassignedGuid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
                continue;
            }
            attempt.RequestsMade++;
            attempt.NextRequestTimeMs = nowMs + attempt.IntervalMs;
            // MTU discovery would step down mtuSizes as requests go unanswered; the probe stays at 1492 for now.
            SendOffline(attempt.Address, new OpenConnectionRequest1(RakNetConstants.ProtocolVersion, ProbeMtu), ProbeMtu);
        }
    }

    // The failure conditions of RakPeer.cpp:5874-5882.
    private static bool HasFailed(RemoteSystem system, long nowMs)
    {
        var session = system.Session;
        return session.IsDead
            || (system.Mode is ConnectMode.DisconnectAsap or ConnectMode.DisconnectAsapSilently && !session.IsOutgoingDataWaiting)
            || (system.Mode == ConnectMode.DisconnectOnNoAck && (!session.AreAcksWaiting || session.AckTimeout(nowMs)))
            || (system.Mode is ConnectMode.RequestedConnection or ConnectMode.HandlingConnectionRequest or ConnectMode.UnverifiedSender
                && nowMs > system.ConnectionTimeMs
                && nowMs - system.ConnectionTimeMs > HandshakeTimeoutMs);
    }

    // RakPeer.cpp:5889-5913.
    private void ReportClosed(RemoteSystem system)
    {
        switch (system.Mode)
        {
            case ConnectMode.RequestedConnection:
                _events.Enqueue(new ConnectionAttemptFailed(system.Address, system.Guid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
                break;
            case ConnectMode.Connected:
                _events.Enqueue(new ConnectionLost(system.Address, system.Guid));
                break;
            case ConnectMode.DisconnectAsap or ConnectMode.DisconnectOnNoAck:
                _events.Enqueue(new Disconnected(system.Address, system.Guid));
                break;
            default:
                break; // Shutting down silently, or never verified: the application is not told.
        }
    }

    private void ProcessSessionEvent(RemoteSystem system, SessionEvent sessionEvent, long nowUs)
    {
        switch (sessionEvent)
        {
            case MessageDelivered delivered:
                ProcessMessage(system, delivered.Payload, nowUs);
                break;
            case ReceiptAcknowledged acknowledged:
                _events.Enqueue(new SendReceiptAcked(system.Address, system.Guid, acknowledged.Receipt));
                break;
            case ReceiptLost lost:
                _events.Enqueue(new SendReceiptLost(system.Address, system.Guid, lost.Receipt));
                break;
            default:
                break; // ConnectionDied is acted on by the failure check.
        }
    }

    // The Receive loop of RunUpdateCycle, RakPeer.cpp:5939-6248.
    private void ProcessMessage(RemoteSystem system, ReadOnlyMemory<byte> payload, long nowUs)
    {
        var data = payload.Span;
        var id = (MessageId)data[0];
        var nowMs = nowUs / 1000;

        // Internal notifications never arrive from the network (RakPeer.cpp:5944-5949).
        if (id == MessageId.ConnectionAttemptFailed)
            return;

        if (system.Mode == ConnectMode.UnverifiedSender)
        {
            if (id == MessageId.ConnectionRequest)
                ParseConnectionRequest(system, data, nowUs);
            else
                Deactivate(system); // RakPeer also bans the address for the timeout; ban lists are not implemented yet.
            return;
        }

        switch (id)
        {
            case MessageId.ConnectionRequest:
                if (system.Mode == ConnectMode.RequestedConnection)
                    ParseConnectionRequest(system, data, nowUs);
                else
                    // Already connected: reply normally. The C++ reads the time from after a magic that is not there
                    // (RakPeer.cpp:5992-5997); that misreading is kept.
                    OnConnectionRequest(system, data.Length >= 33 ? BinaryPrimitives.ReadUInt64BigEndian(data[25..]) : 0, nowUs);
                break;

            // RakPeer.cpp:6005: longer than the ID, a 4-byte address, a port and two times.
            case MessageId.NewIncomingConnection when data.Length > 1 + 4 + 2 + 8 * 2:
                if (system.Mode == ConnectMode.HandlingConnectionRequest && TryDecode<NewIncomingConnection>(data, out var incoming))
                {
                    system.Mode = ConnectMode.Connected;
                    PingInternal(system, Reliability.Unreliable, nowUs);
                    system.TheirInternalAddresses = incoming.InternalAddresses;
                    OnConnectedPong(system, incoming.SendPingTime, incoming.SendPongTime, nowMs);
                    system.MyExternalAddress = incoming.ServerAddress;
                    _events.Enqueue(new IncomingConnectionCompleted(system.Address, system.Guid, payload));
                }
                break;

            case MessageId.ConnectedPong:
                if (TryDecode<ConnectedPong>(data, out var pong))
                    OnConnectedPong(system, pong.SendPingTime, pong.SendPongTime, nowMs);
                break;

            case MessageId.ConnectedPing:
                if (TryDecode<ConnectedPing>(data, out var ping))
                {
                    var reply = Encode(new ConnectedPong(ping.SendPingTime, (ulong)nowMs));
                    SendImmediate(system, reply, Reliability.Unreliable, Priority.Immediate, 0, 0, nowUs);
                }
                break;

            case MessageId.DisconnectionNotification:
                // Close once our ack of the notification has gone (RakPeer.cpp:6095-6102).
                system.Mode = ConnectMode.DisconnectOnNoAck;
                break;

            case MessageId.DetectLostConnections:
                break;

            case MessageId.InvalidPassword:
                if (system.Mode == ConnectMode.RequestedConnection)
                {
                    _events.Enqueue(new ConnectionAttemptFailed(system.Address, system.Guid, payload));
                    system.Mode = ConnectMode.DisconnectAsapSilently;
                }
                break;

            case MessageId.ConnectionRequestAccepted:
                OnConnectionRequestAccepted(system, payload, nowUs);
                break;

            default:
                // RakPeer.cpp:6226-6241: application messages and receipts; other internal IDs are dropped.
                if (data[0] >= (byte)MessageId.Timestamp || id is MessageId.SndReceiptAcked or MessageId.SndReceiptLoss)
                    _events.Enqueue(new MessageReceived(system.Address, system.Guid, payload));
                break;
        }
    }

    // RakPeer.cpp:6127-6220.
    private void OnConnectionRequestAccepted(RemoteSystem system, ReadOnlyMemory<byte> payload, long nowUs)
    {
        // Longer than the ID, a 4-byte address, a port, the system index and two times (RakPeer.cpp:6129).
        if (payload.Length <= 1 + 4 + 2 + 2 + 8 * 2)
            return;
        if (system.Mode is not (ConnectMode.HandlingConnectionRequest or ConnectMode.RequestedConnection))
            return;
        var alreadyConnected = system.Mode == ConnectMode.HandlingConnectionRequest;
        if (!TryDecode<ConnectionRequestAccepted>(payload.Span, out var accepted))
            return;

        var nowMs = nowUs / 1000;
        system.TheirInternalAddresses = accepted.InternalAddresses;
        OnConnectedPong(system, accepted.RequestTime, accepted.ReplyTime, nowMs);
        system.MyExternalAddress = accepted.ClientAddress;
        system.Mode = ConnectMode.Connected;
        _events.Enqueue(new OutgoingConnectionCompleted(system.Address, system.Guid, payload, accepted.ClientAddress));

        var reply = new NewIncomingConnection(system.Address, _options.LocalAddresses, accepted.ReplyTime, (ulong)nowMs);
        SendImmediate(system, Encode(reply), Reliability.ReliableOrdered, Priority.Immediate, 0, 0, nowUs);
        if (!alreadyConnected)
            PingInternal(system, Reliability.Unreliable, nowUs);
    }

    // ParseConnectionRequestPacket, RakPeer.cpp:3447-3538.
    private void ParseConnectionRequest(RemoteSystem system, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<ConnectionRequest>(data, out var request))
            return;
        if (!request.Password.Span.SequenceEqual(_options.IncomingPassword.Span))
        {
            SendImmediate(system, Encode(new InvalidPassword(Guid)), Reliability.Reliable, Priority.Immediate, 0, 0, nowUs);
            system.Mode = ConnectMode.DisconnectAsapSilently;
            return;
        }
        system.Mode = ConnectMode.HandlingConnectionRequest;
        OnConnectionRequest(system, request.RequestTime, nowUs);
    }

    // OnConnectionRequest, RakPeer.cpp:3540-3554.
    private void OnConnectionRequest(RemoteSystem system, ulong requestTime, long nowUs)
    {
        var accepted = new ConnectionRequestAccepted(system.Address, (ushort)system.Index, _options.LocalAddresses, requestTime, (ulong)(nowUs / 1000));
        SendImmediate(system, Encode(accepted), Reliability.ReliableOrdered, Priority.Immediate, 0, 0, nowUs);
    }

    // OnConnectedPong, RakPeer.cpp:4393-4415.
    private static void OnConnectedPong(RemoteSystem system, ulong sendPingTime, ulong sendPongTime, long nowMs)
    {
        var time = (ulong)nowMs;
        var ping = time > sendPingTime ? time - sendPingTime : 0;
        system.LastPing = (ushort)ping;
        system.ClockDifferential = (long)(sendPongTime - (time / 2 + sendPingTime / 2));
        if (system.LowestPing == ushort.MaxValue || system.LowestPing > (int)ping)
            system.LowestPing = (ushort)ping;
    }

    // PingInternal, RakPeer.cpp:4103-4115.
    private static void PingInternal(RemoteSystem system, Reliability reliability, long nowUs) =>
        SendImmediate(system, Encode(new ConnectedPing((ulong)(nowUs / 1000))), reliability, Priority.Immediate, 0, 0, nowUs);

    // SendImmediate for one system, RakPeer.cpp:4286-4384.
    private static void SendImmediate(RemoteSystem system, ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs)
    {
        if (!system.IsActive || system.Mode is ConnectMode.DisconnectAsap or ConnectMode.DisconnectAsapSilently or ConnectMode.DisconnectOnNoAck)
            return;
        system.Session.Send(data, reliability, priority, channel, receipt, nowUs);
        if (reliability.IsReliable())
            system.LastReliableSendMs = nowUs / 1000;
    }

    // IncrementNextSendReceipt, RakPeer.cpp:1279-1287: never 0.
    private uint NextSendReceipt()
    {
        var receipt = _nextSendReceipt;
        if (++_nextSendReceipt == 0)
            _nextSendReceipt = 1;
        return receipt;
    }

    // ProcessOfflineNetworkPacket, RakPeer.cpp:4540-5423. Returns true when the datagram was an offline message, which
    // never reaches a reliability layer.
    private bool ProcessOffline(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!IsOfflineMessage(data))
            return false;
        if (data.Length <= 2)
            return true;

        switch ((MessageId)data[0])
        {
            case MessageId.UnconnectedPing or MessageId.UnconnectedPingOpenConnections:
                OnUnconnectedPing(from, data);
                break;
            case MessageId.UnconnectedPong:
                OnUnconnectedPong(from, data);
                break;
            case MessageId.OpenConnectionReply1:
                OnOpenConnectionReply1(from, data);
                break;
            case MessageId.OpenConnectionReply2:
                OnOpenConnectionReply2(from, data, nowUs);
                break;
            case MessageId.ConnectionAttemptFailed
                or MessageId.NoFreeIncomingConnections
                or MessageId.ConnectionBanned
                or MessageId.AlreadyConnected
                or MessageId.IpRecentlyConnected
                or MessageId.IncompatibleProtocolVersion:
                OnRejection(from, data);
                break;
            case MessageId.OpenConnectionRequest1:
                OnOpenConnectionRequest1(from, data);
                break;
            case MessageId.OpenConnectionRequest2:
                OnOpenConnectionRequest2(from, data, nowUs);
                break;
            default:
                break; // ID_OUT_OF_BAND_INTERNAL and ID_ADVERTISE_SYSTEM are not implemented yet.
        }
        return true;
    }

    // RakPeer.cpp:4583-4627. The C++ checks the pong's length against a 4-byte TimeMS but its magic at the 8-byte
    // RakNet::Time offset; the magic is only compared here when the bytes are actually there.
    private static bool IsOfflineMessage(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 2)
            return true;
        const int guidSize = 8;
        const int timeSize = 8;
        switch ((MessageId)data[0])
        {
            case MessageId.UnconnectedPing or MessageId.UnconnectedPingOpenConnections:
                return MagicAt(data, 1 + timeSize);
            case MessageId.UnconnectedPong:
                return MagicAt(data, 1 + timeSize + guidSize);
            case MessageId.OutOfBandInternal:
                return MagicAt(data, 1 + guidSize);
            case MessageId.OpenConnectionReply1
                or MessageId.OpenConnectionReply2
                or MessageId.OpenConnectionRequest1
                or MessageId.OpenConnectionRequest2
                or MessageId.ConnectionAttemptFailed
                or MessageId.NoFreeIncomingConnections
                or MessageId.ConnectionBanned
                or MessageId.AlreadyConnected
                or MessageId.IpRecentlyConnected:
                return data.Length >= 1 + guidSize + OfflineMagic.Length && MagicAt(data, 1);
            case MessageId.IncompatibleProtocolVersion:
                return data.Length == 2 + guidSize + OfflineMagic.Length && MagicAt(data, 2);
            default:
                return false;
        }
    }

    private static bool MagicAt(ReadOnlySpan<byte> data, int offset) =>
        data.Length >= offset + OfflineMagic.Length && OfflineMagic.Matches(data[offset..]);

    // RakPeer.cpp:4635-4680.
    private void OnUnconnectedPing(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!TryDecode<UnconnectedPing>(data, out var ping))
            return;
        if (ping.OpenConnectionsOnly && !AllowIncomingConnections())
            return;
        SendOffline(from, new UnconnectedPong(ping.SendPingTime, Guid, _offlinePingResponse));
        _events.Enqueue(new UnconnectedPingReceived(from, ping.ClientGuid ?? RakNetConstants.UnassignedGuid, ping.OpenConnectionsOnly));
    }

    // RakPeer.cpp:4682-4705.
    private void OnUnconnectedPong(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (TryDecode<UnconnectedPong>(data, out var pong))
            _events.Enqueue(new UnconnectedPongReceived(from, pong.ServerGuid, pong.SendPingTime, pong.ResponseData));
    }

    // RakPeer.cpp:4737-4870: answer the server's MTU reply with OPEN_CONNECTION_REQUEST_2.
    private void OnOpenConnectionReply1(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!TryDecode<OpenConnectionReply1>(data, out var reply))
            return; // Includes a server that requires security, which is not supported yet.
        if (FindAttempt(from) is null)
            return;
        SendOffline(from, new OpenConnectionRequest2(from, reply.MtuSize, Guid));
    }

    // RakPeer.cpp:4871-5074: the offline handshake is done, so start the connected one.
    private void OnOpenConnectionReply2(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<OpenConnectionReply2>(data, out var reply))
            return;
        var attempt = FindAttempt(from);
        if (attempt is null)
            return;

        // A cross connection may already have made this system (RakPeer.cpp:4941-4954).
        var system = FindActive(from) ?? Assign(from, ConnectMode.UnverifiedSender, reply.MtuSize, reply.ServerGuid, nowUs);
        if (system is null)
        {
            _events.Enqueue(new ConnectionAttemptFailed(from, reply.ServerGuid, new[] { (byte)MessageId.ConnectionAttemptFailed }));
        }
        else
        {
            system.WeInitiatedTheConnection = true;
            system.Mode = ConnectMode.RequestedConnection;
            if (attempt.TimeoutMs != 0)
                system.Session.TimeoutMs = attempt.TimeoutMs;
            var request = new ConnectionRequest(Guid, (ulong)(nowUs / 1000), attempt.Password);
            SendImmediate(system, Encode(request), Reliability.Reliable, Priority.Immediate, 0, 0, nowUs);
        }
        _attempts.Remove(attempt);
    }

    // RakPeer.cpp:5075-5126: the server turned the attempt down.
    private void OnRejection(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        // Every rejection is ID, magic, GUID, except that ID_INCOMPATIBLE_PROTOCOL_VERSION has its version byte first.
        var guidOffset = (MessageId)data[0] == MessageId.IncompatibleProtocolVersion ? 2 + OfflineMagic.Length : 1 + OfflineMagic.Length;
        var guid = BinaryPrimitives.ReadUInt64BigEndian(data[guidOffset..]);
        var attempt = FindAttempt(from);
        if (attempt is null)
            return;
        _attempts.Remove(attempt);
        _events.Enqueue(new ConnectionAttemptFailed(from, guid, new[] { data[0] }));
    }

    // RakPeer.cpp:5127-5197: answer an MTU probe.
    private void OnOpenConnectionRequest1(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (data.Length <= 1 + OfflineMagic.Length)
            return;
        var remoteProtocol = data[1 + OfflineMagic.Length];
        if (remoteProtocol != RakNetConstants.ProtocolVersion)
        {
            SendOffline(from, new IncompatibleProtocolVersion(RakNetConstants.ProtocolVersion, Guid));
            return;
        }
        // Lower the MTU if it exceeds our own limit (RakPeer.cpp:5182-5186).
        var mtu = Math.Min(data.Length + RakNetConstants.UdpHeaderSize, RakNetConstants.MaximumMtuSize);
        SendOffline(from, new OpenConnectionReply1(Guid, (ushort)mtu));
    }

    // RakPeer.cpp:5198-5418: accept or refuse the connection.
    private void OnOpenConnectionRequest2(IPEndPoint from, ReadOnlySpan<byte> data, long nowUs)
    {
        if (!TryDecode<OpenConnectionRequest2>(data, out var request))
            return;

        var fromAddress = FindActive(from);
        var fromGuid = FindActive(request.ClientGuid);
        var addressInUse = fromAddress is not null;
        var guidInUse = fromGuid is not null;
        var reply = new OpenConnectionReply2(Guid, from, request.MtuSize);

        if (addressInUse && guidInUse)
        {
            if (ReferenceEquals(fromAddress, fromGuid) && fromAddress!.Mode == ConnectMode.UnverifiedSender)
            {
                // A duplicate request from packet loss: send the same answer (outcome 1).
                SendOffline(from, reply);
                return;
            }
            SendOffline(from, new AlreadyConnected(Guid)); // Outcome 2.
            return;
        }
        if (addressInUse || guidInUse)
        {
            // Someone else took this GUID (outcome 3), or the client restarted without disconnecting (outcome 4).
            SendOffline(from, new AlreadyConnected(Guid));
            return;
        }

        if (!AllowIncomingConnections())
        {
            SendOffline(from, new NoFreeIncomingConnections(Guid));
            return;
        }

        // The answer goes out even when no slot is free (RakPeer.cpp:5366-5417).
        Assign(from, ConnectMode.UnverifiedSender, request.MtuSize, request.ClientGuid, nowUs);
        SendOffline(from, reply);
    }

    // AllowIncomingConnections and GetNumberOfRemoteInitiatedConnections, RakPeer.cpp:4038-4041, 3573-3592.
    private bool AllowIncomingConnections() =>
        _active.Count(s => s.Mode == ConnectMode.Connected && !s.WeInitiatedTheConnection) < MaxIncomingConnections;

    // AssignSystemAddressToRemoteSystemList, RakPeer.cpp:3595-3770, without the same-IP rate limit.
    private RemoteSystem? Assign(IPEndPoint address, ConnectMode mode, int incomingMtu, ulong guid, long nowUs)
    {
        var index = Array.FindIndex(_slots, s => s is null || !s.IsActive);
        if (index < 0)
            return null;
        // The C++ asserts the MTU is at most MAXIMUM_MTU_SIZE (RakPeer.cpp:3644); larger values are clamped here.
        var mtu = Math.Clamp(incomingMtu, DefaultMtu, RakNetConstants.MaximumMtuSize);
        var session = new ReliabilityLayer(mtu, nowUs) { TimeoutMs = _options.TimeoutMs };
        var system = new RemoteSystem(index, address, guid, session, mode, nowUs / 1000);
        _slots[index] = system;
        _active.Add(system);
        return system;
    }

    // CloseConnectionInternal without notification (RakPeer.cpp:4148-4174): the slot keeps the address but forgets
    // the GUID.
    private void Deactivate(RemoteSystem system)
    {
        if (!system.IsActive)
            return;
        system.IsActive = false;
        system.Guid = RakNetConstants.UnassignedGuid;
        _active.Remove(system);
    }

    private static ConnectionState StateOf(RemoteSystem? system) =>
        system switch
        {
            null => ConnectionState.IsNotConnected,
            { IsActive: false } => ConnectionState.IsDisconnected,
            _ => system.Mode switch
            {
                ConnectMode.DisconnectAsap or ConnectMode.DisconnectOnNoAck => ConnectionState.IsDisconnecting,
                ConnectMode.DisconnectAsapSilently => ConnectionState.IsSilentlyDisconnecting,
                ConnectMode.RequestedConnection or ConnectMode.HandlingConnectionRequest or ConnectMode.UnverifiedSender =>
                    ConnectionState.IsConnecting,
                ConnectMode.Connected => ConnectionState.IsConnected,
                _ => ConnectionState.IsNotConnected,
            },
        };

    private RemoteSystem? FindActive(IPEndPoint address) => _active.Find(s => s.Address.Equals(address));

    private RemoteSystem? FindActive(ulong guid) =>
        guid == RakNetConstants.UnassignedGuid ? null : _active.Find(s => s.Guid == guid);

    private ConnectionAttempt? FindAttempt(IPEndPoint address) => _attempts.Find(a => a.Address.Equals(address));

    private void SendOffline<T>(IPEndPoint to, T message, int capacity = 512)
        where T : IWireMessage<T> =>
        _datagrams.Enqueue(new OutgoingDatagram(to, Encode(message, capacity)));

    private static byte[] Encode<T>(T message, int capacity = 512)
        where T : IWireMessage<T>
    {
        var writer = new BitStreamWriter(new byte[capacity]);
        message.Write(ref writer);
        return writer.Written.ToArray();
    }

    private static bool TryDecode<T>(ReadOnlySpan<byte> data, [MaybeNullWhen(false)] out T value)
        where T : IWireMessage<T>
    {
        var reader = new BitStreamReader(data);
        return T.TryRead(ref reader, out value);
    }

    /// <summary>
    /// RakPeer::CancelConnectionAttempt (RakPeer.cpp:1686-1710): forgets a queued attempt without an event. An attempt
    /// that already has a slot is unaffected.
    /// </summary>
    public void CancelConnectionAttempt(IPEndPoint remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        var attempt = FindAttempt(remote);
        if (attempt is not null)
            _attempts.Remove(attempt);
    }

    /// <summary>The slot of the active system with <paramref name="guid"/>, read by the layer above for its queries.</summary>
    public bool TryGetRemoteSystem(ulong guid, [NotNullWhen(true)] out RemoteSystem? system)
    {
        system = FindActive(guid);
        return system is not null;
    }
}
