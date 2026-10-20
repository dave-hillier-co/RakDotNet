using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// Router2: connects to a system this one cannot reach, through a third system connected to both that forwards the
/// datagrams with a <see cref="UdpForwarder"/>. Every system involved runs the plugin; only routers need
/// <see cref="MaximumForwardingRequests"/> above 0.
/// </summary>
/// <remarks>
/// <para>
/// Ported from Router2.cpp, whose algorithm is:
/// </para>
/// <list type="number">
/// <item>
/// <see cref="EstablishRouting"/> sends ID_ROUTER_2_INTERNAL, ID_ROUTER_2_QUERY_FORWARDING and the endpoint's GUID to
/// every connected system, and waits for each reply until twice the largest ping plus 1 s.
/// </item>
/// <item>
/// A router connected to the endpoint replies ID_ROUTER_2_REPLY_FORWARDING with the endpoint's GUID, true, its ping to
/// the endpoint and half its forwarding entries in use; one that cannot forward replies false.
/// </item>
/// <item>
/// Once every router has replied, or the wait is over, the one with the lowest ping times one plus its entries in use
/// is sent ID_ROUTER_2_REQUEST_FORWARDING. With no router left, the application gets ID_CONNECTION_LOST when the
/// forwarding being replaced had worked, and otherwise ID_ROUTER_2_FORWARDING_NO_PATH.
/// </item>
/// <item>
/// The router starts forwarding between the two for 30 s without data, then every 100 ms until a timeout of eight
/// times the larger ping plus 300 ms sends both an out-of-band ID_ROUTER_2_REPLY_TO_SENDER_PORT from the forwarding
/// socket and an ID_ROUTER_2_REPLY_TO_SPECIFIED_PORT with the forwarding port from its own.
/// </item>
/// <item>
/// Each answers the first with ID_ROUTER_2_MINI_PUNCH_REPLY and the router's GUID to the address it came from, and the
/// second to the forwarding port, which forwards the reply to the other system. A system that receives
/// ID_ROUTER_2_MINI_PUNCH_REPLY sends ID_ROUTER_2_MINI_PUNCH_REPLY_BOUNCE to the router.
/// </item>
/// <item>
/// With a bounce from both, the router sends the endpoint ID_ROUTER_2_REROUTED and the source
/// ID_ROUTER_2_FORWARDING_ESTABLISHED, each with the other's GUID and the forwarding port. The endpoint changes the
/// source's address to the forwarding port; the application at the source gets ID_ROUTER_2_FORWARDING_ESTABLISHED and
/// connects to the router's address on that port, or, when forwarding was re-established after a router was lost,
/// ID_ROUTER_2_REROUTED with the address changed.
/// </item>
/// <item>
/// Connected through a router, the source sends ID_ROUTER_2_INCREASE_TIMEOUT, and both add 10 s to the connection's
/// timeout, so a lost router leaves time to reroute.
/// </item>
/// </list>
/// <para>
/// RakNet's quirks are kept. ID_ROUTER_2_FORWARDING_NO_PATH is pushed as not generated locally, which this plugin then
/// consumes, so the application never sees it. The reply's entries in use are written as a 32-bit int and read as
/// 16 bits, so they are read as 0. A router refusal during the request phase leaves the attempt waiting. Every message
/// is MEDIUM_PRIORITY and RELIABLE_ORDERED on channel 0 but ID_ROUTER_2_INCREASE_TIMEOUT, HIGH_PRIORITY and RELIABLE.
/// Times are RakNet::TimeMS, 32 bits, on the peer's clock.
/// </para>
/// <para>
/// The debug interface is two delegates. The forwarder binds through the peer's transport factory, so it is created
/// when the plugin is attached with <see cref="MaximumForwardingRequests"/> above 0, or when that is set while attached.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class Router2 : IRakPlugin
{
    // Router2MessageIdentifiers (Router2.cpp:54-60).
    private const byte QueryForwarding = 0;
    private const byte ReplyForwarding = 1;
    private const byte RequestForwardingId = 2;
    private const byte IncreaseTimeout = 3;

    private const uint ForwardingTimeoutMs = 30000;
    private static readonly TimeSpan TimeoutIncrease = TimeSpan.FromMilliseconds(10000);

    private readonly Lock _gate = new();
    private readonly PeerThreading _forwarderThreading;
    private readonly List<ConnectRequest> _connectionRequests = [];
    private readonly List<MiniPunchRequest> _miniPunchesInProgress = [];
    private readonly List<ForwardedConnection> _forwardedConnectionList = [];
    private IPluginHost? _host;
    private UdpForwarder? _udpForwarder;
    private int _maximumForwardingRequests;

    /// <param name="forwarderThreading">
    /// Whether the forwarder runs on a thread of its own, as RakNet's does, or on the dispatch step of the peer the
    /// plugin is attached to.
    /// </param>
    public Router2(PeerThreading forwarderThreading = PeerThreading.Background) => _forwarderThreading = forwarderThreading;

    /// <summary>Router2DebugInterface::ShowFailure: why a step failed; null for none.</summary>
    public Action<string>? FailureMessage { get; set; }

    /// <summary>Router2DebugInterface::ShowDiagnostic: what the plugin is doing; null for none.</summary>
    public Action<string>? DiagnosticMessage { get; set; }

    /// <summary>
    /// SetMaximumForwardingRequests: how many connections this system will forward; 0, the default, forwards none.
    /// Above 0 a <see cref="UdpForwarder"/> runs, and at 0 or less it stops.
    /// </summary>
    public int MaximumForwardingRequests
    {
        get
        {
            lock (_gate)
                return _maximumForwardingRequests;
        }
        set
        {
            UdpForwarder? stopped = null;
            lock (_gate)
            {
                if (value > 0 && _maximumForwardingRequests <= 0)
                {
                    if (_host is { Peer: not null } host)
                        StartForwarder(host);
                }
                else if (value <= 0 && _maximumForwardingRequests > 0)
                {
                    stopped = _udpForwarder;
                    _udpForwarder = null;
                }
                _maximumForwardingRequests = value;
            }
            stopped?.Dispose();
        }
    }

    /// <summary>The forwarder, while <see cref="MaximumForwardingRequests"/> is above 0 and the plugin is attached.</summary>
    public UdpForwarder? Forwarder
    {
        get
        {
            lock (_gate)
                return _udpForwarder;
        }
    }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("Router2 is not attached to a RakPeer.");

    /// <summary>
    /// EstablishRouting: finds a connected system to forward to <paramref name="endpointGuid"/>; the outcome is
    /// ID_ROUTER_2_FORWARDING_ESTABLISHED, from the router, with the endpoint's GUID and the port to connect to on the
    /// router's address. Nothing happens when already connected or connecting to the endpoint, when routing to it is in
    /// progress, or when no connected system has a ping yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public void EstablishRouting(RakNetGuid endpointGuid)
    {
        var peer = Peer;
        var state = peer.GetConnectionState(endpointGuid);
        if (state is not ConnectionState.Disconnected and not ConnectionState.NotConnected)
        {
            FailureMessage?.Invoke($"Router2 failed (already connected to {endpointGuid})");
            return;
        }
        lock (_gate)
            ConnectInternal(peer, endpointGuid, returnConnectionLostOnFailure: false);
    }

    void IRakPlugin.OnAttached(IPluginHost host)
    {
        lock (_gate)
        {
            _host = host;
            if (_maximumForwardingRequests > 0 && host.Peer is not null)
                StartForwarder(host);
        }
    }

    void IRakPlugin.OnDetached()
    {
        UdpForwarder? forwarder;
        lock (_gate)
        {
            ClearAll();
            forwarder = _udpForwarder;
            _udpForwarder = null;
            _host = null;
        }
        forwarder?.Dispose();
    }

    /// <summary>OnRakPeerShutdown: forgets every request, punch and forwarded connection.</summary>
    void IRakPlugin.OnShutdown()
    {
        lock (_gate)
            ClearAll();
    }

    /// <summary>Update (Router2.cpp:345-418).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        UdpForwarder? manualForwarder;
        lock (_gate)
        {
            var curTime = (uint)peer.GetTime();
            for (var connectionRequestIndex = 0; connectionRequestIndex < _connectionRequests.Count;)
            {
                var connectionRequest = _connectionRequests[connectionRequestIndex];
                // pingTimeout is only used while querying.
                if (connectionRequest.RequestState == RequestState.QueryForwarding && connectionRequest.PingTimeout < curTime)
                {
                    var anyRemoved = false;
                    for (var i = 0; i < connectionRequest.ConnectionRequestSystems.Count;)
                    {
                        if (connectionRequest.ConnectionRequestSystems[i].PingToEndpoint < 0)
                        {
                            anyRemoved = true;
                            RemoveAtIndexFast(connectionRequest.ConnectionRequestSystems, i);
                        }
                        else
                        {
                            i++;
                        }
                    }
                    if (anyRemoved && !UpdateForwarding(peer, connectionRequest))
                    {
                        RemoveAtIndexFast(_connectionRequests, connectionRequestIndex);
                        continue;
                    }
                }
                connectionRequestIndex++;
            }

            for (var i = 0; i < _miniPunchesInProgress.Count;)
            {
                var punch = _miniPunchesInProgress[i];
                if (punch.Timeout < curTime)
                {
                    SendFailureOnCannotForward(peer, punch.SourceGuid, punch.EndpointGuid);
                    RemoveAtIndexFast(_miniPunchesInProgress, i);
                }
                else if (curTime > punch.NextAction)
                {
                    punch.NextAction = curTime + 100;
                    SendOobMessages(peer, punch);
                }
                else
                {
                    i++;
                }
            }
            manualForwarder = _forwarderThreading == PeerThreading.Manual ? _udpForwarder : null;
        }
        manualForwarder?.Update();
    }

    /// <summary>OnReceive (Router2.cpp:228-344).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.Router2Internal when data.Length >= 2:
                    switch (data[1])
                    {
                        case QueryForwarding:
                            OnQueryForwarding(peer, e.GetReader(), e.RakNetGuid);
                            return PluginResult.Consume;
                        case ReplyForwarding:
                            OnQueryForwardingReply(peer, e.GetReader(), e.RakNetGuid, e.Address);
                            return PluginResult.Consume;
                        case RequestForwardingId:
                            DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REQUEST_FORWARDING on ip {e.Address} from {e.RakNetGuid}");
                            OnRequestForwarding(peer, e.GetReader(), e.RakNetGuid, e.Address);
                            return PluginResult.Consume;
                        case IncreaseTimeout:
                            // The routed system wants more time without communication, in case the router drops.
                            AddToTimeout(peer, e.Address);
                            return PluginResult.Consume;
                    }
                    break;
                case MessageId.OutOfBandInternal when data.Length >= 2:
                    switch ((OutOfBandId)data[1])
                    {
                        case OutOfBandId.Router2ReplyToSenderPort:
                        {
                            Span<byte> guid = stackalloc byte[sizeof(ulong)];
                            var writer = new BitStreamWriter(guid);
                            writer.WriteRakNetGuid(e.RakNetGuid);
                            SendOobFromRakNetPort(peer, OutOfBandId.Router2MiniPunchReply, writer.Written, e.Address);
                            DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REPLY_TO_SENDER_PORT on address {e.Address}, replying with ID_ROUTER_2_MINI_PUNCH_REPLY");
                            return PluginResult.Consume;
                        }
                        case OutOfBandId.Router2ReplyToSpecifiedPort:
                        {
                            var reader = e.GetReader();
                            reader.IgnoreBytes(2);
                            reader.TryReadUInt16(out var port);
                            var sa = e.Address.WithPort(port);
                            Span<byte> guid = stackalloc byte[sizeof(ulong)];
                            var writer = new BitStreamWriter(guid);
                            writer.WriteRakNetGuid(e.RakNetGuid);
                            SendOobFromRakNetPort(peer, OutOfBandId.Router2MiniPunchReply, writer.Written, sa);
                            DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REPLY_TO_SPECIFIED_PORT {port} on address {sa}, replying with ID_ROUTER_2_MINI_PUNCH_REPLY");
                            return PluginResult.Consume;
                        }
                        case OutOfBandId.Router2MiniPunchReply:
                            OnMiniPunchReply(peer, e.GetReader());
                            return PluginResult.Consume;
                        case OutOfBandId.Router2MiniPunchReplyBounce:
                            OnMiniPunchReplyBounce(peer, e.RakNetGuid);
                            return PluginResult.Consume;
                    }
                    break;
                case MessageId.Router2ForwardingEstablished:
                    if (OnForwardingSuccess(peer, e))
                        e.RewriteId(MessageId.Router2Rerouted);
                    break;
                case MessageId.Router2Rerouted:
                    OnRerouted(peer, e.GetReader(), e.RakNetGuid, e.Address);
                    break;
                case MessageId.ConnectionRequestAccepted:
                    var accepted = e.RakNetGuid;
                    if (_forwardedConnectionList.Exists(f => f.EndpointGuid == accepted && f.WeInitiatedForwarding))
                    {
                        // Connected through a router: the endpoint takes longer to drop this system, in case the
                        // router drops, and so does this system.
                        ReadOnlySpan<byte> increase = [(byte)MessageId.Router2Internal, IncreaseTimeout];
                        peer.Send(increase, e.RakNetGuid, Reliability.Reliable, Priority.High, 0);
                        DiagnosticMessage?.Invoke($"Got ID_CONNECTION_REQUEST_ACCEPTED, sending ID_ROUTER_2_INCREASE_TIMEOUT to {e.RakNetGuid}");
                        AddToTimeout(peer, e.Address);
                    }
                    break;
                case MessageId.Router2ForwardingNoPath:
                    if (!e.IsLocal)
                        return PluginResult.Consume;
                    break;
            }
        }
        return PluginResult.Continue;
    }

    /// <summary>OnClosedConnection (Router2.cpp:419-519).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        if (_host?.Peer is not { } peer)
            return;
        var rakNetGuid = connection.RakNetGuid;
        lock (_gate)
        {
            for (var forwardedConnectionIndex = 0; forwardedConnectionIndex < _forwardedConnectionList.Count;)
            {
                var forwarded = _forwardedConnectionList[forwardedConnectionIndex];
                if (forwarded.EndpointGuid == rakNetGuid)
                {
                    DiagnosticMessage?.Invoke($"Closed connection to {rakNetGuid}, removing forwarding from list");
                    // No longer needs forwarding.
                    RemoveAtIndexFast(_forwardedConnectionList, forwardedConnectionIndex);
                }
                else if (forwarded.IntermediaryGuid == rakNetGuid && forwarded.WeInitiatedForwarding)
                {
                    // Lost the router: route to the endpoint again, through another one if need be, replacing a request
                    // already in the list. The entry stays, as the connection is still forwarded.
                    var pos = GetConnectionRequestIndex(forwarded.EndpointGuid);
                    if (pos >= 0)
                        RemoveAtIndexFast(_connectionRequests, pos);
                    ConnectInternal(peer, forwarded.EndpointGuid, returnConnectionLostOnFailure: true);
                    forwardedConnectionIndex++;
                    DiagnosticMessage?.Invoke($"Closed connection {rakNetGuid}, restarting forwarding");
                }
                else
                {
                    forwardedConnectionIndex++;
                }
            }

            for (var connectionRequestIndex = 0; connectionRequestIndex < _connectionRequests.Count;)
            {
                var cr = _connectionRequests[connectionRequestIndex];
                var connectionRequestGuidIndex = cr.GetGuidIndex(rakNetGuid);
                if (connectionRequestGuidIndex < 0)
                {
                    connectionRequestIndex++;
                    continue;
                }
                RemoveAtIndexFast(cr.ConnectionRequestSystems, connectionRequestGuidIndex);
                // False when no system is left to try.
                if (!UpdateForwarding(peer, cr))
                {
                    DiagnosticMessage?.Invoke($"Aborted connection to {rakNetGuid}, aborted forwarding");
                    RemoveAtIndexFast(_connectionRequests, connectionRequestIndex);
                }
                else
                {
                    DiagnosticMessage?.Invoke($"Aborted connection attempt to {rakNetGuid}, restarting forwarding to {cr.EndpointGuid}");
                    connectionRequestIndex++;
                }
            }

            for (var i = 0; i < _miniPunchesInProgress.Count;)
            {
                var punch = _miniPunchesInProgress[i];
                if (punch.SourceGuid == rakNetGuid || punch.EndpointGuid == rakNetGuid)
                {
                    if (punch.SourceGuid != rakNetGuid)
                        SendFailureOnCannotForward(peer, punch.SourceGuid, punch.EndpointGuid);
                    RemoveAtIndexFast(_miniPunchesInProgress, i);
                }
                else
                {
                    i++;
                }
            }
        }
    }

    /// <summary>
    /// OnFailedConnectionAttempt (Router2.cpp:520-540): a failed attempt to a router's forwarding port names the endpoint
    /// it was for, and the forwarding is forgotten.
    /// </summary>
    void IRakPlugin.OnConnectFailed(PeerEvent e, ConnectFailure failure)
    {
        lock (_gate)
        {
            for (var i = 0; i < _forwardedConnectionList.Count;)
            {
                var forwarded = _forwardedConnectionList[i];
                if (forwarded.IntermediaryAddress == e.Address)
                {
                    DiagnosticMessage?.Invoke($"Failed connection attempt to forwarded system ({forwarded.EndpointGuid} : {e.Address})");
                    e.RewriteGuid(forwarded.EndpointGuid);
                    RemoveAtIndexFast(_forwardedConnectionList, i);
                }
                else
                {
                    i++;
                }
            }
        }
    }

    // ConnectInternal (Router2.cpp:118-208).
    private bool ConnectInternal(RakPeer peer, RakNetGuid endpointGuid, bool returnConnectionLostOnFailure)
    {
        var largestPing = GetLargestPingAmongConnectedSystems(peer);
        if (largestPing < 0)
        {
            // Not connected to anyone with a ping.
            FailureMessage?.Invoke($"Router2: ConnectInternal({endpointGuid}) failed: no connected system has a ping");
            return false;
        }
        if (GetConnectionRequestIndex(endpointGuid) >= 0)
        {
            FailureMessage?.Invoke($"Router2: ConnectInternal({endpointGuid}) failed: already in progress");
            return false;
        }
        var guids = peer.Connections.Select(c => c.RakNetGuid).ToArray();
        if (guids.Length == 0)
        {
            FailureMessage?.Invoke("Router2 failed: not connected");
            return false;
        }

        var cr = new ConnectRequest
        {
            RequestState = RequestState.QueryForwarding,
            PingTimeout = unchecked((uint)peer.GetTime() + (uint)(largestPing * 2) + 1000),
            EndpointGuid = endpointGuid,
            ReturnConnectionLostOnFailure = returnConnectionLostOnFailure,
        };
        Span<byte> query = stackalloc byte[2 + sizeof(ulong)];
        foreach (var guid in guids)
        {
            if (guid == endpointGuid)
                continue;
            cr.ConnectionRequestSystems.Add(new ConnectionRequestSystem { Guid = guid, PingToEndpoint = -1 });
            var writer = new BitStreamWriter(query);
            writer.WriteByte((byte)MessageId.Router2Internal);
            writer.WriteByte(QueryForwarding);
            writer.WriteRakNetGuid(endpointGuid);
            peer.Send(writer, guid, Reliability.ReliableOrdered, Priority.Medium, 0);
        }
        _connectionRequests.Add(cr);
        DiagnosticMessage?.Invoke($"Broadcasting ID_ROUTER_2_QUERY_FORWARDING to {endpointGuid}");
        return true;
    }

    // UpdateForwarding (Router2.cpp:546-603): false when no system is left.
    private bool UpdateForwarding(RakPeer peer, ConnectRequest connectionRequest)
    {
        if (connectionRequest.ConnectionRequestSystems.Count == 0)
        {
            if (connectionRequest.ReturnConnectionLostOnFailure)
            {
                // A connection that was forwarded before: rerouting is not possible.
                ReturnToUser(MessageId.ConnectionLost, connectionRequest.EndpointGuid, wasGeneratedLocally: true);
            }
            else
            {
                ReturnToUser(MessageId.Router2ForwardingNoPath, connectionRequest.EndpointGuid, wasGeneratedLocally: false);
            }
            DiagnosticMessage?.Invoke("Forwarding failed, no remaining systems");
            var forwarded = _forwardedConnectionList.FindIndex(f => f.EndpointGuid == connectionRequest.EndpointGuid && f.WeInitiatedForwarding);
            if (forwarded >= 0)
                RemoveAtIndexFast(_forwardedConnectionList, forwarded);
            return false;
        }

        if (connectionRequest.RequestState == RequestState.QueryForwarding)
        {
            // The query is still in progress while any system has not replied.
            if (connectionRequest.ConnectionRequestSystems.Exists(s => s.PingToEndpoint < 0))
                return true;
            RequestForwarding(peer, connectionRequest);
        }
        return true;
    }

    // RequestForwarding (Router2.cpp:620-656).
    private void RequestForwarding(RakPeer peer, ConnectRequest connectionRequest)
    {
        connectionRequest.RequestState = RequestState.RequestForwarding;
        if (connectionRequest.GetGuidIndex(connectionRequest.LastRequestedForwardingSystem) >= 0)
        {
            FailureMessage?.Invoke("Router2 failed: forwarding already requested");
            return;
        }

        // Prioritise the systems to ask, in an OrderedList with ConnectionRequestSystemComp.
        var commandList = new List<ConnectionRequestSystem>();
        foreach (var system in connectionRequest.ConnectionRequestSystems)
            OrderedListSearch.Insert(commandList, system, system, ConnectionRequestSystemComp);
        connectionRequest.LastRequestedForwardingSystem = commandList[0].Guid;

        Span<byte> request = stackalloc byte[2 + sizeof(ulong)];
        var writer = new BitStreamWriter(request);
        writer.WriteByte((byte)MessageId.Router2Internal);
        writer.WriteByte(RequestForwardingId);
        writer.WriteRakNetGuid(connectionRequest.EndpointGuid);
        peer.Send(writer, connectionRequest.LastRequestedForwardingSystem, Reliability.ReliableOrdered, Priority.Medium, 0);
        DiagnosticMessage?.Invoke($"Sending ID_ROUTER_2_REQUEST_FORWARDING (lastRequestedForwardingSystem = {connectionRequest.LastRequestedForwardingSystem}, endpointGuid = {connectionRequest.EndpointGuid})");
    }

    // ConnectionRequestSystemComp (Router2.cpp:604-618): equal weighted pings compare as greater, and different GUIDs as
    // less either way.
    private static int ConnectionRequestSystemComp(ConnectionRequestSystem key, ConnectionRequestSystem data)
    {
        var keyWeight = key.PingToEndpoint * (key.UsedForwardingEntries + 1);
        var dataWeight = data.PingToEndpoint * (data.UsedForwardingEntries + 1);
        if (keyWeight < dataWeight)
            return -1;
        if (keyWeight == dataWeight)
            return 1;
        if (key.Guid < data.Guid)
            return -1;
        if (key.Guid > data.Guid)
            return -1;
        return 0;
    }

    // SendFailureOnCannotForward (Router2.cpp:657-665).
    private static void SendFailureOnCannotForward(RakPeer peer, RakNetGuid sourceGuid, RakNetGuid endpointGuid)
    {
        Span<byte> reply = stackalloc byte[2 + sizeof(ulong) + 1];
        var writer = new BitStreamWriter(reply);
        writer.WriteByte((byte)MessageId.Router2Internal);
        writer.WriteByte(ReplyForwarding);
        writer.WriteRakNetGuid(endpointGuid);
        writer.WriteBool(false);
        peer.Send(writer, sourceGuid, Reliability.ReliableOrdered, Priority.Medium, 0);
    }

    // ReturnFailureOnCannotForward (Router2.cpp:666-705): the ping to the endpoint, or -1 after telling the source.
    private int ReturnFailureOnCannotForward(RakPeer peer, RakNetGuid sourceGuid, RakNetGuid endpointGuid)
    {
        // Already forwarding the most this system will?
        if (_udpForwarder is null || _udpForwarder.UsedForwardEntries / 2 > _maximumForwardingRequests)
        {
            FailureMessage?.Invoke($"Router2 failed ({sourceGuid} -> {endpointGuid}): not forwarding more");
            SendFailureOnCannotForward(peer, sourceGuid, endpointGuid);
            return -1;
        }
        // A connection that is itself forwarded is not forwarded, nor one from a system routed through this one.
        if (_forwardedConnectionList.Exists(f => f.EndpointGuid == endpointGuid || f.EndpointGuid == sourceGuid))
        {
            FailureMessage?.Invoke($"Router2 failed ({sourceGuid} -> {endpointGuid}): forwarded already");
            SendFailureOnCannotForward(peer, sourceGuid, endpointGuid);
            return -1;
        }
        var pingToEndpoint = AveragePing(peer, endpointGuid);
        if (pingToEndpoint == -1)
        {
            FailureMessage?.Invoke($"Router2 failed ({sourceGuid} -> {endpointGuid}): no ping to the endpoint");
            SendFailureOnCannotForward(peer, sourceGuid, endpointGuid);
            return -1;
        }
        return pingToEndpoint;
    }

    // OnQueryForwarding (Router2.cpp:706-735).
    private void OnQueryForwarding(RakPeer peer, BitStreamReader reader, RakNetGuid sourceGuid)
    {
        reader.IgnoreBytes(2);
        reader.TryReadRakNetGuid(out var endpointGuid);
        var pingToEndpoint = ReturnFailureOnCannotForward(peer, sourceGuid, endpointGuid);
        if (pingToEndpoint == -1)
            return;

        // Connected to the endpoint: the ping and the entries in use. (unsigned short)used/2 is an int, so 32 bits.
        Span<byte> reply = stackalloc byte[2 + sizeof(ulong) + 1 + sizeof(ushort) + sizeof(int)];
        var writer = new BitStreamWriter(reply);
        writer.WriteByte((byte)MessageId.Router2Internal);
        writer.WriteByte(ReplyForwarding);
        writer.WriteRakNetGuid(endpointGuid);
        writer.WriteBool(true);
        writer.WriteUInt16(unchecked((ushort)pingToEndpoint));
        writer.WriteInt32((ushort)_udpForwarder!.UsedForwardEntries / 2);
        peer.Send(writer, sourceGuid, Reliability.ReliableOrdered, Priority.Medium, 0);
        DiagnosticMessage?.Invoke($"Sending ID_ROUTER_2_REPLY_FORWARDING to {sourceGuid}");
    }

    // OnQueryForwardingReply (Router2.cpp:736-791).
    private void OnQueryForwardingReply(RakPeer peer, BitStreamReader reader, RakNetGuid routerGuid, SystemAddress routerAddress)
    {
        reader.IgnoreBytes(2);
        reader.TryReadRakNetGuid(out var endpointGuid);
        reader.TryReadBool(out var canForward);
        var connectionRequestIndex = GetConnectionRequestIndex(endpointGuid);
        if (connectionRequestIndex < 0)
        {
            FailureMessage?.Invoke($"Router2 failed ({endpointGuid}): no request");
            return;
        }
        var request = _connectionRequests[connectionRequestIndex];
        var connectionRequestGuidIndex = request.GetGuidIndex(routerGuid);
        if (connectionRequestGuidIndex < 0)
        {
            FailureMessage?.Invoke($"Router2 failed ({endpointGuid}): {routerGuid} was not asked");
            return;
        }
        DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REPLY_FORWARDING on address {routerAddress}({routerGuid} -> {endpointGuid}) canForward={(canForward ? 1 : 0)}");

        if (canForward)
        {
            reader.TryReadUInt16(out var pingToEndpoint);
            reader.TryReadUInt16(out var usedEntries);
            var system = request.ConnectionRequestSystems[connectionRequestGuidIndex];
            system.UsedForwardingEntries = usedEntries;
            system.PingToEndpoint = AveragePing(peer, routerGuid) + pingToEndpoint;
        }
        else
        {
            request.ConnectionRequestSystems.RemoveAt(connectionRequestGuidIndex);
        }

        if (!UpdateForwarding(peer, request))
            RemoveAtIndexFast(_connectionRequests, connectionRequestIndex);
    }

    // SendForwardingSuccess (Router2.cpp:792-810).
    private void SendForwardingSuccess(RakPeer peer, MessageId messageId, RakNetGuid sourceGuid, RakNetGuid endpointGuid, ushort sourceToDstPort)
    {
        Span<byte> message = stackalloc byte[1 + sizeof(ulong) + sizeof(ushort)];
        var writer = new BitStreamWriter(message);
        writer.WriteByte((byte)messageId);
        writer.WriteRakNetGuid(endpointGuid);
        writer.WriteUInt16(sourceToDstPort);
        peer.Send(writer, sourceGuid, Reliability.ReliableOrdered, Priority.Medium, 0);
        DiagnosticMessage?.Invoke(messageId == MessageId.Router2ForwardingEstablished ? "Sending ID_ROUTER_2_FORWARDING_ESTABLISHED" : "Sending ID_ROUTER_2_REROUTED");
    }

    // SendOOBFromRakNetPort (Router2.cpp:811-826): the out-of-band ID and the extra data from the peer's socket.
    private static void SendOobFromRakNetPort(RakPeer peer, OutOfBandId oob, ReadOnlySpan<byte> extraData, SystemAddress sa)
    {
        if (!sa.IsAssigned)
            return;
        Span<byte> payload = stackalloc byte[1 + extraData.Length];
        payload[0] = (byte)oob;
        extraData.CopyTo(payload[1..]);
        peer.LowLevel.SendOutOfBand(sa, payload);
    }

    // SendOOBFromSpecifiedSocket (Router2.cpp:827-846): the out-of-band header and the ID from the forwarding socket.
    private static void SendOobFromSpecifiedSocket(RakPeer peer, OutOfBandId oob, SystemAddress sa, IDatagramTransport socket)
    {
        Span<byte> datagram = stackalloc byte[OutOfBandHeader.Length + 1];
        var writer = new BitStreamWriter(datagram);
        peer.LowLevel.WriteOutOfBandHeader(ref writer);
        writer.WriteByte((byte)oob);
        socket.TrySend(writer.Written, sa);
    }

    // SendOOBMessages (Router2.cpp:847-878).
    private void SendOobMessages(RakPeer peer, MiniPunchRequest mpr)
    {
        // Mini NAT punch: from the forwarding socket to each, which replies if it arrives.
        SendOobFromSpecifiedSocket(peer, OutOfBandId.Router2ReplyToSenderPort, mpr.SourceAddress, mpr.ForwardingSocket);
        SendOobFromSpecifiedSocket(peer, OutOfBandId.Router2ReplyToSenderPort, mpr.EndpointAddress, mpr.ForwardingSocket);
        DiagnosticMessage?.Invoke($"call SendOOBFromSpecifiedSocket(...,{mpr.SourceAddress},...)");
        DiagnosticMessage?.Invoke($"call SendOOBFromSpecifiedSocket(...,{mpr.EndpointAddress},...)");
        // And each told to send to the forwarding port.
        Span<byte> port = stackalloc byte[sizeof(ushort)];
        var writer = new BitStreamWriter(port);
        writer.WriteUInt16(mpr.ForwardingPort);
        SendOobFromRakNetPort(peer, OutOfBandId.Router2ReplyToSpecifiedPort, port, mpr.SourceAddress);
        SendOobFromRakNetPort(peer, OutOfBandId.Router2ReplyToSpecifiedPort, port, mpr.EndpointAddress);
    }

    // OnRequestForwarding (Router2.cpp:879-982).
    private void OnRequestForwarding(RakPeer peer, BitStreamReader reader, RakNetGuid sourceGuid, SystemAddress sourceAddress)
    {
        reader.IgnoreBytes(2);
        reader.TryReadRakNetGuid(out var endpointGuid);
        var pingToEndpoint = ReturnFailureOnCannotForward(peer, sourceGuid, endpointGuid);
        if (pingToEndpoint == -1)
        {
            FailureMessage?.Invoke($"Router2 failed (sourceGuid = {sourceGuid}, endpointGuid = {endpointGuid})");
            return;
        }

        var endpointSystemAddress = peer.GetAddressFromGuid(endpointGuid);
        var forwarding = _udpForwarder!.StartForwarding(sourceAddress, endpointSystemAddress, TimeSpan.FromMilliseconds(ForwardingTimeoutMs));
        switch (forwarding.Result)
        {
            case UdpForwarderResult.ForwardingAlreadyExists:
                DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REQUEST_FORWARDING, result=UDPFORWARDER_FORWARDING_ALREADY_EXISTS (sourceGuid = {sourceGuid}, endpointGuid = {endpointGuid})");
                // Only success fills in the port, so the established message carries 0.
                SendForwardingSuccess(peer, MessageId.Router2ForwardingEstablished, sourceGuid, endpointGuid, forwarding.ForwardingPort);
                break;
            case UdpForwarderResult.NoSockets:
            case UdpForwarderResult.InvalidParameters:
            case UdpForwarderResult.BindFailed:
                FailureMessage?.Invoke($"Router2 failed with {forwarding.Result}, sourceAddress={sourceAddress}, endpointSystemAddress={endpointSystemAddress}");
                SendFailureOnCannotForward(peer, sourceGuid, endpointGuid);
                break;
            case UdpForwarderResult.Success:
            {
                DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_REQUEST_FORWARDING. endpointAddress={endpointSystemAddress} sourceAddress={sourceAddress} forwardingPort={forwarding.ForwardingPort}");
                var now = (uint)peer.GetTime();
                var ping1 = AveragePing(peer, sourceGuid);
                var ping2 = AveragePing(peer, endpointGuid);
                var miniPunchRequest = new MiniPunchRequest
                {
                    EndpointAddress = endpointSystemAddress,
                    EndpointGuid = endpointGuid,
                    SourceGuid = sourceGuid,
                    SourceAddress = sourceAddress,
                    ForwardingPort = forwarding.ForwardingPort,
                    ForwardingSocket = forwarding.ForwardingSocket!,
                    Timeout = unchecked(now + (uint)((ping1 > ping2 ? ping1 : ping2) * 8) + 300),
                    NextAction = now + 100,
                };
                SendOobMessages(peer, miniPunchRequest);
                _miniPunchesInProgress.Add(miniPunchRequest);
                break;
            }
        }
    }

    // OnMiniPunchReplyBounce (Router2.cpp:983-1024).
    private void OnMiniPunchReplyBounce(RakPeer peer, RakNetGuid fromGuid)
    {
        DiagnosticMessage?.Invoke($"Got ID_ROUTER_2_MINI_PUNCH_REPLY_BOUNCE from guid={fromGuid} (miniPunchesInProgress.Size() = {_miniPunchesInProgress.Count})");
        for (var i = 0; i < _miniPunchesInProgress.Count;)
        {
            var punch = _miniPunchesInProgress[i];
            if (punch.SourceGuid != fromGuid && punch.EndpointGuid != fromGuid)
            {
                i++;
                continue;
            }
            if (punch.SourceGuid == fromGuid)
                punch.GotReplyFromSource = true;
            if (punch.EndpointGuid == fromGuid)
                punch.GotReplyFromEndpoint = true;
            DiagnosticMessage?.Invoke($"Processing ID_ROUTER_2_MINI_PUNCH_REPLY_BOUNCE, gotReplyFromSource={(punch.GotReplyFromSource ? 1 : 0)} gotReplyFromEndpoint={(punch.GotReplyFromEndpoint ? 1 : 0)}");
            if (punch.GotReplyFromEndpoint && punch.GotReplyFromSource)
            {
                SendForwardingSuccess(peer, MessageId.Router2Rerouted, punch.EndpointGuid, punch.SourceGuid, punch.ForwardingPort);
                SendForwardingSuccess(peer, MessageId.Router2ForwardingEstablished, punch.SourceGuid, punch.EndpointGuid, punch.ForwardingPort);
                RemoveAtIndexFast(_miniPunchesInProgress, i);
            }
            else
            {
                i++;
            }
        }
    }

    // OnMiniPunchReply (Router2.cpp:1025-1043): the bounce goes to the router named in the reply.
    private void OnMiniPunchReply(RakPeer peer, BitStreamReader reader)
    {
        reader.IgnoreBytes(2);
        reader.TryReadRakNetGuid(out var routerGuid);
        var router = peer.GetAddressFromGuid(routerGuid);
        SendOobFromRakNetPort(peer, OutOfBandId.Router2MiniPunchReplyBounce, [], router);
        DiagnosticMessage?.Invoke($"Sending ID_ROUTER_2_MINI_PUNCH_REPLY_BOUNCE ({router})");
    }

    // OnRerouted (Router2.cpp:1044-1100): the endpoint's side.
    private void OnRerouted(RakPeer peer, BitStreamReader reader, RakNetGuid routerGuid, SystemAddress routerAddress)
    {
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var endpointGuid);
        reader.TryReadUInt16(out var sourceToDestPort);
        var intermediaryAddress = routerAddress.WithPort(sourceToDestPort);
        peer.LowLevel.ChangeSystemAddress(endpointGuid, intermediaryAddress);

        var forwardingIndex = _forwardedConnectionList.FindIndex(f => f.EndpointGuid == endpointGuid);
        if (forwardingIndex >= 0)
        {
            var forwarded = _forwardedConnectionList[forwardingIndex];
            forwarded.IntermediaryAddress = intermediaryAddress;
            forwarded.IntermediaryGuid = routerGuid;
            peer.LowLevel.ChangeSystemAddress(endpointGuid, intermediaryAddress);
            DiagnosticMessage?.Invoke($"FIX: Got ID_ROUTER_2_REROUTE, returning ID_ROUTER_2_REROUTED, Calling RakPeer::ChangeSystemAddress({endpointGuid}, {intermediaryAddress})");
        }
        else
        {
            // Listed only so ReturnFailureOnCannotForward does not report it as a direct connection.
            _forwardedConnectionList.Add(new ForwardedConnection
            {
                EndpointGuid = endpointGuid,
                IntermediaryAddress = intermediaryAddress,
                IntermediaryGuid = routerGuid,
                WeInitiatedForwarding = false,
            });
            peer.LowLevel.ChangeSystemAddress(endpointGuid, intermediaryAddress);
            DiagnosticMessage?.Invoke("Got ID_ROUTER_2_REROUTE, returning ID_ROUTER_2_REROUTED, Calling RakPeer::ChangeSystemAddress");
        }
    }

    // OnForwardingSuccess (Router2.cpp:1101-1173): the source's side. True when the ID is to be rewritten to
    // ID_ROUTER_2_REROUTED, as for forwarding re-established after a router was lost.
    private bool OnForwardingSuccess(RakPeer peer, PeerEvent e)
    {
        var reader = e.GetReader();
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var endpointGuid);
        reader.TryReadUInt16(out var sourceToDestPort);
        var intermediaryAddress = e.Address.WithPort(sourceToDestPort);

        var forwardingIndex = _forwardedConnectionList.FindIndex(f => f.EndpointGuid == endpointGuid);
        if (forwardingIndex >= 0)
        {
            peer.LowLevel.ChangeSystemAddress(endpointGuid, intermediaryAddress);
            var forwarded = _forwardedConnectionList[forwardingIndex];
            forwarded.IntermediaryAddress = intermediaryAddress;
            forwarded.IntermediaryGuid = e.RakNetGuid;
            DiagnosticMessage?.Invoke("Got ID_ROUTER_2_FORWARDING_ESTABLISHED, returning ID_ROUTER_2_REROUTED, Calling RakPeer::ChangeSystemAddress");
            return true;
        }

        // Moved from the requests to the forwarded connections. RakNet indexes the request list with -1 when there is
        // none; here the connection is listed without failure reporting.
        var connectionRequestIndex = GetConnectionRequestIndex(endpointGuid);
        var returnConnectionLostOnFailure = false;
        if (connectionRequestIndex >= 0)
        {
            returnConnectionLostOnFailure = _connectionRequests[connectionRequestIndex].ReturnConnectionLostOnFailure;
            RemoveAtIndexFast(_connectionRequests, connectionRequestIndex);
        }
        _forwardedConnectionList.Add(new ForwardedConnection
        {
            EndpointGuid = endpointGuid,
            IntermediaryAddress = intermediaryAddress,
            IntermediaryGuid = e.RakNetGuid,
            ReturnConnectionLostOnFailure = returnConnectionLostOnFailure,
            WeInitiatedForwarding = true,
        });
        DiagnosticMessage?.Invoke("Got and returning to user ID_ROUTER_2_FORWARDING_ESTABLISHED");
        return false;
    }

    // GetLargestPingAmongConnectedSystems (Router2.cpp:1174-1196): 9999 for a peer that takes no connections.
    private static int GetLargestPingAmongConnectedSystems(RakPeer peer)
    {
        if (peer.MaxConnections == 0)
            return 9999;
        var largestPing = -1;
        foreach (var connection in peer.Connections)
            largestPing = Math.Max(largestPing, AveragePing(connection));
        return largestPing;
    }

    // ReturnToUser (Router2.cpp:1219-1228): two bytes pushed at the head, from no address and the endpoint's GUID. The
    // second byte is left as allocated in RakNet.
    private void ReturnToUser(MessageId messageId, RakNetGuid endpointGuid, bool wasGeneratedLocally) =>
        _host?.Inject([(byte)messageId, 0], SystemAddress.Unassigned, endpointGuid, atHead: true, generatedLocally: wasGeneratedLocally);

    private int GetConnectionRequestIndex(RakNetGuid endpointGuid) => _connectionRequests.FindIndex(r => r.EndpointGuid == endpointGuid);

    // RakPeer::SetTimeoutTime(GetTimeoutTime(address) + 10000, address).
    private static void AddToTimeout(RakPeer peer, SystemAddress address)
    {
        if (peer.TryGetConnection(address, out var connection))
            connection.Timeout += TimeoutIncrease;
    }

    // RakPeer::GetAveragePing: -1 without a connection or a ping.
    private static int AveragePing(RakPeer peer, RakNetGuid guid) =>
        peer.TryGetConnection(guid, out var connection) ? AveragePing(connection) : -1;

    private static int AveragePing(RakConnection connection) =>
        connection.AveragePing is { } ping ? (int)ping.TotalMilliseconds : -1;

    private void StartForwarder(IPluginHost host)
    {
        _udpForwarder ??= new UdpForwarder(host.Peer!.LowLevel.TransportFactory, host.TimeProvider, _forwarderThreading);
        _udpForwarder.Startup();
    }

    private void ClearAll()
    {
        _connectionRequests.Clear();
        _miniPunchesInProgress.Clear();
        _forwardedConnectionList.Clear();
    }

    // DataStructures::List::RemoveAtIndexFast: the last entry takes the removed one's place.
    private static void RemoveAtIndexFast<T>(List<T> list, int index)
    {
        list[index] = list[^1];
        list.RemoveAt(list.Count - 1);
    }

    private enum RequestState
    {
        QueryForwarding,
        RequestForwarding,
    }

    /// <summary>Router2::ConnectionRequestSystem (Router2.h:96-101).</summary>
    private sealed class ConnectionRequestSystem
    {
        public RakNetGuid Guid { get; init; }

        public int PingToEndpoint { get; set; }

        public ushort UsedForwardingEntries { get; set; }
    }

    /// <summary>Router2::ConnnectRequest (Router2.h:103-116).</summary>
    private sealed class ConnectRequest
    {
        public List<ConnectionRequestSystem> ConnectionRequestSystems { get; } = [];

        public RequestState RequestState { get; set; }

        public uint PingTimeout { get; init; }

        public RakNetGuid EndpointGuid { get; init; }

        public RakNetGuid LastRequestedForwardingSystem { get; set; } = RakNetGuid.Unassigned;

        public bool ReturnConnectionLostOnFailure { get; init; }

        public int GetGuidIndex(RakNetGuid guid) => ConnectionRequestSystems.FindIndex(s => s.Guid == guid);
    }

    /// <summary>Router2::MiniPunchRequest (Router2.h:120-132).</summary>
    private sealed class MiniPunchRequest
    {
        public RakNetGuid EndpointGuid { get; init; }

        public SystemAddress EndpointAddress { get; init; }

        public bool GotReplyFromEndpoint { get; set; }

        public RakNetGuid SourceGuid { get; init; }

        public SystemAddress SourceAddress { get; init; }

        public bool GotReplyFromSource { get; set; }

        public uint Timeout { get; init; }

        public uint NextAction { get; set; }

        public ushort ForwardingPort { get; init; }

        public required IDatagramTransport ForwardingSocket { get; init; }
    }

    /// <summary>Router2::ForwardedConnection (Router2.h:134-141).</summary>
    private sealed class ForwardedConnection
    {
        public RakNetGuid EndpointGuid { get; init; }

        public RakNetGuid IntermediaryGuid { get; set; }

        public SystemAddress IntermediaryAddress { get; set; }

        public bool ReturnConnectionLostOnFailure { get; init; }

        public bool WeInitiatedForwarding { get; init; }
    }
}
