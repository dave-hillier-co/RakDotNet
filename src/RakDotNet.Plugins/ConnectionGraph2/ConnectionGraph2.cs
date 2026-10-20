using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>One connection of a remote system, as that system reported it: ConnectionGraph2::SystemAddressAndGuid.</summary>
/// <param name="Address">The address the remote system sees the other system at.</param>
/// <param name="RakNetGuid">The other system's GUID.</param>
/// <param name="SendersPing">The remote system's average ping to the other system when it reported it; 65535 for none.</param>
public readonly record struct ConnectionGraphConnection(SystemAddress Address, RakNetGuid RakNetGuid, ushort SendersPing);

/// <summary>
/// ConnectionGraph2: a one-hop connection graph. Tells every connected system about each new connection and each lost
/// or closed one, and keeps, for every system this one is connected to, the list of systems that system is connected to.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ConnectionGraph2.cpp. A new connection is announced with ID_REMOTE_NEW_INCOMING_CONNECTION: a big-endian
/// 32-bit count, then per system its SystemAddress, RakNetGUID and the sender's average ping to it as a 16-bit value.
/// The other systems are told about the new one, and the new one about every other connected system when there is one.
/// A closed connection is announced to every other system with ID_REMOTE_DISCONNECTION_NOTIFICATION, or
/// ID_REMOTE_CONNECTION_LOST when it was lost, followed by its SystemAddress and RakNetGUID. Everything is sent
/// HIGH_PRIORITY and RELIABLE_ORDERED on channel 0, and every message reaches the application.
/// </para>
/// <para>
/// Remote systems and their connection lists are kept in GUID order, as RakNet's ordered lists keep them. A message from a
/// system that is not a participant is ignored, as is a truncated one, where RakNet would read uninitialised values.
/// RakNet's quirk in <see cref="GetLowestAveragePingSystem"/> is kept: the starting value is the ping to the last remote
/// system divided by the number of remote systems with a ping (ConnectionGraph2.cpp:116-133).
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class ConnectionGraph2 : IRakPlugin
{
    private const ushort NoPing = ushort.MaxValue;

    private readonly Lock _gate = new();
    private readonly SortedList<RakNetGuid, SortedList<RakNetGuid, ConnectionGraphConnection>> _remoteSystems = [];
    private IPluginHost? _host;
    private volatile bool _autoProcessNewConnections = true;

    /// <summary>
    /// SetAutoProcessNewConnections / GetAutoProcessNewConnections: whether every new connection is added with
    /// <see cref="AddParticipant"/> as it connects. True by default; set it false to add systems after validating them.
    /// </summary>
    public bool AutoProcessNewConnections
    {
        get => _autoProcessNewConnections;
        set => _autoProcessNewConnections = value;
    }

    /// <summary>
    /// GetConnectionListForRemoteSystem: the systems <paramref name="remoteSystem"/> reported being connected to, in GUID
    /// order. False for <see cref="RakNetGuid.Unassigned"/>, this system or a system that is not a participant.
    /// </summary>
    public bool TryGetConnectionList(RakNetGuid remoteSystem, out IReadOnlyList<ConnectionGraphConnection> connections)
    {
        lock (_gate)
        {
            if (remoteSystem.IsAssigned && _remoteSystems.TryGetValue(remoteSystem, out var list))
            {
                connections = [.. list.Values];
                return true;
            }
        }
        connections = [];
        return false;
    }

    /// <summary>ConnectionExists: <paramref name="g1"/> is a participant that reported a connection to <paramref name="g2"/>.</summary>
    public bool ConnectionExists(RakNetGuid g1, RakNetGuid g2)
    {
        if (g1 == g2)
            return false;
        lock (_gate)
            return _remoteSystems.TryGetValue(g1, out var list) && list.ContainsKey(g2);
    }

    /// <summary>
    /// GetPingBetweenSystems: 0 for the same system, this system's average ping when either is this system, otherwise
    /// what <paramref name="g1"/> reported for <paramref name="g2"/>; 65535 where RakNet returns (uint16_t)-1.
    /// </summary>
    public ushort GetPingBetweenSystems(RakNetGuid g1, RakNetGuid g2)
    {
        if (g1 == g2)
            return 0;
        var host = _host;
        if (host is not null && g1 == host.RakNetGuid)
            return unchecked((ushort)AveragePing(host, g2));
        if (host is not null && g2 == host.RakNetGuid)
            return unchecked((ushort)AveragePing(host, g1));
        lock (_gate)
        {
            if (!_remoteSystems.TryGetValue(g1, out var list) || !list.TryGetValue(g2, out var connection))
                return NoPing;
            return connection.SendersPing;
        }
    }

    /// <summary>
    /// GetLowestAveragePingSystem: the participant whose reported pings average lowest, or this system when none averages
    /// below this system's own figure (ConnectionGraph2.cpp:107-158).
    /// </summary>
    public RakNetGuid GetLowestAveragePingSystem()
    {
        var host = RequireHost();
        lock (_gate)
        {
            var lowestPing = -1.0f;
            var lowestIndex = -1;
            var thisAveragePing = 0.0f;
            var count = 0;
            foreach (var guid in _remoteSystems.Keys)
            {
                // ConnectionGraph2.cpp:120: the sum restarts for every system, so only the last ping counts.
                thisAveragePing = 0.0f;
                var ping = AveragePing(host, guid);
                if (ping != -1)
                {
                    thisAveragePing += ping;
                    count++;
                }
            }
            if (count > 0)
                lowestPing = thisAveragePing / count;

            for (var index = 0; index < _remoteSystems.Count; index++)
            {
                thisAveragePing = 0.0f;
                count = 0;
                foreach (var connection in _remoteSystems.Values[index].Values)
                {
                    // ConnectionGraph2.cpp:142: a uint16_t is never -1, so 65535 counts as a ping.
                    thisAveragePing += connection.SendersPing;
                    count++;
                }
                if (count > 0 && (lowestPing == -1.0f || thisAveragePing / count < lowestPing))
                {
                    lowestPing = thisAveragePing / count;
                    lowestIndex = index;
                }
            }
            return lowestIndex == -1 ? host.RakNetGuid : _remoteSystems.Keys[lowestIndex];
        }
    }

    /// <summary>
    /// AddParticipant: announces <paramref name="rakNetGuid"/> to every other connected system, sends it every other
    /// connected system, and starts its connection list. Called for each new connection while
    /// <see cref="AutoProcessNewConnections"/> is true.
    /// </summary>
    public void AddParticipant(SystemAddress address, RakNetGuid rakNetGuid)
    {
        var host = RequireHost();
        var peer = host.Peer ?? throw new InvalidOperationException("ConnectionGraph2 needs a RakPeer host.");
        lock (_gate)
        {
            // ConnectionGraph2.cpp:200-206: the new system, to everyone else.
            using var announcement = new BitStream(1 + sizeof(uint) + EntrySize);
            announcement.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
            announcement.WriteUInt32(1);
            WriteEntry(announcement, address, rakNetGuid, AveragePing(host, rakNetGuid));
            host.Broadcast(announcement, Reliability.ReliableOrdered, Priority.High, 0, AddressOrGuidOf(address));

            // ConnectionGraph2.cpp:208-237: everyone else, to the new system, when there is anyone.
            var others = peer.Connections.Where(c => c.Address != address).ToList();
            if (others.Count > 0)
            {
                using var list = new BitStream(1 + sizeof(uint) + others.Count * EntrySize);
                list.WriteByte((byte)MessageId.RemoteNewIncomingConnection);
                list.WriteUInt32((uint)others.Count);
                foreach (var other in others)
                    WriteEntry(list, other.Address, other.RakNetGuid, AveragePing(host, other.RakNetGuid));
                if (address.IsAssigned)
                    host.Send(list, address, Reliability.ReliableOrdered, Priority.High, 0);
            }

            if (!_remoteSystems.ContainsKey(rakNetGuid))
                _remoteSystems.Add(rakNetGuid, []);
        }
    }

    /// <summary>GetParticipantList: the systems added with <see cref="AddParticipant"/> and still connected, in GUID order.</summary>
    public IReadOnlyList<RakNetGuid> GetParticipantList()
    {
        lock (_gate)
            return [.. _remoteSystems.Keys];
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnNewConnection (ConnectionGraph2.cpp:252-257).</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        if (_autoProcessNewConnections)
            AddParticipant(connection.Address, connection.RakNetGuid);
    }

    /// <summary>OnClosedConnection (ConnectionGraph2.cpp:160-180): everyone else is told, and the system's list is dropped.</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        var host = RequireHost();
        using var notice = new BitStream(1 + EntryAddressAndGuidSize);
        notice.WriteByte((byte)(reason == DisconnectReason.ConnectionLost ? MessageId.RemoteConnectionLost : MessageId.RemoteDisconnectionNotification));
        notice.WriteAddress(connection.Address);
        notice.WriteRakNetGuid(connection.RakNetGuid);
        host.Broadcast(notice, Reliability.ReliableOrdered, Priority.High, 0, AddressOrGuidOf(connection.Address));

        lock (_gate)
            _remoteSystems.Remove(connection.RakNetGuid);
    }

    /// <summary>OnReceive (ConnectionGraph2.cpp:258-306). Every message continues to the application.</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        switch ((MessageId)data[0])
        {
            case MessageId.RemoteConnectionLost:
            case MessageId.RemoteDisconnectionNotification:
                OnRemoteClosed(e.GetReader(), e.RakNetGuid);
                break;
            case MessageId.RemoteNewIncomingConnection:
                OnRemoteNewIncomingConnection(e.GetReader(), e.RakNetGuid);
                break;
        }
        return PluginResult.Continue;
    }

    private const int EntryAddressAndGuidSize = SystemAddress.WireSize + sizeof(ulong);

    private const int EntrySize = EntryAddressAndGuidSize + sizeof(ushort);

    private void OnRemoteClosed(BitStreamReader reader, RakNetGuid sender)
    {
        lock (_gate)
        {
            if (!_remoteSystems.TryGetValue(sender, out var list))
                return;
            reader.IgnoreBytes(1);
            if (!reader.TryReadSystemAddress(out _) || !reader.TryReadRakNetGuid(out var guid))
                return;
            list.Remove(guid);
        }
    }

    private void OnRemoteNewIncomingConnection(BitStreamReader reader, RakNetGuid sender)
    {
        lock (_gate)
        {
            if (!_remoteSystems.TryGetValue(sender, out var list))
                return;
            reader.IgnoreBytes(1);
            if (!reader.TryReadUInt32(out var count))
                return;
            for (var i = 0u; i < count; i++)
            {
                if (!reader.TryReadSystemAddress(out var address) || !reader.TryReadRakNetGuid(out var guid) || !reader.TryReadUInt16(out var ping))
                    return;
                // ConnectionGraph2.cpp:296-298: a system already listed keeps its first entry.
                list.TryAdd(guid, new ConnectionGraphConnection(address, guid, ping));
            }
        }
    }

    // bs.Write(SystemAddress); bs.Write(RakNetGUID); bs.WriteCasted<uint16_t>(GetAveragePing(guid)).
    private static void WriteEntry(BitStream stream, SystemAddress address, RakNetGuid rakNetGuid, int averagePing)
    {
        stream.WriteAddress(address);
        stream.WriteRakNetGuid(rakNetGuid);
        stream.WriteUInt16(unchecked((ushort)averagePing));
    }

    // RakPeer::GetAveragePing: -1 for no such system or no ping sample yet (RakPeer.cpp:2149-2169).
    internal static int AveragePing(IPluginHost host, RakNetGuid rakNetGuid) =>
        host.Peer is { } peer && peer.TryGetConnection(rakNetGuid, out var connection) && connection.AveragePing is { } ping
            ? (int)ping.TotalMilliseconds
            : -1;

    // A broadcast excludes this address; UNASSIGNED_SYSTEM_ADDRESS excludes nobody.
    private static AddressOrGuid AddressOrGuidOf(SystemAddress address) => address.IsAssigned ? address : default;

    private IPluginHost RequireHost() =>
        _host ?? throw new InvalidOperationException("The ConnectionGraph2 is not attached to a peer.");
}
