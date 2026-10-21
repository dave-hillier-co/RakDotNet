using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// NatPunchthroughClient: opens a path through the routers between this system and another system connected to the
/// same <see cref="NatPunchthroughServer"/>, by both sending to each other at a time the server picks. It does not
/// connect; once ID_NAT_PUNCHTHROUGH_SUCCEEDED arrives either system can connect to the address it came from.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NatPunchthroughClient.cpp. <see cref="OpenNat"/> sends ID_NAT_PUNCHTHROUGH_REQUEST to the facilitator,
/// after learning the router's port stride with <see cref="FindRouterPortStride(SystemAddress)"/> when it is not known: the facilitator
/// lists its bound addresses, and an out-of-band ID_NAT_PING to its second address comes back as ID_NAT_PONG with the
/// two ports the facilitator saw. Asked for its most recent port (ID_NAT_GET_MOST_RECENT_PORT), the client answers with
/// its last known external port plus the stride. ID_NAT_CONNECT_AT_TIME then starts the attempt at the given time:
/// out-of-band ID_NAT_ESTABLISH_UNIDIRECTIONAL and the session ID to each of the other system's internal addresses,
/// then for each predicted external port (the reported one and the next <see cref="PunchthroughConfiguration.MaxPredictivePortRange"/>)
/// a send with a time-to-live of 2, which opens this system's router without reaching the other's, then the punches.
/// </para>
/// <para>
/// A system that receives the other's ID_NAT_ESTABLISH_UNIDIRECTIONAL keeps sending ID_NAT_ESTABLISH_BIDIRECTIONAL,
/// the session ID and the port it sends to, to that address; one that receives ID_NAT_ESTABLISH_BIDIRECTIONAL answers
/// it once and has succeeded. Success and failure are pushed at the head of the stream as generated locally:
/// ID_NAT_PUNCHTHROUGH_SUCCEEDED or ID_NAT_PUNCHTHROUGH_FAILED and 1 when this system asked, 0 when it was asked, from
/// the other system's address and GUID. The facilitator's ID_NAT_TARGET_NOT_CONNECTED, ID_NAT_TARGET_UNRESPONSIVE,
/// ID_NAT_CONNECTION_TO_TARGET_LOST and ID_NAT_ALREADY_IN_PROGRESS reach the application as they arrive. After each
/// attempt the client tells the facilitator it is ready with ID_NAT_CLIENT_READY.
/// </para>
/// <para>
/// RakNet's quirks are kept: every out-of-band message other than ID_NAT_ESTABLISH_* for another session is consumed;
/// the external phases from port 1024 are skipped as RakNet skips them; the remaining failed attempts are reported
/// with the current attempt's address and GUID when the facilitator disconnects. Times are RakNet::Time milliseconds
/// on the peer's clock; the start time arrives in ID_TIMESTAMP, already on it.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class NatPunchthroughClient : IRakPlugin
{
    private const ulong PortStrideCalculationTimeoutMs = 5000;
    private const int MaximumNumberOfInternalIds = RakNetConstants.MaximumNumberOfInternalIds;

    private readonly Lock _gate = new();
    private readonly List<(SystemAddress Address, RakNetGuid RakNetGuid)> _failedAttemptList = [];
    private readonly Queue<(RakNetGuid Destination, SystemAddress Facilitator)> _queuedOpenNat = new();
    private readonly SendPing _sp = new();
    private IPluginHost? _host;
    private ushort _mostRecentExternalPort;
    private ushort _portStride;
    private PortStrideState _hasPortStride = PortStrideState.Unknown;
    private ulong _portStrideCalTimeout;

    /// <summary>The punchthrough phases of SendPing::TestMode (NatPunchthroughClient.h:178-195).</summary>
    private enum TestMode
    {
        TestingInternalIps,
        WaitingForInternalIpsResponse,
        TestingExternalIpsFacilitatorPortToFacilitatorPort,
        TestingExternalIps1024ToFacilitatorPort,
        TestingExternalIpsFacilitatorPortTo1024,
        TestingExternalIps1024To1024,
        WaitingAfterAllAttempts,
        PunchingFixedPort,
    }

    private enum PortStrideState
    {
        HasPortStride,
        Unknown,
        Calculating,
        Incapable,
    }

    /// <summary>GetPunchthroughConfiguration.</summary>
    public PunchthroughConfiguration Configuration { get; } = new();

    /// <summary>SetDebugInterface: receives RakNet's diagnostic messages; null for none.</summary>
    public Action<string>? DebugMessage { get; set; }

    /// <summary>
    /// FindRouterPortStride: learns how this system's router steps its ports, from a facilitator bound to two or more
    /// addresses. Called by <see cref="OpenNat"/> when needed; calling it early, once connected, speeds that up.
    /// Nothing happens unless <paramref name="facilitator"/> is connected and the stride is unknown.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void FindRouterPortStride(SystemAddress facilitator)
    {
        lock (_gate)
            StartFindingPortStride(Peer, facilitator);
    }

    /// <summary>
    /// OpenNAT: punches through to <paramref name="destination"/>, a system connected to
    /// <paramref name="facilitator"/>, which this system must be connected to. False, sending nothing, when it is not.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool OpenNat(RakNetGuid destination, SystemAddress facilitator)
    {
        lock (_gate)
        {
            var peer = Peer;
            if (!IsConnected(peer, facilitator))
                return false;
            switch (_hasPortStride)
            {
                case PortStrideState.Unknown:
                    StartFindingPortStride(peer, facilitator);
                    _queuedOpenNat.Enqueue((destination, facilitator));
                    break;
                case PortStrideState.Calculating:
                    _queuedOpenNat.Enqueue((destination, facilitator));
                    break;
                default:
                    SendPunchthrough(peer, destination, facilitator);
                    break;
            }
            return true;
        }
    }

    /// <summary>
    /// GetUPNPPortMappings: the external port to map, this system's most recent external port (as
    /// <paramref name="natPunchthroughServerAddress"/> sees it when none is known yet), and the internal port, that of
    /// the peer's first socket.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public (ushort ExternalPort, ushort InternalPort) GetUpnpPortMappings(SystemAddress natPunchthroughServerAddress)
    {
        lock (_gate)
        {
            var peer = Peer;
            if (_mostRecentExternalPort == 0)
                _mostRecentExternalPort = ExternalAddress(peer, natPunchthroughServerAddress).Port;
            return (_mostRecentExternalPort, peer.Sockets[0].LocalAddress.Port);
        }
    }

    /// <summary>Clear: ends the attempt in progress, telling the facilitator, and forgets failures and queued requests.</summary>
    public void Clear()
    {
        lock (_gate)
            ClearState();
    }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The NatPunchthroughClient is not attached to a RakPeer.");

    /// <summary>OnAttach: Clear.</summary>
    void IRakPlugin.OnAttached(IPluginHost host)
    {
        lock (_gate)
        {
            _host = host;
            ClearState();
        }
    }

    /// <summary>OnDetach: Clear.</summary>
    void IRakPlugin.OnDetached()
    {
        lock (_gate)
        {
            ClearState();
            _host = null;
        }
    }

    /// <summary>OnRakPeerShutdown: Clear.</summary>
    void IRakPlugin.OnShutdown() => Clear();

    /// <summary>OnNewConnection (NatPunchthroughClient.cpp:863-894): the first external port seen is the most recent.</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        lock (_gate)
        {
            var ourExternalId = ExternalAddress(connection.Peer, connection.Address);
            if (ourExternalId.IsAssigned && _mostRecentExternalPort == 0)
            {
                _mostRecentExternalPort = ourExternalId.Port;
                DebugMessage?.Invoke($"OnNewConnection mostRecentExternalPort first time set to {_mostRecentExternalPort}");
            }
        }
    }

    /// <summary>
    /// OnClosedConnection (NatPunchthroughClient.cpp:896-937): losing the facilitator reports every earlier failure not
    /// in progress.
    /// </summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            if (_host?.Peer is not { } peer || _sp.Facilitator != connection.Address)
                return;
            var i = 0;
            while (i < _failedAttemptList.Count)
            {
                if (_sp.NextActionTime != 0 && _sp.TargetGuid == _failedAttemptList[i].RakNetGuid)
                {
                    i++;
                    continue;
                }
                PushFailure(peer);
                RemoveAtIndexFast(_failedAttemptList, i);
            }
        }
    }

    /// <summary>Update (NatPunchthroughClient.cpp:119-334).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        lock (_gate)
            Update(peer);
    }

    /// <summary>OnReceive (NatPunchthroughClient.cpp:429-722).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.NatGetMostRecentPort:
                    OnGetMostRecentPort(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                case MessageId.NatPunchthroughFailed:
                case MessageId.NatPunchthroughSucceeded:
                    return e.IsLocal ? PluginResult.Continue : PluginResult.Consume;
                case MessageId.NatRespondBoundAddresses:
                    OnRespondBoundAddresses(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                case MessageId.OutOfBandInternal:
                    return OnOutOfBand(peer, e.GetReader(), data, e.Address, e.RakNetGuid);
                case MessageId.NatAlreadyInProgress:
                    {
                        var reader = e.GetReader();
                        reader.IgnoreBytes(1);
                        reader.TryReadRakNetGuid(out var targetGuid);
                        DebugMessage?.Invoke($"Punchthrough retry to guid {targetGuid} failed due to ID_NAT_ALREADY_IN_PROGRESS. Returning failure.");
                    }
                    return PluginResult.Continue;
                case MessageId.NatTargetNotConnected:
                case MessageId.NatConnectionToTargetLost:
                case MessageId.NatTargetUnresponsive:
                    OnTargetFailure(e.GetReader(), (MessageId)data[0]);
                    return PluginResult.Continue;
                case MessageId.Timestamp:
                    if (data.Length > 1 + sizeof(ulong) && data[1 + sizeof(ulong)] == (byte)MessageId.NatConnectAtTime)
                    {
                        OnConnectAtTime(e.GetReader());
                        return PluginResult.Consume;
                    }
                    return PluginResult.Continue;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    private void Update(RakPeer peer)
    {
        var time = peer.GetTime();
        if (_hasPortStride == PortStrideState.Calculating && time > _portStrideCalTimeout)
        {
            DebugMessage?.Invoke("CALCULATING_PORT_STRIDE timeout");
            SendQueuedOpenNat(peer);
            _hasPortStride = PortStrideState.Unknown;
        }

        if (_sp.NextActionTime == 0 || !(_sp.NextActionTime < time))
            return;
        var delta = time - _sp.NextActionTime;
        switch (_sp.TestMode)
        {
            case TestMode.TestingInternalIps:
                SendOutOfBand(peer, _sp.AttemptCount < MaximumNumberOfInternalIds ? _sp.InternalIds[_sp.AttemptCount] : SystemAddress.Unassigned, OutOfBandId.NatEstablishUnidirectional);
                if (++_sp.RetryCount >= Configuration.UdpSendsPerPortInternal)
                {
                    ++_sp.AttemptCount;
                    _sp.RetryCount = 0;
                }
                if (_sp.AttemptCount >= Configuration.MaximumNumberOfInternalIdsToCheck)
                {
                    _sp.TestMode = TestMode.WaitingForInternalIpsResponse;
                    if (Configuration.InternalIpWaitAfterAttempts > TimeSpan.Zero)
                    {
                        _sp.NextActionTime = After(time, Configuration.InternalIpWaitAfterAttempts, delta);
                    }
                    else
                    {
                        _sp.TestMode = TestMode.TestingExternalIpsFacilitatorPortToFacilitatorPort;
                        _sp.AttemptCount = 0;
                        _sp.SentTtl = false;
                    }
                }
                else
                {
                    _sp.NextActionTime = After(time, Configuration.TimeBetweenPunchAttemptsInternal, delta);
                }
                break;
            case TestMode.WaitingForInternalIpsResponse:
                _sp.TestMode = TestMode.TestingExternalIpsFacilitatorPortToFacilitatorPort;
                _sp.AttemptCount = 0;
                _sp.SentTtl = false;
                break;
            case TestMode.TestingExternalIpsFacilitatorPortToFacilitatorPort:
                {
                    var sa = _sp.TargetAddress.WithPort(unchecked((ushort)(_sp.TargetAddress.Port + _sp.AttemptCount)));
                    if (!_sp.SentTtl)
                    {
                        SendTtl(peer, sa);
                        DebugMessage?.Invoke($"Send with TTL 2 to {sa}");
                        _sp.NextActionTime = After(time, Configuration.ExternalIpWaitAfterFirstTtl, delta);
                        _sp.SentTtl = true;
                    }
                    else
                    {
                        SendOutOfBand(peer, sa, OutOfBandId.NatEstablishUnidirectional);
                        IncrementExternalAttemptCount(time, delta);
                        if (_sp.AttemptCount > Configuration.MaxPredictivePortRange)
                        {
                            // The phases from port 1024 are skipped, as RakNet skips them.
                            _sp.TestMode = TestMode.WaitingAfterAllAttempts;
                            _sp.NextActionTime = After(time, Configuration.ExternalIpWaitAfterAllAttempts, delta);
                        }
                    }
                }
                break;
            case TestMode.TestingExternalIps1024ToFacilitatorPort:
                {
                    var port = _sp.TargetGuid < peer.RakNetGuid ? 1024 + _sp.AttemptCount : _sp.TargetAddress.Port + _sp.AttemptCount;
                    SendOutOfBand(peer, _sp.TargetAddress.WithPort(unchecked((ushort)port)), OutOfBandId.NatEstablishUnidirectional);
                    IncrementExternalAttemptCount(time, delta);
                    if (_sp.AttemptCount > Configuration.MaxPredictivePortRange)
                    {
                        _sp.TestMode = TestMode.TestingExternalIpsFacilitatorPortTo1024;
                        _sp.AttemptCount = 0;
                    }
                }
                break;
            case TestMode.TestingExternalIpsFacilitatorPortTo1024:
                {
                    var port = _sp.TargetGuid > peer.RakNetGuid ? 1024 + _sp.AttemptCount : _sp.TargetAddress.Port + _sp.AttemptCount;
                    SendOutOfBand(peer, _sp.TargetAddress.WithPort(unchecked((ushort)port)), OutOfBandId.NatEstablishUnidirectional);
                    IncrementExternalAttemptCount(time, delta);
                    if (_sp.AttemptCount > Configuration.MaxPredictivePortRange)
                    {
                        _sp.TestMode = TestMode.TestingExternalIps1024To1024;
                        _sp.AttemptCount = 0;
                    }
                }
                break;
            case TestMode.TestingExternalIps1024To1024:
                SendOutOfBand(peer, _sp.TargetAddress.WithPort(unchecked((ushort)(1024 + _sp.AttemptCount))), OutOfBandId.NatEstablishUnidirectional);
                IncrementExternalAttemptCount(time, delta);
                if (_sp.AttemptCount > Configuration.MaxPredictivePortRange)
                {
                    DebugMessage?.Invoke($"Likely bidirectional punchthrough failure to guid {_sp.TargetGuid}, system address {_sp.TargetAddress}.");
                    _sp.TestMode = TestMode.WaitingAfterAllAttempts;
                    _sp.NextActionTime = After(time, Configuration.ExternalIpWaitAfterAllAttempts, delta);
                }
                break;
            case TestMode.WaitingAfterAllAttempts:
                // Failed; tell the user.
                OnPunchthroughFailure(peer);
                break;
        }

        if (_sp.TestMode == TestMode.PunchingFixedPort)
        {
            SendOutOfBand(peer, _sp.TargetAddress, OutOfBandId.NatEstablishBidirectional);
            if (++_sp.RetryCount >= _sp.PunchingFixedPortAttempts)
            {
                DebugMessage?.Invoke($"Likely unidirectional punchthrough failure to guid {_sp.TargetGuid}, system address {_sp.TargetAddress}.");
                _sp.TestMode = TestMode.WaitingAfterAllAttempts;
                _sp.NextActionTime = After(time, Configuration.ExternalIpWaitAfterAllAttempts, delta);
            }
            else if (_sp.RetryCount % Configuration.UdpSendsPerPortExternal == 0)
            {
                _sp.NextActionTime = After(time, Configuration.ExternalIpWaitBetweenPorts, delta);
            }
            else
            {
                _sp.NextActionTime = After(time, Configuration.TimeBetweenPunchAttemptsExternal, delta);
            }
        }
    }

    // ID_NAT_RESPOND_BOUND_ADDRESSES (NatPunchthroughClient.cpp:443-472): ping the first address other than the one
    // that answered, with this system's external port as the facilitator sees it.
    private void OnRespondBoundAddresses(RakPeer peer, BitStreamReader reader, SystemAddress facilitator)
    {
        reader.IgnoreBytes(1);
        reader.TryReadByte(out var boundAddressCount);
        if (boundAddressCount < 2)
        {
            DebugMessage?.Invoke($"INCAPABLE_PORT_STRIDE. My external ID is {ExternalAddress(peer, facilitator)}");
            _hasPortStride = PortStrideState.Incapable;
            SendQueuedOpenNat(peer);
        }
        for (var i = 0; i < boundAddressCount && i < MaximumNumberOfInternalIds; i++)
        {
            reader.TryReadSystemAddress(out var boundAddress);
            if (boundAddress == facilitator)
                continue;
            Span<byte> ping = stackalloc byte[1 + sizeof(ushort)];
            var writer = new BitStreamWriter(ping);
            writer.WriteByte((byte)OutOfBandId.NatPing);
            writer.WriteUInt16(ExternalAddress(peer, facilitator).Port);
            if (boundAddress.IsAssigned)
                peer.LowLevel.SendOutOfBand(boundAddress, writer.Written);
            break;
        }
    }

    // ID_OUT_OF_BAND_INTERNAL (NatPunchthroughClient.cpp:473-599).
    private PluginResult OnOutOfBand(RakPeer peer, BitStreamReader reader, ReadOnlySpan<byte> data, SystemAddress sender, RakNetGuid senderGuid)
    {
        if (data.Length >= 2 && data[1] == (byte)OutOfBandId.NatPong)
        {
            reader.IgnoreBytes(2);
            reader.TryReadUInt16(out var externalPort);
            reader.TryReadUInt16(out var externalPort2);
            _portStride = unchecked((ushort)(externalPort2 - externalPort));
            _mostRecentExternalPort = externalPort2;
            _hasPortStride = PortStrideState.HasPortStride;
            DebugMessage?.Invoke($"HAS_PORT_STRIDE {_portStride}. First external port {externalPort}. Second external port {externalPort2}.");
            SendQueuedOpenNat(peer);
            return PluginResult.Consume;
        }
        if (data.Length >= 2 && data[1] is (byte)OutOfBandId.NatEstablishUnidirectional or (byte)OutOfBandId.NatEstablishBidirectional
            && _sp.NextActionTime != 0)
        {
            reader.IgnoreBytes(2);
            reader.TryReadUInt16(out var sessionId);
            if (sessionId != _sp.SessionId)
                return PluginResult.Continue;

            // sp.targetGuid==packet->guid, because the internal addresses may include loopbacks.
            if (data[1] == (byte)OutOfBandId.NatEstablishUnidirectional && _sp.TargetGuid == senderGuid)
            {
                if (_sp.TestMode != TestMode.PunchingFixedPort)
                {
                    _sp.TestMode = TestMode.PunchingFixedPort;
                    _sp.RetryCount += _sp.AttemptCount * Configuration.UdpSendsPerPortExternal;
                    _sp.TargetAddress = sender;
                    // Keeps trying until the other side gives up too, in case it is unidirectional.
                    _sp.PunchingFixedPortAttempts = Configuration.UdpSendsPerPortExternal * (Configuration.MaxPredictivePortRange + 1);
                    DebugMessage?.Invoke($"PUNCHING_FIXED_PORT: Received ID_NAT_ESTABLISH_UNIDIRECTIONAL from guid {_sp.TargetGuid}, system address {sender}.");
                }
                else
                {
                    DebugMessage?.Invoke($"Received ID_NAT_ESTABLISH_UNIDIRECTIONAL from guid {_sp.TargetGuid}, system address {sender}.");
                }
                SendOutOfBand(peer, _sp.TargetAddress, OutOfBandId.NatEstablishBidirectional);
            }
            else if (data[1] == (byte)OutOfBandId.NatEstablishBidirectional && _sp.TargetGuid == senderGuid)
            {
                OnEstablishBidirectional(peer, reader, sender);
            }
        }
        return PluginResult.Consume;
    }

    // The other system heard this one: it sends back the port it sent to, our external port.
    private void OnEstablishBidirectional(RakPeer peer, BitStreamReader reader, SystemAddress sender)
    {
        reader.TryReadUInt16(out var ourExternalPort);
        if (_mostRecentExternalPort == 0)
        {
            _mostRecentExternalPort = ourExternalPort;
            DebugMessage?.Invoke($"ID_NAT_ESTABLISH_BIDIRECTIONAL mostRecentExternalPort first time set to {_mostRecentExternalPort}");
        }
        else if (_sp.TestMode != TestMode.TestingInternalIps && _sp.TestMode != TestMode.WaitingForInternalIpsResponse)
        {
            if (_hasPortStride != PortStrideState.HasPortStride)
            {
                _portStride = unchecked((ushort)(ourExternalPort - _mostRecentExternalPort));
                _hasPortStride = PortStrideState.HasPortStride;
                DebugMessage?.Invoke($"ID_NAT_ESTABLISH_BIDIRECTIONAL: Estimated port stride from incoming connection at {_portStride}. ourExternalPort={ourExternalPort} mostRecentExternalPort={_mostRecentExternalPort}");
                SendQueuedOpenNat(peer);
            }
            _mostRecentExternalPort = ourExternalPort;
            DebugMessage?.Invoke($"ID_NAT_ESTABLISH_BIDIRECTIONAL: New mostRecentExternalPort {_mostRecentExternalPort}");
        }
        SendOutOfBand(peer, sender, OutOfBandId.NatEstablishBidirectional);

        // Tell the user about the success.
        _sp.TargetAddress = sender;
        PushSuccess(peer);
        OnReadyForNextPunchthrough(peer);
        var removedFromFailureQueue = RemoveFromFailureQueue();
        DebugMessage?.Invoke(removedFromFailureQueue
            ? $"Punchthrough to guid {_sp.TargetGuid}, system address {sender} succeeded on 2nd attempt."
            : $"Punchthrough to guid {_sp.TargetGuid}, system address {sender} succeeded on 1st attempt.");
    }

    // ID_NAT_TARGET_NOT_CONNECTED, ID_NAT_CONNECTION_TO_TARGET_LOST, ID_NAT_TARGET_UNRESPONSIVE (NatPunchthroughClient.cpp:617-712).
    private void OnTargetFailure(BitStreamReader reader, MessageId id)
    {
        var reason = id switch
        {
            MessageId.NatTargetNotConnected => "ID_NAT_TARGET_NOT_CONNECTED",
            MessageId.NatConnectionToTargetLost => "ID_NAT_CONNECTION_TO_TARGET_LOST",
            _ => "ID_NAT_TARGET_UNRESPONSIVE",
        };
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var targetGuid);
        if (id is MessageId.NatConnectionToTargetLost or MessageId.NatTargetUnresponsive)
        {
            reader.TryReadUInt16(out var sessionId);
            if (sessionId != _sp.SessionId)
                return;
        }

        // A retry whose target is gone cannot be retried; the message itself reports the failure.
        var i = _failedAttemptList.FindIndex(f => f.RakNetGuid == targetGuid);
        if (i >= 0)
        {
            DebugMessage?.Invoke($"Punchthrough retry to guid {targetGuid} failed due to {reason}.");
            RemoveAtIndexFast(_failedAttemptList, i);
        }
        DebugMessage?.Invoke($"Punchthrough attempt to guid {targetGuid} failed due to {reason}.");

        // Stop trying punchthrough.
        _sp.NextActionTime = 0;
    }

    // OnConnectAtTime (NatPunchthroughClient.cpp:737-781).
    private void OnConnectAtTime(BitStreamReader reader)
    {
        reader.IgnoreBytes(1);
        reader.TryReadUInt64(out var nextActionTime);
        _sp.NextActionTime = nextActionTime;
        reader.IgnoreBytes(1);
        reader.TryReadUInt16(out var sessionId);
        _sp.SessionId = sessionId;
        reader.TryReadSystemAddress(out var targetAddress);
        _sp.TargetAddress = targetAddress;
        for (var j = 0; j < MaximumNumberOfInternalIds; j++)
        {
            reader.TryReadSystemAddress(out var internalId);
            _sp.InternalIds[j] = internalId;
        }
        _sp.AttemptCount = 0;
        _sp.RetryCount = 0;
        if (Configuration.MaximumNumberOfInternalIdsToCheck > 0)
        {
            _sp.TestMode = TestMode.TestingInternalIps;
        }
        else
        {
            _sp.TestMode = TestMode.TestingExternalIpsFacilitatorPortToFacilitatorPort;
            _sp.AttemptCount = 0;
            _sp.SentTtl = false;
        }
        reader.TryReadRakNetGuid(out var targetGuid);
        _sp.TargetGuid = targetGuid;
        reader.TryReadBool(out var weAreSender);
        _sp.WeAreSender = weAreSender;
    }

    // OnGetMostRecentPort (NatPunchthroughClient.cpp:971-1001): the facilitator asking also becomes the attempt's.
    private void OnGetMostRecentPort(RakPeer peer, BitStreamReader reader, SystemAddress facilitator)
    {
        reader.IgnoreBytes(1);
        reader.TryReadUInt16(out var sessionId);
        if (_mostRecentExternalPort == 0)
        {
            _mostRecentExternalPort = ExternalAddress(peer, facilitator).Port;
            DebugMessage?.Invoke($"OnGetMostRecentPort mostRecentExternalPort first time set to {_mostRecentExternalPort}");
        }
        var portWithStride = _hasPortStride == PortStrideState.HasPortStride
            ? unchecked((ushort)(_mostRecentExternalPort + _portStride))
            : _mostRecentExternalPort;
        Span<byte> reply = stackalloc byte[1 + (2 * sizeof(ushort))];
        var writer = new BitStreamWriter(reply);
        writer.WriteByte((byte)MessageId.NatGetMostRecentPort);
        writer.WriteUInt16(sessionId);
        writer.WriteUInt16(portWithStride);
        SendReliable(peer, writer, facilitator);
        _sp.Facilitator = facilitator;
    }

    // OnPunchthroughFailure (NatPunchthroughClient.cpp:349-428).
    private void OnPunchthroughFailure(RakPeer peer)
    {
        if (!Configuration.RetryOnFailure)
        {
            DebugMessage?.Invoke($"Failed punchthrough once. Returning failure to guid {_sp.TargetGuid}, system address {_sp.TargetAddress} to user.");
            PushFailure(peer);
            OnReadyForNextPunchthrough(peer);
            return;
        }

        var i = _failedAttemptList.FindIndex(f => f.RakNetGuid == _sp.TargetGuid);
        if (i >= 0)
        {
            DebugMessage?.Invoke($"Failed punchthrough twice. Returning failure to guid {_sp.TargetGuid}, system address {_sp.TargetAddress} to user.");
            // Failed a second time, so return failed to user.
            PushFailure(peer);
            OnReadyForNextPunchthrough(peer);
            RemoveAtIndexFast(_failedAttemptList, i);
            return;
        }

        if (!IsConnected(peer, _sp.Facilitator))
        {
            DebugMessage?.Invoke($"Not connected to facilitator, so cannot retry punchthrough after first failure. Returning failure onj guid {_sp.TargetGuid}, system address {_sp.TargetAddress} to user.");
            // Failed, and cannot try again because there is no facilitator.
            PushFailure(peer);
            return;
        }

        DebugMessage?.Invoke($"First punchthrough failure on guid {_sp.TargetGuid}, system address {_sp.TargetAddress}. Reattempting.");
        // Failed the first time: add to the failure queue and try again.
        _failedAttemptList.Add((_sp.TargetAddress, _sp.TargetGuid));
        // Tell the server we are ready.
        OnReadyForNextPunchthrough(peer);
        // As the sender, try again, at once if possible, else queued on the facilitator.
        if (_sp.WeAreSender)
            SendPunchthrough(peer, _sp.TargetGuid, _sp.Facilitator);
    }

    private void StartFindingPortStride(RakPeer peer, SystemAddress facilitator)
    {
        if (!IsConnected(peer, facilitator) || _hasPortStride != PortStrideState.Unknown)
            return;
        _hasPortStride = PortStrideState.Calculating;
        _portStrideCalTimeout = peer.GetTime() + PortStrideCalculationTimeoutMs;
        DebugMessage?.Invoke($"Calculating port stride from {facilitator}");
        ReadOnlySpan<byte> request = [(byte)MessageId.NatRequestBoundAddresses];
        peer.Send(request, facilitator, Reliability.ReliableOrdered, Priority.High, 0);
    }

    private void SendQueuedOpenNat(RakPeer peer)
    {
        while (_queuedOpenNat.TryDequeue(out var queued))
            SendPunchthrough(peer, queued.Destination, queued.Facilitator);
    }

    private void SendPunchthrough(RakPeer peer, RakNetGuid destination, SystemAddress facilitator)
    {
        Span<byte> request = stackalloc byte[1 + sizeof(ulong)];
        var writer = new BitStreamWriter(request);
        writer.WriteByte((byte)MessageId.NatPunchthroughRequest);
        writer.WriteRakNetGuid(destination);
        SendReliable(peer, writer, facilitator);
        DebugMessage?.Invoke($"Starting ID_NAT_PUNCHTHROUGH_REQUEST to guid {destination}.");
    }

    // SendTTL (NatPunchthroughClient.cpp:782-793): a TTL of 1 does not get past the router; 2 might reach a LAN host.
    private static void SendTtl(RakPeer peer, SystemAddress sa)
    {
        if (!sa.IsAssigned || sa.Port == 0)
            return;
        peer.LowLevel.SendTtl(sa, 2);
    }

    // SendOutOfBand (NatPunchthroughClient.cpp:829-862): the ID, the session ID and, for the bidirectional message, the
    // port it is sent to.
    private void SendOutOfBand(RakPeer peer, SystemAddress sa, OutOfBandId id)
    {
        if (!sa.IsAssigned || sa.Port == 0)
            return;
        Span<byte> payload = stackalloc byte[1 + (2 * sizeof(ushort))];
        var writer = new BitStreamWriter(payload);
        writer.WriteByte((byte)id);
        writer.WriteUInt16(_sp.SessionId);
        if (id == OutOfBandId.NatEstablishBidirectional)
            writer.WriteUInt16(sa.Port);
        peer.LowLevel.SendOutOfBand(sa, writer.Written);
        if (DebugMessage is { } debug)
        {
            // Server time: my time plus the clock differential.
            var differential = peer.TryGetConnection(_sp.Facilitator, out var facilitator) ? facilitator.ClockDifferential : 0;
            var serverTime = unchecked((ulong)((long)peer.GetTime() + differential));
            var name = id == OutOfBandId.NatEstablishUnidirectional ? "ID_NAT_ESTABLISH_UNIDIRECTIONAL" : "ID_NAT_ESTABLISH_BIDIRECTIONAL";
            debug($"{serverTime}: {TestModeToString(_sp.TestMode)}: OOB {name} to guid {_sp.TargetGuid}, system address {sa}.\n");
        }
    }

    private void IncrementExternalAttemptCount(ulong time, ulong delta)
    {
        if (++_sp.RetryCount >= Configuration.UdpSendsPerPortExternal)
        {
            ++_sp.AttemptCount;
            _sp.RetryCount = 0;
            _sp.NextActionTime = After(time, Configuration.ExternalIpWaitBetweenPorts, delta);
            _sp.SentTtl = false;
        }
        else
        {
            _sp.NextActionTime = After(time, Configuration.TimeBetweenPunchAttemptsExternal, delta);
        }
    }

    private void OnReadyForNextPunchthrough(RakPeer? peer)
    {
        if (peer is null)
            return;
        _sp.NextActionTime = 0;
        ReadOnlySpan<byte> ready = [(byte)MessageId.NatClientReady];
        // RakPeer::Send sends nothing once the peer is shutting down (RakPeer.cpp:1320-1321).
        if (_sp.Facilitator.IsAssigned && peer.IsActive)
            peer.Send(ready, _sp.Facilitator, Reliability.ReliableOrdered, Priority.High, 0);
    }

    private void PushSuccess(RakPeer peer) => PushResult(peer, MessageId.NatPunchthroughSucceeded);

    private void PushFailure(RakPeer peer) => PushResult(peer, MessageId.NatPunchthroughFailed);

    // PushSuccess and PushFailure (NatPunchthroughClient.cpp:335-348, 1090-1103).
    private void PushResult(RakPeer peer, MessageId id)
    {
        ReadOnlySpan<byte> packet = [(byte)id, _sp.WeAreSender ? (byte)1 : (byte)0];
        peer.Inject(packet, _sp.TargetAddress, _sp.TargetGuid, atHead: true, generatedLocally: true);
    }

    private bool RemoveFromFailureQueue()
    {
        var i = _failedAttemptList.FindIndex(f => f.RakNetGuid == _sp.TargetGuid);
        if (i < 0)
            return false;
        RemoveAtIndexFast(_failedAttemptList, i);
        return true;
    }

    private void ClearState()
    {
        OnReadyForNextPunchthrough(_host?.Peer);
        _failedAttemptList.Clear();
        _queuedOpenNat.Clear();
    }

    private static ulong After(ulong time, TimeSpan wait, ulong delta) => unchecked(time + (ulong)(long)wait.TotalMilliseconds - delta);

    private static bool IsConnected(RakPeer peer, SystemAddress address) =>
        address.IsAssigned && peer.GetConnectionState(address) == ConnectionState.Connected;

    // RakPeer::GetExternalID(target): UNASSIGNED_SYSTEM_ADDRESS without a connection.
    private static SystemAddress ExternalAddress(RakPeer peer, SystemAddress target) =>
        peer.TryGetConnection(target, out var connection) ? connection.ExternalAddress : SystemAddress.Unassigned;

    private static void SendReliable(RakPeer peer, BitStreamWriter writer, SystemAddress target)
    {
        if (target.IsAssigned)
            peer.Send(writer, target, Reliability.ReliableOrdered, Priority.High, 0);
    }

    private static void RemoveAtIndexFast<T>(List<T> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    private static string TestModeToString(TestMode mode) => mode switch
    {
        TestMode.TestingInternalIps => "TESTING_INTERNAL_IPS",
        TestMode.WaitingForInternalIpsResponse => "WAITING_FOR_INTERNAL_IPS_RESPONSE",
        TestMode.TestingExternalIpsFacilitatorPortToFacilitatorPort => "TESTING_EXTERNAL_IPS_FACILITATOR_PORT_TO_FACILITATOR_PORT",
        TestMode.TestingExternalIps1024ToFacilitatorPort => "TESTING_EXTERNAL_IPS_1024_TO_FACILITATOR_PORT",
        TestMode.TestingExternalIpsFacilitatorPortTo1024 => "TESTING_EXTERNAL_IPS_FACILITATOR_PORT_TO_1024",
        TestMode.TestingExternalIps1024To1024 => "TESTING_EXTERNAL_IPS_1024_TO_1024",
        TestMode.WaitingAfterAllAttempts => "WAITING_AFTER_ALL_ATTEMPTS",
        TestMode.PunchingFixedPort => "PUNCHING_FIXED_PORT",
        _ => "",
    };

    /// <summary>NatPunchthroughClient::SendPing (NatPunchthroughClient.h:163-196): the attempt in progress.</summary>
    private sealed class SendPing
    {
        public ulong NextActionTime { get; set; }

        public SystemAddress TargetAddress { get; set; }

        public SystemAddress Facilitator { get; set; }

        public SystemAddress[] InternalIds { get; } = new SystemAddress[MaximumNumberOfInternalIds];

        public RakNetGuid TargetGuid { get; set; }

        public bool WeAreSender { get; set; }

        public int AttemptCount { get; set; }

        public int RetryCount { get; set; }

        public int PunchingFixedPortAttempts { get; set; }

        public ushort SessionId { get; set; }

        public bool SentTtl { get; set; }

        public TestMode TestMode { get; set; }
    }
}
