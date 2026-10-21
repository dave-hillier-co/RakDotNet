using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// UDPProxyCoordinator: keeps the list of <see cref="UdpProxyServer"/>s that have logged in and finds one to forward
/// for each <see cref="UdpProxyClient"/> request.
/// </summary>
/// <remarks>
/// <para>
/// Ported from UDPProxyCoordinator.cpp. A server logs in with the password set by <see cref="RemoteLoginPassword"/>;
/// with none set no server can. A client's forwarding request is refused as in progress when the pair, or the reverse
/// pair, already has a request, which is kept for the forwarding timeout after it succeeds; as having no servers when
/// none is logged in; and as not connected when the target named by GUID is not connected. With one server the
/// request goes straight to it. With more, both systems are asked to ping every server, and the servers are tried in
/// order of the sum of the two round trips once both have answered or after 3 s. A server that is out of forwarding
/// entries, or disconnects, passes the request to the next; with none left the client is told all are busy. On success
/// the requester gets ID_UDP_PROXY_FORWARDING_SUCCEEDED and the target ID_UDP_PROXY_FORWARDING_NOTIFICATION, both with
/// the server's public address, or the address it is connected from, and the forwarding port. Every message is
/// MEDIUM_PRIORITY and RELIABLE_ORDERED on channel 0; times are RakNet::TimeMS, 32 bits, on the peer's clock.
/// </para>
/// <para>
/// RakNet's quirks are kept. The requests are an ordered list whose comparison returns "less" for any two different
/// senders (ForwardingRequestComp, UDPProxyCoordinator.cpp:33-44), so a lookup can miss a request that exists. The
/// target's ping replies are stored with the source's (UDPProxyCoordinator.cpp:518-531), so with more than one server
/// the order is only decided by the 3 s timeout, and servers whose round trips sum to the same value as an earlier one
/// are dropped from the order. A server that disconnects is still offered to the requests it was serving.
/// </para>
/// <para>
/// One deviation bounds what a peer can make the coordinator store: a ping reply names at most as many servers as the
/// request asked about, a reply whose entries run out is read only as far as they go, and a request stores at most two
/// replies' worth of pings. RakNet inserts every entry a reply counts, read or not (UDPProxyCoordinator.cpp:439-484).
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class UdpProxyCoordinator : IRakPlugin
{
    // DEFAULT_CLIENT_UNRESPONSIVE_PING_TIME and DEFAULT_UNRESPONSIVE_PING_TIME_COORDINATOR (UDPProxyCoordinator.cpp:23-24).
    private const ushort ClientUnresponsivePingTimeMs = 2000;
    private const uint UnresponsivePingTimeCoordinatorMs = ClientUnresponsivePingTimeMs + 1000;

    private readonly Lock _gate = new();
    private readonly List<SystemAddress> _serverList = [];
    private readonly List<ForwardingRequest> _forwardingRequestList = [];
    private byte[] _remoteLoginPassword = [];
    private IPluginHost? _host;

    /// <summary>
    /// SetRemoteLoginPassword: the password a <see cref="UdpProxyServer"/> must log in with. Empty, the default, takes no
    /// logins.
    /// </summary>
    public string RemoteLoginPassword
    {
        get
        {
            lock (_gate)
                return UdpProxyWire.FromRakString(_remoteLoginPassword);
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
                _remoteLoginPassword = UdpProxyWire.ToRakString(value);
        }
    }

    /// <summary>The servers logged in, in RakNet's order.</summary>
    public IReadOnlyList<SystemAddress> Servers
    {
        get
        {
            lock (_gate)
                return [.. _serverList];
        }
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>Update (UDPProxyCoordinator.cpp:67-96).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        lock (_gate)
        {
            var curTime = (uint)peer.GetTime();
            for (var idx = 0; idx < _forwardingRequestList.Count;)
            {
                var fw = _forwardingRequestList[idx];
                if (fw.TimeRequestedPings != 0 && curTime > unchecked(fw.TimeRequestedPings + UnresponsivePingTimeCoordinatorMs))
                {
                    fw.OrderRemainingServersToTry();
                    fw.TimeRequestedPings = 0;
                    TryNextServer(peer, fw.Sata, fw);
                    idx++;
                }
                else if (fw.TimeoutAfterSuccess != 0 && curTime > fw.TimeoutAfterSuccess)
                {
                    // Forwarding succeeded and was kept a while to report duplicates; it can be forgotten now.
                    _forwardingRequestList.RemoveAt(idx);
                }
                else
                {
                    idx++;
                }
            }
        }
    }

    /// <summary>OnReceive (UDPProxyCoordinator.cpp:97-121).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.Length <= 1 || data[0] != (byte)MessageId.UdpProxyGeneral || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        lock (_gate)
        {
            switch ((UdpProxyMessage)data[1])
            {
                case UdpProxyMessage.ForwardingRequestFromClientToCoordinator:
                    OnForwardingRequestFromClientToCoordinator(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                case UdpProxyMessage.LoginRequestFromServerToCoordinator:
                    OnLoginRequestFromServerToCoordinator(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                case UdpProxyMessage.ForwardingReplyFromServerToCoordinator:
                    OnForwardingReplyFromServerToCoordinator(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                case UdpProxyMessage.PingServersReplyFromClientToCoordinator:
                    OnPingServersReplyFromClientToCoordinator(peer, e.GetReader(), e.Address);
                    return PluginResult.Consume;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    /// <summary>OnClosedConnection (UDPProxyCoordinator.cpp:122-158).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        if (_host?.Peer is not { } peer)
            return;
        var systemAddress = connection.Address;
        lock (_gate)
        {
            for (var idx = 0; idx < _forwardingRequestList.Count;)
            {
                // The requester disconnected before the attempt completed.
                if (_forwardingRequestList[idx].RequestingAddress == systemAddress)
                    _forwardingRequestList.RemoveAt(idx);
                else
                    idx++;
            }

            var serverIndex = _serverList.IndexOf(systemAddress);
            if (serverIndex < 0)
                return;
            // Each request this server was serving tries the next server.
            for (var idx2 = 0; idx2 < _forwardingRequestList.Count; idx2++)
            {
                var fw = _forwardingRequestList[idx2];
                if (fw.CurrentlyAttemptedServerAddress == systemAddress)
                    TryNextServer(peer, fw.Sata, fw);
            }
            // Remove the dead server; RemoveAtIndexFast.
            _serverList[serverIndex] = _serverList[^1];
            _serverList.RemoveAt(_serverList.Count - 1);
        }
    }

    // OnForwardingRequestFromClientToCoordinator (UDPProxyCoordinator.cpp:159-275).
    private void OnForwardingRequestFromClientToCoordinator(RakPeer peer, BitStreamReader reader, SystemAddress requester)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var sourceAddress);
        if (!sourceAddress.IsAssigned)
            sourceAddress = requester;
        reader.TryReadBool(out var usesAddress);
        SystemAddress targetAddress;
        RakNetGuid targetGuid;
        if (usesAddress)
        {
            reader.TryReadSystemAddress(out targetAddress);
            targetGuid = peer.GetGuidFromAddress(targetAddress);
        }
        else
        {
            reader.TryReadRakNetGuid(out targetGuid);
            targetAddress = peer.GetAddressFromGuid(targetGuid);
        }
        reader.TryReadUInt32(out var timeoutOnNoDataMs);
        // The server selection data is read and kept, and nothing uses it.
        reader.TryReadBool(out _);

        var sata = new SenderAndTargetAddress(sourceAddress, peer.GetGuidFromAddress(sourceAddress), targetAddress, targetGuid);
        var sataReversed = new SenderAndTargetAddress(targetAddress, sata.TargetClientGuid, sourceAddress, sata.SenderClientGuid);
        var insertionIndex = GetIndexFromKey(sata, out var objectExists1);
        GetIndexFromKey(sataReversed, out var objectExists2);

        if (objectExists1 || objectExists2)
        {
            // In progress, not completed: no server address and port 0.
            using var inProgress = UdpProxyWire.Begin(UdpProxyMessage.InProgress);
            inProgress.WriteAddress(sata.SenderClientAddress);
            inProgress.WriteAddress(targetAddress);
            inProgress.WriteRakNetGuid(targetGuid);
            UdpProxyWire.WriteRakString(inProgress, []);
            inProgress.WriteUInt16(0);
            UdpProxyWire.Send(peer, inProgress, requester);
            return;
        }

        if (_serverList.Count == 0)
        {
            SendFailure(peer, UdpProxyMessage.NoServersOnline, sata.SenderClientAddress, targetAddress, targetGuid, requester);
            return;
        }

        if (peer.GetConnectionState(targetAddress) != ConnectionState.Connected && !usesAddress)
        {
            SendFailure(peer, UdpProxyMessage.RecipientGuidNotConnectedToCoordinator, sata.SenderClientAddress, targetAddress, targetGuid, requester);
            return;
        }

        var fw = new ForwardingRequest(timeoutOnNoDataMs, sata, requester);
        if (_serverList.Count > 1)
        {
            using var ping = UdpProxyWire.Begin(UdpProxyMessage.PingServersFromCoordinatorToClient);
            ping.WriteAddress(sourceAddress);
            ping.WriteAddress(targetAddress);
            ping.WriteRakNetGuid(targetGuid);
            ping.WriteUInt16((ushort)_serverList.Count);
            foreach (var server in _serverList)
                ping.WriteAddress(server);
            UdpProxyWire.Send(peer, ping, sourceAddress);
            UdpProxyWire.Send(peer, ping, targetAddress);
            fw.TimeRequestedPings = (uint)peer.GetTime();
            fw.RemainingServersToTry.AddRange(_serverList);
            _forwardingRequestList.Insert(insertionIndex, fw);
        }
        else
        {
            fw.TimeRequestedPings = 0;
            fw.CurrentlyAttemptedServerAddress = _serverList[0];
            _forwardingRequestList.Insert(insertionIndex, fw);
            SendForwardingRequest(peer, sourceAddress, targetAddress, fw.CurrentlyAttemptedServerAddress, fw.TimeoutOnNoDataMs);
        }
    }

    // SendForwardingRequest (UDPProxyCoordinator.cpp:276-287).
    private static void SendForwardingRequest(RakPeer peer, SystemAddress sourceAddress, SystemAddress targetAddress, SystemAddress serverAddress, uint timeoutOnNoDataMs)
    {
        using var stream = UdpProxyWire.Begin(UdpProxyMessage.ForwardingRequestFromCoordinatorToServer);
        stream.WriteAddress(sourceAddress);
        stream.WriteAddress(targetAddress);
        stream.WriteUInt32(timeoutOnNoDataMs);
        UdpProxyWire.Send(peer, stream, serverAddress);
    }

    // OnLoginRequestFromServerToCoordinator (UDPProxyCoordinator.cpp:288-334).
    private void OnLoginRequestFromServerToCoordinator(RakPeer peer, BitStreamReader reader, SystemAddress server)
    {
        reader.IgnoreBytes(2);
        var password = reader.TryReadRakStringBytes(out var bytes) ? bytes.ToArray() : [];
        UdpProxyMessage reply;
        if (_remoteLoginPassword.Length == 0)
        {
            reply = UdpProxyMessage.NoPasswordSetFromCoordinatorToServer;
        }
        else if (!_remoteLoginPassword.AsSpan().SequenceEqual(password))
        {
            reply = UdpProxyMessage.WrongPasswordFromCoordinatorToServer;
        }
        else if (_serverList.Contains(server))
        {
            reply = UdpProxyMessage.AlreadyLoggedInFromCoordinatorToServer;
        }
        else
        {
            _serverList.Add(server);
            reply = UdpProxyMessage.LoginSuccessFromCoordinatorToServer;
        }
        using var stream = UdpProxyWire.Begin(reply);
        UdpProxyWire.WriteRakString(stream, password);
        UdpProxyWire.Send(peer, stream, server);
    }

    // OnForwardingReplyFromServerToCoordinator (UDPProxyCoordinator.cpp:335-430).
    private void OnForwardingReplyFromServerToCoordinator(RakPeer peer, BitStreamReader reader, SystemAddress server)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var senderClientAddress);
        reader.TryReadSystemAddress(out var targetClientAddress);
        var index = GetIndexFromKey(new SenderAndTargetAddress(senderClientAddress, default, targetClientAddress, default), out var objectExists);
        // The requester disconnected before the request finished.
        if (!objectExists)
            return;
        var fw = _forwardingRequestList[index];
        var sata = new SenderAndTargetAddress(senderClientAddress, fw.Sata.SenderClientGuid, targetClientAddress, fw.Sata.TargetClientGuid);

        var serverPublicIp = reader.TryReadRakStringBytes(out var ipBytes) ? ipBytes.ToArray() : [];
        if (serverPublicIp.Length == 0)
            serverPublicIp = UdpProxyWire.ToRakString(server.ToString(writePort: false));
        reader.TryReadByte(out var result);
        reader.TryReadUInt16(out var forwardingPort);

        switch ((UdpForwarderResult)result)
        {
            case UdpForwarderResult.Success:
            {
                SendWithServer(peer, UdpProxyMessage.ForwardingSucceeded, sata, serverPublicIp, forwardingPort, fw.RequestingAddress);
                SendWithServer(peer, UdpProxyMessage.ForwardingNotification, sata, serverPublicIp, forwardingPort, sata.TargetClientAddress);
                // Kept for a while after success, so a request from the target before its notification is reported
                // as a duplicate.
                fw.TimeoutAfterSuccess = unchecked((uint)peer.GetTime() + fw.TimeoutOnNoDataMs);
                break;
            }
            case UdpForwarderResult.NoSockets:
                TryNextServer(peer, sata, fw);
                break;
            default:
                // RakNet asserts UDPFORWARDER_FORWARDING_ALREADY_EXISTS and reports any other result the same way.
                SendWithServer(peer, UdpProxyMessage.InProgress, sata, serverPublicIp, forwardingPort, fw.RequestingAddress);
                _forwardingRequestList.RemoveAt(index);
                break;
        }
    }

    // OnPingServersReplyFromClientToCoordinator (UDPProxyCoordinator.cpp:431-498).
    private void OnPingServersReplyFromClientToCoordinator(RakPeer peer, BitStreamReader reader, SystemAddress client)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var senderClientAddress);
        reader.TryReadSystemAddress(out var targetClientAddress);
        var index = GetIndexFromKey(new SenderAndTargetAddress(senderClientAddress, default, targetClientAddress, default), out var objectExists);
        if (!objectExists)
            return;
        var fw = _forwardingRequestList[index];
        if (fw.TimeRequestedPings == 0)
            return;
        reader.TryReadUInt16(out var serversToPingSize);
        var fromSender = client == senderClientAddress;
        // Deviation: RakNet inserts serversToPingSize entries whether or not they were read, so one short message
        // naming 65535 servers costs quadratic time and memory. Here a reply names at most the servers asked about,
        // stops at the first entry that is not there, and adds nothing once the two systems' replies are stored.
        var asked = fw.RemainingServersToTry.Count;
        var count = Math.Min(serversToPingSize, asked);
        for (var idx = 0; idx < count && fw.SourceServerPings.Count < 2 * asked; idx++)
        {
            if (!reader.TryReadSystemAddress(out var serverAddress) || !reader.TryReadUInt16(out var ping))
                break;
            var swp = new ServerWithPing(ping, serverAddress);
            // The position is found in the sender's list or the target's, and the reply is inserted into the
            // sender's either way.
            var list = fromSender ? fw.SourceServerPings : fw.TargetServerPings;
            var index2 = 0;
            while (index2 < list.Count && list[index2].Ping < swp.Ping)
                index2++;
            fw.SourceServerPings.Insert(Math.Min(index2, fw.SourceServerPings.Count), swp);
        }

        // Both systems have to give their pings to progress here; otherwise Update times out.
        if (fw.SourceServerPings.Count > 0 && fw.TargetServerPings.Count > 0)
        {
            fw.OrderRemainingServersToTry();
            fw.TimeRequestedPings = 0;
            TryNextServer(peer, fw.Sata, fw);
        }
    }

    // TryNextServer (UDPProxyCoordinator.cpp:499-523).
    private void TryNextServer(RakPeer peer, SenderAndTargetAddress sata, ForwardingRequest fw)
    {
        var pickedGoodServer = false;
        while (fw.RemainingServersToTry.Count > 0)
        {
            fw.CurrentlyAttemptedServerAddress = fw.RemainingServersToTry[0];
            fw.RemainingServersToTry.RemoveAt(0);
            if (_serverList.Contains(fw.CurrentlyAttemptedServerAddress))
            {
                pickedGoodServer = true;
                break;
            }
        }

        if (!pickedGoodServer)
        {
            SendFailure(peer, UdpProxyMessage.AllServersBusy, sata.SenderClientAddress, sata.TargetClientAddress, sata.TargetClientGuid, fw.RequestingAddress);
            // OrderedList::Remove with the request's key.
            var index = GetIndexFromKey(sata, out var exists);
            if (exists)
                _forwardingRequestList.RemoveAt(index);
            return;
        }
        SendForwardingRequest(peer, sata.SenderClientAddress, sata.TargetClientAddress, fw.CurrentlyAttemptedServerAddress, fw.TimeoutOnNoDataMs);
    }

    // ID_UDP_PROXY_NO_SERVERS_ONLINE, _RECIPIENT_GUID_NOT_CONNECTED_TO_COORDINATOR and _ALL_SERVERS_BUSY (SendAllBusy).
    private static void SendFailure(RakPeer peer, UdpProxyMessage message, SystemAddress senderClientAddress, SystemAddress targetClientAddress,
                                    RakNetGuid targetClientGuid, SystemAddress requestingAddress)
    {
        using var stream = UdpProxyWire.Begin(message);
        stream.WriteAddress(senderClientAddress);
        stream.WriteAddress(targetClientAddress);
        stream.WriteRakNetGuid(targetClientGuid);
        UdpProxyWire.Send(peer, stream, requestingAddress);
    }

    private static void SendWithServer(RakPeer peer, UdpProxyMessage message, SenderAndTargetAddress sata, byte[] serverPublicIp, ushort forwardingPort,
                                       SystemAddress destination)
    {
        using var stream = UdpProxyWire.Begin(message);
        stream.WriteAddress(sata.SenderClientAddress);
        stream.WriteAddress(sata.TargetClientAddress);
        stream.WriteRakNetGuid(sata.TargetClientGuid);
        UdpProxyWire.WriteRakString(stream, serverPublicIp);
        stream.WriteUInt16(forwardingPort);
        UdpProxyWire.Send(peer, stream, destination);
    }

    // DataStructures::OrderedList::GetIndexFromKey with ForwardingRequestComp.
    private int GetIndexFromKey(SenderAndTargetAddress key, out bool objectExists) =>
        OrderedListSearch.GetIndexFromKey(_forwardingRequestList, key, static (k, fw) => ForwardingRequestComp(k, fw.Sata), out objectExists);

    // ForwardingRequestComp (UDPProxyCoordinator.cpp:33-44): different senders compare as less either way.
    private static int ForwardingRequestComp(SenderAndTargetAddress key, SenderAndTargetAddress data)
    {
        if (key.SenderClientAddress < data.SenderClientAddress)
            return -1;
        if (key.SenderClientAddress > data.SenderClientAddress)
            return -1;
        if (key.TargetClientAddress < data.TargetClientAddress)
            return -1;
        if (key.TargetClientAddress > data.TargetClientAddress)
            return 1;
        return 0;
    }

    /// <summary>UDPProxyCoordinator::SenderAndTargetAddress (UDPProxyCoordinator.h:54-60).</summary>
    private readonly record struct SenderAndTargetAddress(SystemAddress SenderClientAddress, RakNetGuid SenderClientGuid, SystemAddress TargetClientAddress,
                                                          RakNetGuid TargetClientGuid);

    /// <summary>UDPProxyCoordinator::ServerWithPing (UDPProxyCoordinator.h:62-66).</summary>
    private readonly record struct ServerWithPing(ushort Ping, SystemAddress ServerAddress);

    /// <summary>UDPProxyCoordinator::ForwardingRequest (UDPProxyCoordinator.h:68-82).</summary>
    private sealed class ForwardingRequest(uint timeoutOnNoDataMs, SenderAndTargetAddress sata, SystemAddress requestingAddress)
    {
        public uint TimeoutOnNoDataMs { get; } = timeoutOnNoDataMs;

        public uint TimeoutAfterSuccess { get; set; }

        public SenderAndTargetAddress Sata { get; } = sata;

        /// <summary>Which system sent the request.</summary>
        public SystemAddress RequestingAddress { get; } = requestingAddress;

        public SystemAddress CurrentlyAttemptedServerAddress { get; set; }

        public List<SystemAddress> RemainingServersToTry { get; } = [];

        public List<ServerWithPing> SourceServerPings { get; } = [];

        public List<ServerWithPing> TargetServerPings { get; } = [];

        public uint TimeRequestedPings { get; set; }

        // OrderRemainingServersToTry (UDPProxyCoordinator.cpp:534-568): an OrderedList keyed on the summed ping, where
        // a second server with the same sum is not inserted.
        public void OrderRemainingServersToTry()
        {
            if (SourceServerPings.Count == 0 && TargetServerPings.Count == 0)
                return;
            var ordered = new List<ServerWithPing>();
            for (var idx = 0; idx < RemainingServersToTry.Count; idx++)
            {
                var ping = unchecked((ushort)((SourceServerPings.Count > 0 ? PingAt(SourceServerPings, idx) : ClientUnresponsivePingTimeMs)
                                              + (TargetServerPings.Count > 0 ? PingAt(TargetServerPings, idx) : ClientUnresponsivePingTimeMs)));
                OrderedListSearch.Insert(ordered, ping, new ServerWithPing(ping, RemainingServersToTry[idx]), ServerWithPingComp);
            }
            RemainingServersToTry.Clear();
            foreach (var server in ordered)
                RemainingServersToTry.Add(server.ServerAddress);
        }

        // ServerWithPingComp (UDPProxyCoordinator.cpp:25-32).
        private static int ServerWithPingComp(ushort key, ServerWithPing data) => key < data.Ping ? -1 : key > data.Ping ? 1 : 0;

        // RakNet indexes the ping lists by the server's position in the list to try; past their end it reads memory
        // that is not part of them, taken here as an unresponsive client.
        private static ushort PingAt(List<ServerWithPing> pings, int index) =>
            index < pings.Count ? pings[index].Ping : ClientUnresponsivePingTimeMs;
    }
}
