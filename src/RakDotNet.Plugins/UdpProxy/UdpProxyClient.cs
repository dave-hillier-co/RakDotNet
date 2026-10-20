using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// UDPProxyClient: asks a <see cref="UdpProxyCoordinator"/> to have one of its <see cref="UdpProxyServer"/>s forward
/// datagrams between this system, or another, and a target, when NAT punchthrough fails.
/// </summary>
/// <remarks>
/// <para>
/// Ported from UDPProxyClient.cpp. <see cref="RequestForwarding(SystemAddress, SystemAddress, RakNetGuid, TimeSpan, BitStream?)"/>
/// sends ID_UDP_PROXY_FORWARDING_REQUEST_FROM_CLIENT_TO_COORDINATOR: the source address (unassigned for this system),
/// whether the target is named by address, the target's address or GUID, the timeout as a uint32 of milliseconds, and
/// an optional bit stream for the coordinator. The outcome comes back to <see cref="ResultHandler"/>. When the
/// coordinator has more than one server, it first asks both systems to ping every server: each is pinged unconnected
/// and the round trips go back to the coordinator once all have answered or after 1 s, a server that did not answer
/// counting 1000 ms. A target told it is being forwarded to pings the proxy, to open its own router.
/// </para>
/// <para>
/// RakNet's quirks are kept: a server whose round trip is exactly 1000 ms counts as not answered, an
/// ID_UNCONNECTED_PONG from a server being pinged is consumed, and ID_UDP_PROXY_PING_SERVERS_FROM_COORDINATOR_TO_CLIENT
/// is passed on to the application. One is not: RakNet reads the server count of that message where the coordinator
/// wrote the target's GUID (UDPProxyClient.cpp:285-292), so it pings up to 65535 addresses read past the message's end
/// from uninitialised memory; this client reads the GUID first, as the coordinator wrote it. Every message is MEDIUM_PRIORITY and RELIABLE_ORDERED on
/// channel 0; times are RakNet::TimeMS, 32 bits, on the peer's clock.
/// </para>
/// <para>Members may be called from any thread; hooks and the result handler run on the dispatch step.</para>
/// </remarks>
public sealed class UdpProxyClient : IRakPlugin
{
    // DEFAULT_UNRESPONSIVE_PING_TIME_COORDINATOR (UDPProxyClient.cpp:21).
    private const ushort UnresponsivePingTimeMs = 1000;

    private readonly Lock _gate = new();
    private readonly List<PingServerGroup> _pingServerGroups = [];
    private IPluginHost? _host;

    /// <summary>SetResultHandler: receives the outcome of every request. Requests are refused while it is null.</summary>
    public IUdpProxyClientResultHandler? ResultHandler { get; set; }

    private RakPeer Peer => _host?.Peer ?? throw new InvalidOperationException("The UdpProxyClient is not attached to a RakPeer.");

    /// <summary>
    /// RequestForwarding: asks the coordinator to forward between the source and the system with
    /// <paramref name="targetGuid"/>, which must be connected to the coordinator. False, sending nothing, when not
    /// connected to the coordinator or with no <see cref="ResultHandler"/>.
    /// </summary>
    /// <param name="proxyCoordinator">A system running <see cref="UdpProxyCoordinator"/> that this system is connected to.</param>
    /// <param name="sourceAddress">The external address of the system to forward from; unassigned for this system.</param>
    /// <param name="targetGuid">The system to forward to.</param>
    /// <param name="timeoutOnNoData">
    /// How long forwarding lasts with nothing forwarded, at most <see cref="UdpForwarder.MaximumTimeout"/>.
    /// </param>
    /// <param name="serverSelection">Data for the coordinator's choice of server; RakNet's coordinator ignores it.</param>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool RequestForwarding(SystemAddress proxyCoordinator, SystemAddress sourceAddress, RakNetGuid targetGuid, TimeSpan timeoutOnNoData,
                                  BitStream? serverSelection = null) =>
        Request(proxyCoordinator, sourceAddress, usesAddress: false, default, targetGuid, timeoutOnNoData, serverSelection);

    /// <summary>
    /// RequestForwarding to <paramref name="targetAddressAsSeenFromCoordinator"/>, which need not be connected to the
    /// coordinator; a target that is not must open its own router to the proxy. False, sending nothing, when not
    /// connected to the coordinator or with no <see cref="ResultHandler"/>.
    /// </summary>
    /// <param name="proxyCoordinator">A system running <see cref="UdpProxyCoordinator"/> that this system is connected to.</param>
    /// <param name="sourceAddress">The external address of the system to forward from; unassigned for this system.</param>
    /// <param name="targetAddressAsSeenFromCoordinator">The external address of the system to forward to.</param>
    /// <param name="timeoutOnNoData">
    /// How long forwarding lasts with nothing forwarded, at most <see cref="UdpForwarder.MaximumTimeout"/>.
    /// </param>
    /// <param name="serverSelection">Data for the coordinator's choice of server; RakNet's coordinator ignores it.</param>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a <see cref="RakPeer"/>.</exception>
    public bool RequestForwarding(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddressAsSeenFromCoordinator,
                                  TimeSpan timeoutOnNoData, BitStream? serverSelection = null) =>
        Request(proxyCoordinator, sourceAddress, usesAddress: true, targetAddressAsSeenFromCoordinator, default, timeoutOnNoData, serverSelection);

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnRakPeerShutdown: forgets the pings in progress.</summary>
    void IRakPlugin.OnShutdown()
    {
        lock (_gate)
            _pingServerGroups.Clear();
    }

    /// <summary>Update (UDPProxyClient.cpp:133-150): servers that have not answered in 1 s are given up on.</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        lock (_gate)
        {
            var now = (uint)peer.GetTime();
            for (var i = 0; i < _pingServerGroups.Count;)
            {
                var group = _pingServerGroups[i];
                if (group.ServersToPing.Count > 0 && now > unchecked(group.StartPingTime + UnresponsivePingTimeMs))
                {
                    group.SendPingedServersToCoordinator(peer);
                    _pingServerGroups.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }
    }

    /// <summary>OnReceive (UDPProxyClient.cpp:151-272).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host?.Peer is not { } peer)
            return PluginResult.Continue;
        if (data[0] == (byte)MessageId.UnconnectedPong)
        {
            lock (_gate)
                return OnPong(peer, e) ? PluginResult.Consume : PluginResult.Continue;
        }
        if (data[0] != (byte)MessageId.UdpProxyGeneral || data.Length <= 1)
            return PluginResult.Continue;
        switch ((UdpProxyMessage)data[1])
        {
            case UdpProxyMessage.PingServersFromCoordinatorToClient:
                // RakNet passes the message on after starting the pings.
                lock (_gate)
                    OnPingServers(peer, e.GetReader(), e.Address);
                return PluginResult.Continue;
            case UdpProxyMessage.ForwardingSucceeded:
            case UdpProxyMessage.AllServersBusy:
            case UdpProxyMessage.InProgress:
            case UdpProxyMessage.NoServersOnline:
            case UdpProxyMessage.RecipientGuidNotConnectedToCoordinator:
            case UdpProxyMessage.ForwardingNotification:
                OnResult(peer, (UdpProxyMessage)data[1], e.GetReader(), e.Address);
                return PluginResult.Consume;
            default:
                return PluginResult.Continue;
        }
    }

    private bool Request(SystemAddress proxyCoordinator, SystemAddress sourceAddress, bool usesAddress, SystemAddress targetAddress,
                         RakNetGuid targetGuid, TimeSpan timeoutOnNoData, BitStream? serverSelection)
    {
        var peer = Peer;
        if (peer.GetConnectionState(proxyCoordinator) != ConnectionState.Connected)
            return false;
        // Not setting the result handler is pretty much a bug, as the outcome would never be known.
        if (ResultHandler is null)
            return false;
        using var stream = UdpProxyWire.Begin(UdpProxyMessage.ForwardingRequestFromClientToCoordinator);
        stream.WriteAddress(sourceAddress);
        stream.WriteBool(usesAddress);
        if (usesAddress)
            stream.WriteAddress(targetAddress);
        else
            stream.WriteRakNetGuid(targetGuid);
        stream.WriteUInt32(UdpProxyWire.ToTimeMs(timeoutOnNoData));
        if (serverSelection is not null && serverSelection.BitsWritten > 0)
        {
            stream.WriteBool(true);
            var reader = serverSelection.CreateReader();
            stream.Write(ref reader);
        }
        else
        {
            stream.WriteBool(false);
        }
        UdpProxyWire.Send(peer, stream, proxyCoordinator);
        return true;
    }

    // ID_UNCONNECTED_PONG from a server being pinged: its round trip, then the coordinator once all have answered.
    private bool OnPong(RakPeer peer, PeerEvent e)
    {
        for (var i = 0; i < _pingServerGroups.Count; i++)
        {
            var group = _pingServerGroups[i];
            for (var j = 0; j < group.ServersToPing.Count; j++)
            {
                if (group.ServersToPing[j].ServerAddress != e.Address)
                    continue;
                var reader = e.GetReader();
                reader.IgnoreBytes(1);
                reader.TryReadUInt32(out var sentTime);
                var curTime = (uint)peer.GetTime();
                var ping = curTime > sentTime ? (int)(curTime - sentTime) : 0;
                group.ServersToPing[j] = group.ServersToPing[j] with { Ping = unchecked((ushort)ping) };
                if (group.AreAllServersPinged())
                {
                    group.SendPingedServersToCoordinator(peer);
                    _pingServerGroups.RemoveAt(i);
                }
                return true;
            }
        }
        return false;
    }

    // OnPingServers (UDPProxyClient.cpp:277-303).
    private void OnPingServers(RakPeer peer, BitStreamReader reader, SystemAddress coordinator)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var senderClientAddress);
        reader.TryReadSystemAddress(out var targetClientAddress);
        var group = new PingServerGroup(senderClientAddress, targetClientAddress, (uint)peer.GetTime(), coordinator);
        // The coordinator writes the target's GUID here (UDPProxyCoordinator.cpp:246); RakNet reads the server count from
        // its first two bytes instead, then addresses past the end of the message.
        reader.TryReadRakNetGuid(out _);
        reader.TryReadUInt16(out var serverListSize);
        for (var i = 0; i < serverListSize && reader.TryReadSystemAddress(out var serverAddress); i++)
        {
            group.ServersToPing.Add(new ServerWithPing(UnresponsivePingTimeMs, serverAddress));
            peer.PingUnconnected(serverAddress);
        }
        _pingServerGroups.Add(group);
    }

    private void OnResult(RakPeer peer, UdpProxyMessage message, BitStreamReader reader, SystemAddress coordinator)
    {
        reader.IgnoreBytes(2);
        reader.TryReadSystemAddress(out var senderAddress);
        reader.TryReadSystemAddress(out var targetAddress);
        reader.TryReadRakNetGuid(out var targetGuid);
        var handler = ResultHandler;
        switch (message)
        {
            case UdpProxyMessage.ForwardingNotification:
            case UdpProxyMessage.ForwardingSucceeded:
            case UdpProxyMessage.InProgress:
            {
                var serverIp = reader.TryReadRakStringBytes(out var ipBytes) ? UdpProxyWire.FromRakString(ipBytes) : "";
                reader.TryReadUInt16(out var forwardingPort);
                if (message == UdpProxyMessage.ForwardingSucceeded)
                {
                    handler?.OnForwardingSuccess(serverIp, forwardingPort, coordinator, senderAddress, targetAddress, targetGuid, this);
                }
                else if (message == UdpProxyMessage.InProgress)
                {
                    handler?.OnForwardingInProgress(serverIp, forwardingPort, coordinator, senderAddress, targetAddress, targetGuid, this);
                }
                else
                {
                    // A datagram to the proxy, so a router this system is behind lets the forwarded datagrams in. It
                    // need not arrive, only pass through the router.
                    Ping(peer, serverIp, forwardingPort);
                    handler?.OnForwardingNotification(serverIp, forwardingPort, coordinator, senderAddress, targetAddress, targetGuid, this);
                }
                break;
            }
            case UdpProxyMessage.AllServersBusy:
                handler?.OnAllServersBusy(coordinator, senderAddress, targetAddress, targetGuid, this);
                break;
            case UdpProxyMessage.NoServersOnline:
                handler?.OnNoServersOnline(coordinator, senderAddress, targetAddress, targetGuid, this);
                break;
            case UdpProxyMessage.RecipientGuidNotConnectedToCoordinator:
                handler?.OnRecipientNotConnected(coordinator, senderAddress, targetAddress, targetGuid, this);
                break;
        }
    }

    // RakPeer::Ping(host, port, false): resolves a host name, as RakNet does.
    private static void Ping(RakPeer peer, string host, ushort port)
    {
        if (IPAddress.TryParse(host, out var address))
            peer.PingUnconnected(new SystemAddress(address, port));
        else if (host.Length > 0)
            _ = peer.PingUnconnectedAsync(host, port).AsTask().ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }

    /// <summary>UDPProxyClient::ServerWithPing (UDPProxyClient.h:142-146).</summary>
    private readonly record struct ServerWithPing(ushort Ping, SystemAddress ServerAddress);

    /// <summary>UDPProxyClient::PingServerGroup (UDPProxyClient.h:152-161).</summary>
    private sealed class PingServerGroup(SystemAddress senderClientAddress, SystemAddress targetClientAddress, uint startPingTime, SystemAddress coordinator)
    {
        public uint StartPingTime { get; } = startPingTime;

        public List<ServerWithPing> ServersToPing { get; } = [];

        public bool AreAllServersPinged() => ServersToPing.TrueForAll(s => s.Ping != UnresponsivePingTimeMs);

        // ID_UDP_PROXY_PING_SERVERS_REPLY_FROM_CLIENT_TO_COORDINATOR (UDPProxyClient.cpp:315-331).
        public void SendPingedServersToCoordinator(RakPeer peer)
        {
            using var stream = UdpProxyWire.Begin(UdpProxyMessage.PingServersReplyFromClientToCoordinator);
            stream.WriteAddress(senderClientAddress);
            stream.WriteAddress(targetClientAddress);
            stream.WriteUInt16((ushort)ServersToPing.Count);
            foreach (var server in ServersToPing)
            {
                stream.WriteAddress(server.ServerAddress);
                stream.WriteUInt16(server.Ping);
            }
            UdpProxyWire.Send(peer, stream, coordinator);
        }
    }
}
