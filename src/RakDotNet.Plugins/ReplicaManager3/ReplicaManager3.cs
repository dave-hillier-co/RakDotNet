using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// ReplicaManager3: replicates game objects. Replicas passed to <see cref="Reference"/> are constructed on, serialized to
/// and destroyed on the connected systems that take part, as each replica's queries decide.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ReplicaManager3.cpp. Derive from it and implement <see cref="AllocConnection"/> to return your
/// <see cref="ReplicaConnection"/>, derive your objects from <see cref="Replica3"/>, set a
/// <see cref="NetworkIdManager"/> with <see cref="SetNetworkIdManager"/>, and attach it to a peer. With
/// <see cref="AutoCreateConnections"/>, every connected system takes part.
/// </para>
/// <para>
/// Each update (every dispatch step, RakNet's Receive) constructs and destroys what the queries ask for on validated
/// connections, one ID_REPLICA_MANAGER_CONSTRUCTION per connection, the first wrapped in ID_REPLICA_MANAGER_DOWNLOAD_STARTED
/// and ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE; then, once <see cref="AutoSerializeInterval"/> has passed, serializes every
/// replica to every connection as ID_REPLICA_MANAGER_SERIALIZE. A connection is validated when the other system's
/// ID_REPLICA_MANAGER_SCOPE_CHANGE shows it takes part too. Every message continues to the application, except those
/// held for a grouped download.
/// </para>
/// <para>
/// As in RakNet, a few calls act on world 0 whatever world the replica is in: the relay and broadcast of a destruction
/// received, <see cref="Replica3.BroadcastDestruction"/>, a replica's dereference when it is disposed, and connections
/// made, lost, and created by <see cref="AutoCreateConnectionList"/>. World 255 does not exist, where RakNet's array of
/// 255 worlds reads past its end. Malformed messages are ignored from the first field that cannot be read.
/// </para>
/// <para>
/// Not thread-safe, as in RakNet: call it, and change replicas, on the thread that reads the peer's events, where its
/// hooks run.
/// </para>
/// </remarks>
public abstract class ReplicaManager3 : IRakPlugin, IDisposable
{
    // RakNet's RM3World *worldsArray[255].
    private const int WorldSlots = 255;

    private readonly World?[] _worldsArray = new World?[WorldSlots];
    private readonly List<World> _worldsList = [];
    private readonly List<Replica3> _replicasToSerialize = [];
    private ReplicaSendParameters _defaultSendParameters = new(Priority.High, Reliability.ReliableOrdered, 0, 0);
    private ulong _autoSerializeInterval = 30;
    private ulong _lastAutoSerializeOccurrence;
    private Replica3? _currentlyDeallocatingReplica;
    private uint _nextReferenceIndex;
    private IPluginHost? _host;
    private SerializeParameters? _updateParameters;
    private SerializeParameters? _constructionParameters;
    private DeserializeParameters? _deserializeParameters;

    protected ReplicaManager3()
    {
        AddWorld(0);
    }

    /// <summary>GetRakPeerInterface: the host this plugin is attached to.</summary>
    public IPluginHost? Host => _host;

    /// <summary>GetAutoCreateConnections: <see cref="AllocConnection"/> and <see cref="PushConnection"/> for each new connection. True by default.</summary>
    public bool AutoCreateConnections { get; set; } = true;

    /// <summary>GetAutoDestroyConnections: <see cref="PopConnection"/> and <see cref="DeallocConnection"/> for each lost connection. True by default.</summary>
    public bool AutoDestroyConnections { get; set; } = true;

    /// <summary>SetDefaultOrderingChannel</summary>
    public byte DefaultOrderingChannel
    {
        get => _defaultSendParameters.OrderingChannel;
        set => _defaultSendParameters.OrderingChannel = value;
    }

    /// <summary>SetDefaultPacketPriority</summary>
    public Priority DefaultPriority
    {
        get => _defaultSendParameters.Priority;
        set => _defaultSendParameters.Priority = value;
    }

    /// <summary>SetDefaultPacketReliability</summary>
    public Reliability DefaultReliability
    {
        get => _defaultSendParameters.Reliability;
        set => _defaultSendParameters.Reliability = value;
    }

    /// <summary>GetDefaultSendParameters</summary>
    public ReplicaSendParameters DefaultSendParameters => _defaultSendParameters;

    /// <summary>
    /// SetAutoSerializeInterval: how often replicas are serialized, 30 ms by default; <see cref="TimeSpan.Zero"/> serializes
    /// on every update and <see cref="Timeout.InfiniteTimeSpan"/> never.
    /// </summary>
    public TimeSpan AutoSerializeInterval
    {
        get => _autoSerializeInterval == ulong.MaxValue ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(_autoSerializeInterval);
        set
        {
            if (value == Timeout.InfiniteTimeSpan)
            {
                _autoSerializeInterval = ulong.MaxValue;
                return;
            }
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            _autoSerializeInterval = (ulong)value.TotalMilliseconds;
        }
    }

    /// <summary>GetWorldCount</summary>
    public int WorldCount => _worldsList.Count;

    internal SerializeParameters ConstructionSerializeParameters => _constructionParameters ??= new SerializeParameters();

    /// <summary>AllocConnection: the connection object for a system that takes part, or null to leave it out.</summary>
    public abstract ReplicaConnection? AllocConnection(SystemAddress systemAddress, RakNetGuid rakNetGuid);

    /// <summary>DeallocConnection: the manager is done with <paramref name="connection"/>; by default it is disposed.</summary>
    public virtual void DeallocConnection(ReplicaConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.Dispose();
    }

    /// <summary>SetAutoManageConnections</summary>
    public void SetAutoManageConnections(bool autoCreate, bool autoDestroy)
    {
        AutoCreateConnections = autoCreate;
        AutoDestroyConnections = autoDestroy;
    }

    /// <summary>
    /// AutoCreateConnectionList: <see cref="AllocConnection"/> and <see cref="PushConnection"/> for each of
    /// <paramref name="participantListIn"/> without a connection in <paramref name="worldId"/>; the new connections are
    /// added to <paramref name="participantListOut"/>. As in RakNet they are pushed to world 0.
    /// </summary>
    public void AutoCreateConnectionList(IReadOnlyList<RakNetGuid> participantListIn, List<ReplicaConnection> participantListOut, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(participantListIn);
        ArgumentNullException.ThrowIfNull(participantListOut);
        var host = RequireHost();
        foreach (var guid in participantListIn)
        {
            if (GetConnectionByGuid(guid, worldId) is not null)
                continue;
            var address = host.Peer is { } peer ? peer.GetAddressFromGuid(guid) : SystemAddress.Unassigned;
            if (AllocConnection(address, guid) is { } connection)
            {
                PushConnection(connection);
                participantListOut.Add(connection);
            }
        }
    }

    /// <summary>
    /// PushConnection: <paramref name="newConnection"/> takes part. It is sent the validation message, and with a query
    /// construction mode every replica is queried for it. False when a connection with its GUID is already in the world.
    /// </summary>
    public bool PushConnection(ReplicaConnection newConnection, byte worldId = 0)
    {
        if (newConnection is null)
            return false;
        if (GetConnectionByGuid(newConnection.RakNetGuid, worldId) is not null)
            return false;
        var world = RequireWorld(worldId);
        if (!world.Connections.Contains(newConnection))
        {
            world.Connections.Add(newConnection);
            newConnection.SendValidation(RequireHost(), worldId);
            if (newConnection.QueryConstructionMode() is ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
            {
                foreach (var replica in world.UserReplicas)
                    newConnection.OnLocalReference(replica, this);
            }
        }
        return true;
    }

    /// <summary>
    /// PopConnection: stops tracking the connection to <paramref name="rakNetGuid"/>, asking each replica that system created
    /// what to do (<see cref="Replica3.QueryActionOnPopConnection"/>), and returns it; null when there is none.
    /// </summary>
    public ReplicaConnection? PopConnection(RakNetGuid rakNetGuid, byte worldId = 0)
    {
        var world = RequireWorld(worldId);
        for (var index = 0; index < world.Connections.Count; index++)
        {
            if (world.Connections[index].RakNetGuid == rakNetGuid)
                return PopConnectionAt(index, worldId);
        }
        return null;
    }

    /// <summary>
    /// Reference: starts replicating <paramref name="replica3"/>. It is given the world's NetworkIDManager, and so a
    /// NetworkID, and this system as its creator unless it has one.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached, or the world has no NetworkIDManager.</exception>
    public void Reference(Replica3 replica3, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replica3);
        var world = RequireWorld(worldId);
        if (ReferenceInternal(replica3, worldId) < 0)
            return;
        foreach (var connection in world.Connections)
        {
            if (connection.QueryConstructionMode() is ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
                connection.OnLocalReference(replica3, this);
        }
    }

    /// <summary>Dereference: stops replicating <paramref name="replica3"/>, which stays as it is; disposing a replica does this.</summary>
    public void Dereference(Replica3 replica3, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replica3);
        var world = RequireWorld(worldId);
        world.UserReplicas.Remove(replica3);
        foreach (var connection in world.Connections)
            connection.OnDereference(replica3);
    }

    /// <summary>DereferenceList</summary>
    public void DereferenceList(IReadOnlyList<Replica3> replicaListIn, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replicaListIn);
        foreach (var replica in replicaListIn.ToArray())
            Dereference(replica, worldId);
    }

    /// <summary>GetReplicasCreatedByGuid: the referenced replicas whose <see cref="Replica3.CreatingSystemGuid"/> is <paramref name="rakNetGuid"/>.</summary>
    public void GetReplicasCreatedByGuid(RakNetGuid rakNetGuid, List<Replica3> replicaListOut, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replicaListOut);
        var world = RequireWorld(worldId);
        replicaListOut.Clear();
        foreach (var replica in world.UserReplicas)
        {
            if (replica.CreatingSystemGuid == rakNetGuid)
                replicaListOut.Add(replica);
        }
    }

    /// <summary>GetReplicasCreatedByMe</summary>
    public void GetReplicasCreatedByMe(List<Replica3> replicaListOut, byte worldId = 0) =>
        GetReplicasCreatedByGuid(RequireHost().RakNetGuid, replicaListOut, worldId);

    /// <summary>GetReferencedReplicaList: every replica referenced here or downloaded, in that order.</summary>
    public void GetReferencedReplicaList(List<Replica3> replicaListOut, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replicaListOut);
        replicaListOut.Clear();
        replicaListOut.AddRange(RequireWorld(worldId).UserReplicas);
    }

    /// <summary>GetReplicaCount</summary>
    public int GetReplicaCount(byte worldId = 0) => RequireWorld(worldId).UserReplicas.Count;

    /// <summary>GetReplicaAtIndex</summary>
    public Replica3 GetReplicaAtIndex(int index, byte worldId = 0) => RequireWorld(worldId).UserReplicas[index];

    /// <summary>GetConnectionCount</summary>
    public int GetConnectionCount(byte worldId = 0) => RequireWorld(worldId).Connections.Count;

    /// <summary>GetConnectionAtIndex</summary>
    public ReplicaConnection GetConnectionAtIndex(int index, byte worldId = 0) => RequireWorld(worldId).Connections[index];

    /// <summary>GetConnectionBySystemAddress</summary>
    public ReplicaConnection? GetConnectionBySystemAddress(SystemAddress systemAddress, byte worldId = 0) =>
        RequireWorld(worldId).Connections.Find(c => c.SystemAddress == systemAddress);

    /// <summary>GetConnectionByGUID</summary>
    public ReplicaConnection? GetConnectionByGuid(RakNetGuid rakNetGuid, byte worldId = 0) =>
        RequireWorld(worldId).Connections.Find(c => c.RakNetGuid == rakNetGuid);

    /// <summary>GetConnectionsThatHaveReplicaConstructed</summary>
    public void GetConnectionsThatHaveReplicaConstructed(Replica3 replica, List<ReplicaConnection> connectionsThatHaveConstructedThisReplica, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentNullException.ThrowIfNull(connectionsThatHaveConstructedThisReplica);
        connectionsThatHaveConstructedThisReplica.Clear();
        foreach (var connection in RequireWorld(worldId).Connections)
        {
            if (connection.HasReplicaConstructed(replica))
                connectionsThatHaveConstructedThisReplica.Add(connection);
        }
    }

    /// <summary>GetAllConnectionDownloadsCompleted</summary>
    public bool GetAllConnectionDownloadsCompleted(byte worldId = 0) => RequireWorld(worldId).Connections.TrueForAll(c => c.DownloadWasCompleted);

    /// <summary>AddWorld: a world with its own connections, replicas and NetworkIDManager. World 0 exists from the start.</summary>
    /// <exception cref="InvalidOperationException">The world exists.</exception>
    public void AddWorld(byte worldId)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(worldId, WorldSlots);
        if (_worldsArray[worldId] is not null)
            throw new InvalidOperationException($"World {worldId} is already in use.");
        var world = new World(worldId);
        _worldsArray[worldId] = world;
        _worldsList.Add(world);
    }

    /// <summary>RemoveWorld: forgets the world, its connections and replicas. Worlds are reordered, as RakNet removes it fast.</summary>
    public void RemoveWorld(byte worldId)
    {
        var world = RequireWorld(worldId);
        var index = _worldsList.IndexOf(world);
        _worldsList[index] = _worldsList[^1];
        _worldsList.RemoveAt(_worldsList.Count - 1);
        _worldsArray[worldId] = null;
    }

    /// <summary>GetWorldIdAtIndex</summary>
    public byte GetWorldIdAtIndex(int index) => _worldsList[index].WorldId;

    /// <summary>SetNetworkIDManager: the manager that names <paramref name="worldId"/>'s replicas.</summary>
    public void SetNetworkIdManager(NetworkIdManager? networkIdManager, byte worldId = 0) => RequireWorld(worldId).NetworkIdManager = networkIdManager;

    /// <summary>GetNetworkIDManager</summary>
    public NetworkIdManager? GetNetworkIdManager(byte worldId = 0) => RequireWorld(worldId).NetworkIdManager;

    /// <summary>
    /// BroadcastDestructionList: tells every connection but the one at <paramref name="exclusionAddress"/> to destroy those
    /// of <paramref name="replicaListSource"/> it has, in one message each. The replicas are unaffected here.
    /// </summary>
    public void BroadcastDestructionList(IReadOnlyList<Replica3> replicaListSource, SystemAddress exclusionAddress, byte worldId = 0)
    {
        ArgumentNullException.ThrowIfNull(replicaListSource);
        var world = RequireWorld(worldId);
        var replicaList = new List<Replica3>(replicaListSource.Count);
        foreach (var replica in replicaListSource)
        {
            if (!ReferenceEquals(replica, _currentlyDeallocatingReplica))
                replicaList.Add(replica);
        }
        if (replicaList.Count == 0)
            return;
        var host = RequireHost();
        foreach (var replica in replicaList)
        {
            if (!replica.DeletingSystemGuid.IsAssigned)
                replica.DeletingSystemGuid = host.RakNetGuid;
        }

        using var bsOut = new BitStream();
        foreach (var connection in world.Connections)
        {
            if (connection.SystemAddress == exclusionAddress)
                continue;
            bsOut.Reset();
            bsOut.WriteByte((byte)MessageId.ReplicaManagerConstruction);
            bsOut.WriteByte(worldId);
            // No constructions, then the destruction count, overwritten once known.
            bsOut.WriteUInt16(0);
            var countOffset = bsOut.BitsWritten;
            bsOut.WriteUInt16((ushort)replicaList.Count);
            ushort count = 0;
            foreach (var replica in replicaList)
            {
                if (!connection.HasReplicaConstructed(replica))
                    continue;
                count++;
                bsOut.WriteUInt64(replica.NetworkId);
                var offsetStart = bsOut.BitsWritten;
                bsOut.WriteUInt32((uint)offsetStart);
                bsOut.WriteRakNetGuid(replica.DeletingSystemGuid);
                replica.SerializeDestruction(bsOut, connection);
                bsOut.AlignToByte();
                var offsetEnd = bsOut.BitsWritten;
                bsOut.SetWriteOffset(offsetStart);
                bsOut.WriteUInt32((uint)offsetEnd);
                bsOut.SetWriteOffset(offsetEnd);
            }
            if (count == 0)
                continue;
            var current = bsOut.BitsWritten;
            bsOut.SetWriteOffset(countOffset);
            bsOut.WriteUInt16(count);
            bsOut.SetWriteOffset(current);
            host.Send(bsOut, connection.SystemAddress, _defaultSendParameters.Reliability, _defaultSendParameters.Priority,
                      _defaultSendParameters.OrderingChannel, new SendReceipt(_defaultSendParameters.SendReceipt));
        }
    }

    /// <summary>BroadcastDestruction: <see cref="BroadcastDestructionList"/> of one replica, in world 0.</summary>
    public void BroadcastDestruction(Replica3 replica, SystemAddress exclusionAddress) => BroadcastDestructionList([replica], exclusionAddress);

    /// <summary>
    /// Clear: forgets every connection, deallocating them with <see cref="AutoDestroyConnections"/>, and every replica,
    /// which loses its manager and NetworkID; with <paramref name="deleteWorlds"/> the worlds go too, world 0 included.
    /// </summary>
    public void Clear(bool deleteWorlds = false)
    {
        foreach (var world in _worldsList)
        {
            ClearWorld(world);
            if (deleteWorlds)
                _worldsArray[world.WorldId] = null;
        }
        if (deleteWorlds)
            _worldsList.Clear();
    }

    /// <summary>
    /// Update: constructs and destroys what the queries ask for on every validated connection, then, once
    /// <see cref="AutoSerializeInterval"/> has passed, serializes. Runs on every dispatch step; call it to send at once.
    /// </summary>
    public virtual void Update()
    {
        var host = RequireHost();
        var time = host.GetTime();
        // Index loops over the live lists, as RakNet's, since callbacks may change them.
        for (var w = 0; w < _worldsList.Count; w++)
        {
            var world = _worldsList[w];
            for (var c = 0; c < world.Connections.Count; c++)
            {
                if (world.Connections[c].IsValidated)
                    world.Connections[c].AutoConstructByQuery(this, world.WorldId);
            }
        }

        if (time - _lastAutoSerializeOccurrence < _autoSerializeInterval)
            return;

        for (var w = 0; w < _worldsList.Count; w++)
        {
            var world = _worldsList[w];
            for (var r = 0; r < world.UserReplicas.Count; r++)
            {
                var replica = world.UserReplicas[r];
                replica.ForceSendUntilNextUpdate = false;
                replica.OnUserReplicaPreSerializeTick();
            }

            // A SerializeParameters per world, reused for every connection and replica (ReplicaManager3.cpp:899-967).
            var sp = _updateParameters ??= new SerializeParameters();
            sp.CurrentTime = time;
            sp.MessageTimestamp = 0;
            sp.SetAllPro(_defaultSendParameters);
            for (var c = 0; c < world.Connections.Count; c++)
            {
                var connection = world.Connections[c];
                sp.BitsWrittenSoFar = 0;
                sp.DestinationConnection = connection;
                _replicasToSerialize.Clear();
                var queue = connection.QueryToSerializeReplicaList;
                if (connection.QuerySerializationList(_replicasToSerialize))
                {
                    // Each replica's lsr is this connection's, so the listed replicas can be found.
                    foreach (var lsr in queue)
                        lsr.Replica.Lsr = lsr;
                    for (var r = 0; r < _replicasToSerialize.Count; r++)
                    {
                        if (_replicasToSerialize[r].Lsr is not { } lsr)
                            continue;
                        sp.WhenLastSerialized = lsr.WhenLastSerialized;
                        if (connection.SendSerializeIfChanged(lsr, sp, host, world.WorldId, this, time) == SendSerializeResult.SentData)
                            lsr.WhenLastSerialized = time;
                    }
                }
                else
                {
                    var index = 0;
                    while (index < queue.Count)
                    {
                        var lsr = queue[index];
                        sp.DestinationConnection = connection;
                        sp.WhenLastSerialized = lsr.WhenLastSerialized;
                        var result = connection.SendSerializeIfChanged(lsr, sp, host, world.WorldId, this, time);
                        if (result == SendSerializeResult.SentData)
                        {
                            lsr.WhenLastSerialized = time;
                            index++;
                        }
                        else if (result != SendSerializeResult.NeverSerialize)
                        {
                            // NeverSerialize removed it from the list, so the next one is at this index.
                            index++;
                        }
                    }
                }
            }
        }
        _lastAutoSerializeOccurrence = time;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        _updateParameters?.Dispose();
        _constructionParameters?.Dispose();
        _deserializeParameters?.Dispose();
        _updateParameters = null;
        _constructionParameters = null;
        _deserializeParameters = null;
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached()
    {
        // OnDetach calls OnRakPeerShutdown (ReplicaManager3.cpp:651-654).
        OnShutdown();
        _host = null;
    }

    void IRakPlugin.OnShutdown() => OnShutdown();

    void IRakPlugin.OnUpdate()
    {
        if (_host is not null)
            Update();
    }

    // OnNewConnection (ReplicaManager3.cpp:617-626).
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        if (!AutoCreateConnections)
            return;
        if (AllocConnection(connection.Address, connection.RakNetGuid) is { } created)
            PushConnection(created);
    }

    // OnClosedConnection (ReplicaManager3.cpp:604-613).
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        if (!AutoDestroyConnections)
            return;
        if (PopConnection(connection.RakNetGuid) is { } popped)
            DeallocConnection(popped);
    }

    // OnReceive (ReplicaManager3.cpp:657-720).
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.Length < 2)
            return PluginResult.Continue;

        ulong timestamp = 0;
        byte packetIdentifier;
        int packetDataOffset;
        byte incomingWorldId;
        if (data[0] == (byte)MessageId.Timestamp)
        {
            if (data.Length <= 1 + sizeof(ulong))
                return PluginResult.Consume;
            packetIdentifier = data[1 + sizeof(ulong)];
            timestamp = BinaryPrimitives.ReadUInt64BigEndian(data[1..]);
            // RakNet reads the world ID one byte past the end of a message that has none.
            if (data.Length < 3 + sizeof(ulong))
                return PluginResult.Continue;
            incomingWorldId = data[2 + sizeof(ulong)];
            packetDataOffset = 3 + sizeof(ulong);
        }
        else
        {
            packetIdentifier = data[0];
            incomingWorldId = data[1];
            packetDataOffset = 2;
        }

        if (incomingWorldId >= WorldSlots || _worldsArray[incomingWorldId] is null)
            return PluginResult.Continue;

        switch ((MessageId)packetIdentifier)
        {
            case MessageId.ReplicaManagerConstruction:
                return OnConstruction(e, data, packetDataOffset, incomingWorldId);
            case MessageId.ReplicaManagerSerialize:
                return OnSerialize(e, data, timestamp, packetDataOffset, incomingWorldId);
            case MessageId.ReplicaManagerDownloadStarted when !e.IsLocal:
                return OnDownloadStarted(e, data, packetDataOffset, incomingWorldId);
            case MessageId.ReplicaManagerDownloadComplete when !e.IsLocal:
                return OnDownloadComplete(e, data, packetDataOffset, incomingWorldId);
            case MessageId.ReplicaManagerScopeChange:
            {
                if (GetConnectionByGuid(e.RakNetGuid, incomingWorldId) is { IsValidated: false } connection)
                {
                    // The connection is confirmed in both directions; answer with validation in turn.
                    connection.IsValidated = true;
                    connection.SendValidation(RequireHost(), incomingWorldId);
                }
                break;
            }
        }
        return PluginResult.Continue;
    }

    // The Replica3 destructor's Dereference, which a disposed replica must survive after its world is gone.
    internal void DereferenceDisposed(Replica3 replica3)
    {
        if (_worldsArray[0] is not null)
            Dereference(replica3);
    }

    internal IPluginHost RequireHost() => _host ?? throw new InvalidOperationException("The ReplicaManager3 is not attached to a peer.");

    // OnRakPeerShutdown (ReplicaManager3.cpp:630-648).
    private void OnShutdown()
    {
        if (AutoDestroyConnections)
        {
            foreach (var world in _worldsList.ToArray())
            {
                while (world.Connections.Count > 0)
                    DeallocConnection(PopConnectionAt(world.Connections.Count - 1, world.WorldId));
            }
        }
        Clear(false);
    }

    // RM3World::Clear (ReplicaManager3.cpp:535-555).
    private void ClearWorld(World world)
    {
        foreach (var connection in world.Connections)
        {
            if (AutoDestroyConnections)
                DeallocConnection(connection);
            else
                connection.ClearDownloadGroup();
        }
        foreach (var replica in world.UserReplicas)
        {
            replica.ReplicaManager = null;
            replica.Manager = null;
        }
        world.Connections.Clear();
        world.UserReplicas.Clear();
    }

    // DeallocReplicaNoBroadcastDestruction: a destruction the replica broadcasts from DeallocReplica is not sent.
    private void DeallocReplicaNoBroadcastDestruction(ReplicaConnection connection, Replica3 replica3)
    {
        _currentlyDeallocatingReplica = replica3;
        try
        {
            replica3.DeallocReplica(connection);
        }
        finally
        {
            _currentlyDeallocatingReplica = null;
        }
    }

    // PopConnection by index (ReplicaManager3.cpp:179-251).
    private ReplicaConnection PopConnectionAt(int index, byte worldId)
    {
        var world = RequireWorld(worldId);
        var connection = world.Connections[index];
        connection.ClearDownloadGroup();

        // RakNet asks world 0 for the replicas the system created, by creatingSystemGUID, so a replica that changed owner
        // is not asked.
        var replicaList = new List<Replica3>();
        GetReplicasCreatedByGuid(connection.RakNetGuid, replicaList);
        var destructionList = new List<ulong>();
        var broadcastList = new List<Replica3>();
        foreach (var replica in replicaList)
        {
            var action = replica.QueryActionOnPopConnection(connection);
            replica.OnPoppedConnection(connection);
            if (action == ReplicaActionOnPopConnection.DeleteReplica)
            {
                if (replica.Manager is not null)
                    destructionList.Add(replica.NetworkId);
            }
            else if (action == ReplicaActionOnPopConnection.DeleteReplicaAndBroadcastDestruction)
            {
                if (replica.Manager is not null)
                    destructionList.Add(replica.NetworkId);
                broadcastList.Add(replica);
            }
            else if (action == ReplicaActionOnPopConnection.DoNothing)
            {
                // Whatever this system was last sent is forgotten, so the next owner sends everything.
                foreach (var lsr in connection.QueryToSerializeReplicaList)
                {
                    lsr.WhenLastSerialized = 0;
                    lsr.Channels?.ResetStreams();
                }
            }
        }

        BroadcastDestructionList(broadcastList, connection.SystemAddress);
        foreach (var networkId in destructionList)
        {
            // Looked up again in case DeallocReplica destroyed a later replica of the list.
            if (world.NetworkIdManager?.GetObject<Replica3>(networkId) is { } replicaToDestroy)
            {
                replicaToDestroy.PreDestruction(connection);
                replicaToDestroy.DeallocReplica(connection);
            }
        }

        world.Connections.Remove(connection);
        return connection;
    }

    // ReferenceInternal (ReplicaManager3.cpp:302-327): the index in the world's list, or -1 when already referenced.
    private int ReferenceInternal(Replica3 replica3, byte worldId)
    {
        var world = RequireWorld(worldId);
        if (world.UserReplicas.Contains(replica3))
            return -1;
        var networkIdManager = world.NetworkIdManager ?? throw new InvalidOperationException($"World {worldId} has no NetworkIdManager; call SetNetworkIdManager first.");
        var host = RequireHost();
        replica3.Manager = networkIdManager;
        if (!replica3.CreatingSystemGuid.IsAssigned)
            replica3.CreatingSystemGuid = host.RakNetGuid;
        replica3.ReplicaManager = this;
        if (replica3.ReferenceIndex == uint.MaxValue)
            replica3.ReferenceIndex = _nextReferenceIndex++;
        world.UserReplicas.Add(replica3);
        return world.UserReplicas.Count - 1;
    }

    // OnConstruction (ReplicaManager3.cpp:659-873).
    private PluginResult OnConstruction(PeerEvent e, ReadOnlySpan<byte> data, int packetDataOffset, byte worldId)
    {
        var world = _worldsArray[worldId]!;
        var connection = GetConnectionByGuid(e.RakNetGuid, worldId);
        if (connection is null)
            return PluginResult.Continue;
        if (connection.GroupConstructionAndSerialize)
        {
            connection.DownloadGroup.Add(e.Detach());
            return PluginResult.Consume;
        }
        var networkIdManager = world.NetworkIdManager ?? throw new InvalidOperationException($"World {worldId} has no NetworkIdManager; call SetNetworkIdManager first.");

        var bsIn = new BitStreamReader(data);
        bsIn.IgnoreBits(packetDataOffset * 8);
        if (!bsIn.TryReadUInt16(out var constructionObjectListSize))
            return PluginResult.Continue;

        var actuallyCreateObjectList = new List<bool>(constructionObjectListSize);
        var constructionTickStack = new List<Replica3?>(constructionObjectListSize);
        for (var index = 0; index < constructionObjectListSize; index++)
        {
            if (!bsIn.TryReadUInt32(out var streamEnd) || !bsIn.TryReadUInt64(out var networkId))
                return PluginResult.Continue;
            var existingReplica = networkIdManager.GetObject<Replica3>(networkId);
            if (!bsIn.TryReadBit(out var actuallyCreateObject))
                return PluginResult.Continue;
            actuallyCreateObjectList.Add(actuallyCreateObject);
            bsIn.AlignToByte();

            if (actuallyCreateObject)
            {
                if (!bsIn.TryReadRakNetGuid(out var creatingSystemGuid) || !bsIn.TryReadUInt32(out var writeAllocationIdEnd))
                    return PluginResult.Continue;
                if (existingReplica is not null)
                {
                    // The NetworkID is in use: this system has the replica already.
                    existingReplica.ReplicaManager = this;
                    connection.OnDownloadExisting(existingReplica, this);
                    constructionTickStack.Add(null);
                    if (!TrySeek(ref bsIn, streamEnd))
                        return PluginResult.Continue;
                    continue;
                }

                bsIn.AlignToByte();
                var replica = connection.AllocReplica(ref bsIn, this);
                if (replica is null)
                {
                    constructionTickStack.Add(null);
                    if (!TrySeek(ref bsIn, streamEnd))
                        return PluginResult.Continue;
                    continue;
                }

                // Past the allocation ID, however much of it AllocReplica read.
                if (!TrySeek(ref bsIn, writeAllocationIdEnd))
                    return PluginResult.Continue;
                replica.Manager = networkIdManager;
                replica.NetworkId = networkId;
                replica.ReplicaManager = this;
                replica.CreatingSystemGuid = creatingSystemGuid;

                if (!replica.QueryRemoteConstruction(connection) || !replica.DeserializeConstruction(ref bsIn, connection))
                {
                    DeallocReplicaNoBroadcastDestruction(connection, replica);
                    constructionTickStack.Add(null);
                    if (!TrySeek(ref bsIn, streamEnd))
                        return PluginResult.Continue;
                    continue;
                }

                constructionTickStack.Add(replica);
                ReferenceInternal(replica, worldId);
            }
            else if (existingReplica is not null)
            {
                existingReplica.DeserializeConstructionExisting(ref bsIn, connection);
                constructionTickStack.Add(existingReplica);
            }
            else
            {
                constructionTickStack.Add(null);
            }

            if (!TrySeek(ref bsIn, streamEnd))
                return PluginResult.Continue;
            bsIn.AlignToByte();
        }

        for (var index = 0; index < constructionObjectListSize; index++)
        {
            if (!bsIn.TryReadBit(out var pdcWritten))
                return PluginResult.Continue;
            var replica = constructionTickStack[index];
            if (pdcWritten)
            {
                bsIn.AlignToByte();
                if (!bsIn.TryReadUInt32(out var streamEnd) || !bsIn.TryReadUInt64(out _))
                    return PluginResult.Continue;
                if (replica is not null)
                {
                    bsIn.AlignToByte();
                    if (actuallyCreateObjectList[index])
                        replica.PostDeserializeConstruction(ref bsIn, connection);
                    else
                        replica.PostDeserializeConstructionExisting(ref bsIn, connection);
                }
                if (!TrySeek(ref bsIn, streamEnd))
                    return PluginResult.Continue;
            }
            else if (replica is not null)
            {
                var empty = default(BitStreamReader);
                if (actuallyCreateObjectList[index])
                    replica.PostDeserializeConstruction(ref empty, connection);
                else
                    replica.PostDeserializeConstructionExisting(ref empty, connection);
            }
        }

        for (var index = 0; index < constructionObjectListSize; index++)
        {
            if (constructionTickStack[index] is not { } replica || !actuallyCreateObjectList[index])
                continue;
            // The sender has it; every other connection is queried for it.
            connection.OnDownloadFromThisSystem(replica, this);
            for (var c = 0; c < world.Connections.Count; c++)
            {
                if (world.Connections[c] != connection)
                    world.Connections[c].OnDownloadFromOtherSystem(replica, this);
            }
        }

        // RakNet reads the destruction count without aligning after the post-construction flags, although the sender
        // aligns before writing it (ReplicaManager3.cpp:2289, 818); so after a construction with no post-construction data
        // the count is read from inside the padding, and destructions in the same message are lost.
        if (!bsIn.TryReadUInt16(out var destructionObjectListSize))
            return PluginResult.Continue;
        for (var index = 0; index < destructionObjectListSize; index++)
        {
            if (!bsIn.TryReadUInt64(out var networkId) || !bsIn.TryReadUInt32(out var streamEnd))
                return PluginResult.Continue;
            var replica = networkIdManager.GetObject<Replica3>(networkId);
            if (replica is null)
            {
                if (!TrySeek(ref bsIn, streamEnd))
                    return PluginResult.Continue;
                continue;
            }
            if (!bsIn.TryReadRakNetGuid(out var deletingSystemGuid))
                return PluginResult.Continue;
            replica.DeletingSystemGuid = deletingSystemGuid;
            if (replica.DeserializeDestruction(ref bsIn, connection))
            {
                // Unless DeserializeDestruction destroyed it already.
                if (networkIdManager.GetObject<Replica3>(networkId) is not null)
                {
                    replica.PreDestruction(connection);
                    if (replica.QueryRelayDestruction(connection))
                        BroadcastDestruction(replica, connection.SystemAddress);
                    Dereference(replica);
                    DeallocReplicaNoBroadcastDestruction(connection, replica);
                }
            }
            else
            {
                replica.PreDestruction(connection);
                connection.OnDereference(replica);
            }
            bsIn.AlignToByte();
        }
        return PluginResult.Continue;
    }

    // OnSerialize (ReplicaManager3.cpp:877-922).
    private PluginResult OnSerialize(PeerEvent e, ReadOnlySpan<byte> data, ulong timestamp, int packetDataOffset, byte worldId)
    {
        var connection = GetConnectionByGuid(e.RakNetGuid, worldId);
        if (connection is null)
            return PluginResult.Continue;
        if (connection.GroupConstructionAndSerialize)
        {
            connection.DownloadGroup.Add(e.Detach());
            return PluginResult.Consume;
        }
        var world = _worldsArray[worldId]!;
        var networkIdManager = world.NetworkIdManager ?? throw new InvalidOperationException($"World {worldId} has no NetworkIdManager; call SetNetworkIdManager first.");
        var bsIn = new BitStreamReader(data);
        bsIn.IgnoreBits(packetDataOffset * 8);
        if (!bsIn.TryReadUInt64(out var networkId))
            return PluginResult.Continue;
        if (networkIdManager.GetObject<Replica3>(networkId) is not { } replica)
            return PluginResult.Continue;

        var ds = _deserializeParameters ??= new DeserializeParameters();
        ds.Reset();
        ds.Timestamp = timestamp;
        ds.SourceConnection = connection;
        for (var z = 0; z < SerializeParameters.ChannelCount; z++)
        {
            if (!bsIn.TryReadBit(out var writtenTo))
                break;
            ds.SetWrittenTo(z, writtenTo);
            if (!writtenTo)
                continue;
            if (!bsIn.TryReadCompressed(out uint bitsUsed))
                break;
            bsIn.AlignToByte();
            if (bitsUsed <= int.MaxValue)
                bsIn.TryReadBits(ds.Stream(z), (int)bitsUsed);
        }
        replica.Deserialize(ds);
        return PluginResult.Continue;
    }

    // OnDownloadStarted (ReplicaManager3.cpp:925-949).
    private PluginResult OnDownloadStarted(PeerEvent e, ReadOnlySpan<byte> data, int packetDataOffset, byte worldId)
    {
        var connection = GetConnectionByGuid(e.RakNetGuid, worldId);
        if (connection is null)
            return PluginResult.Continue;
        // The second time this message is processed, after the download completes, the group is open and it passes.
        if (connection.QueryGroupDownloadMessages() && !connection.GroupConstructionAndSerialize)
        {
            connection.GroupConstructionAndSerialize = true;
            connection.DownloadGroup.Add(e.Detach());
            return PluginResult.Consume;
        }
        connection.GroupConstructionAndSerialize = false;
        var bsIn = new BitStreamReader(data);
        bsIn.IgnoreBits(packetDataOffset * 8);
        connection.DeserializeOnDownloadStarted(ref bsIn);
        return PluginResult.Continue;
    }

    // OnDownloadComplete (ReplicaManager3.cpp:953-981): the held messages, then this one, go back on the event stream.
    private PluginResult OnDownloadComplete(PeerEvent e, ReadOnlySpan<byte> data, int packetDataOffset, byte worldId)
    {
        var connection = GetConnectionByGuid(e.RakNetGuid, worldId);
        if (connection is null)
            return PluginResult.Continue;
        if (connection.GroupConstructionAndSerialize && connection.DownloadGroup.Count > 0)
        {
            var host = RequireHost();
            foreach (var held in connection.DownloadGroup)
                host.Inject(held);
            connection.DownloadGroup.Clear();
            host.Inject(e.Detach());
            return PluginResult.Consume;
        }
        var bsIn = new BitStreamReader(data);
        bsIn.IgnoreBits(packetDataOffset * 8);
        connection.MarkDownloadComplete();
        connection.DeserializeOnDownloadComplete(ref bsIn);
        return PluginResult.Continue;
    }

    // BitStream::SetReadOffset to an offset the message gives; false when it lies past the end.
    private static bool TrySeek(ref BitStreamReader reader, uint bitOffset)
    {
        if (bitOffset > (uint)(reader.BitPosition + reader.BitsRemaining))
            return false;
        reader.BitPosition = (int)bitOffset;
        return true;
    }

    private World RequireWorld(byte worldId) =>
        (worldId < WorldSlots ? _worldsArray[worldId] : null) ?? throw new InvalidOperationException($"World {worldId} is not in use.");

    private sealed class World(byte worldId)
    {
        public byte WorldId { get; } = worldId;

        public List<ReplicaConnection> Connections { get; } = [];

        public List<Replica3> UserReplicas { get; } = [];

        public NetworkIdManager? NetworkIdManager { get; set; }
    }
}
