using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// Replica3: the base of a game object <see cref="ReplicaManager3"/> replicates. Pass it to
/// <see cref="ReplicaManager3.Reference"/>; call <see cref="BroadcastDestruction"/> before destroying it, then
/// <see cref="NetworkIdObject.Dispose()"/>, which dereferences it as the C++ destructor does.
/// </summary>
/// <remarks>
/// <para>
/// Ported from Replica3 in ReplicaManager3.cpp. The QueryConstructionFor*, QueryRemoteConstructionFor*,
/// QuerySerializationFor* and QueryActionOnPopConnectionFor* members are RakNet's defaults for client/server and peer to
/// peer topologies; return one of them from the corresponding query.
/// </para>
/// <para>
/// Reads are given a <see cref="BitStreamReader"/> by reference over the whole received message, positioned where this
/// replica's data starts; where RakNet relies on the read position afterwards (destruction data), read exactly what was
/// written. A member Replica3 that forwards to its owner takes the place of Replica3Composite.
/// </para>
/// </remarks>
public abstract class Replica3 : NetworkIdObject
{
    private SerializationChannels? _lastSentSerialization;

    /// <summary>
    /// creatingSystemGUID: the system that first referenced this replica, set by <see cref="ReplicaManager3.Reference"/>
    /// or from the construction that created it here, and sent with its construction.
    /// </summary>
    public RakNetGuid CreatingSystemGuid { get; set; } = RakNetGuid.Unassigned;

    /// <summary>deletingSystemGUID: the system that sent the destruction of this replica.</summary>
    public RakNetGuid DeletingSystemGuid { get; set; } = RakNetGuid.Unassigned;

    /// <summary>replicaManager: the manager this replica was referenced by or downloaded through.</summary>
    public ReplicaManager3? ReplicaManager { get; internal set; }

    /// <summary>WasReferenced</summary>
    public bool WasReferenced => ReplicaManager is not null;

    internal SerializationChannels LastSentSerialization => _lastSentSerialization ??= new SerializationChannels();

    internal bool HasLastSentSerialization => _lastSentSerialization is not null;

    internal bool ForceSendUntilNextUpdate { get; set; }

    internal LastSerializationResult? Lsr { get; set; }

    // referenceIndex: the order key of every connection's constructed list, set by the first Reference.
    internal uint ReferenceIndex { get; set; } = uint.MaxValue;

    /// <summary>
    /// WriteAllocationID: writes what identifies this replica's type, which the remote system's
    /// <see cref="ReplicaConnection.AllocReplica"/> reads.
    /// </summary>
    public abstract void WriteAllocationId(ReplicaConnection destinationConnection, BitStream allocationIdBitstream);

    /// <summary>QueryConstruction: whether this replica, which <paramref name="destinationConnection"/> lacks, should be sent to it now.</summary>
    public abstract ReplicaConstructionState QueryConstruction(ReplicaConnection destinationConnection, ReplicaManager3 replicaManager3);

    /// <summary>QueryDestruction: whether this replica, which <paramref name="destinationConnection"/> has, should be destroyed there.</summary>
    public virtual ReplicaDestructionState QueryDestruction(ReplicaConnection destinationConnection, ReplicaManager3 replicaManager3) =>
        ReplicaDestructionState.DoNotQueryDestruction;

    /// <summary>QueryRemoteConstruction: false rejects a construction <paramref name="sourceConnection"/> sent before it is deserialized.</summary>
    public abstract bool QueryRemoteConstruction(ReplicaConnection sourceConnection);

    /// <summary>QueryRelayDestruction: true sends a destruction received from <paramref name="sourceConnection"/> on to the other systems.</summary>
    public virtual bool QueryRelayDestruction(ReplicaConnection sourceConnection) => true;

    /// <summary>SerializeConstruction: data sent only with the construction.</summary>
    public abstract void SerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection);

    /// <summary>DeserializeConstruction: reads what <see cref="SerializeConstruction"/> wrote; false rejects the replica.</summary>
    public abstract bool DeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection);

    /// <summary>SerializeConstructionExisting: as <see cref="SerializeConstruction"/>, for <see cref="ReplicaConstructionState.AlreadyExistsRemotely"/>.</summary>
    public virtual void SerializeConstructionExisting(BitStream constructionBitstream, ReplicaConnection destinationConnection)
    {
    }

    /// <summary>DeserializeConstructionExisting: reads what <see cref="SerializeConstructionExisting"/> wrote.</summary>
    public virtual void DeserializeConstructionExisting(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
    }

    /// <summary>SerializeDestruction: data sent with the destruction.</summary>
    public abstract void SerializeDestruction(BitStream destructionBitstream, ReplicaConnection destinationConnection);

    /// <summary>
    /// DeserializeDestruction: reads exactly what <see cref="SerializeDestruction"/> wrote. True destroys the replica:
    /// <see cref="PreDestruction"/>, the relay, dereferencing and <see cref="DeallocReplica"/>.
    /// </summary>
    public abstract bool DeserializeDestruction(ref BitStreamReader destructionBitstream, ReplicaConnection sourceConnection);

    /// <summary>QueryActionOnPopConnection: what to do with this replica when the system that created it goes.</summary>
    public abstract ReplicaActionOnPopConnection QueryActionOnPopConnection(ReplicaConnection droppedConnection);

    /// <summary>OnPoppedConnection: called for each replica the popped system created.</summary>
    public virtual void OnPoppedConnection(ReplicaConnection droppedConnection)
    {
    }

    /// <summary>
    /// DeallocReplica: the manager is done with this replica, because a destruction or the end of its creator's connection
    /// destroyed it, or its construction was rejected. RakNet's implementations delete the object; this one disposes it.
    /// </summary>
    public virtual void DeallocReplica(ReplicaConnection? sourceConnection) => Dispose();

    /// <summary>QuerySerialization: whether to call <see cref="Serialize"/> for <paramref name="destinationConnection"/>.</summary>
    public abstract ReplicaQuerySerializationResult QuerySerialization(ReplicaConnection destinationConnection);

    /// <summary>OnUserReplicaPreSerializeTick: once per serialization tick, before any <see cref="Serialize"/>.</summary>
    public virtual void OnUserReplicaPreSerializeTick()
    {
    }

    /// <summary>Serialize: writes this replica's state to the parameters' channels and says how to send it.</summary>
    public abstract ReplicaSerializationResult Serialize(SerializeParameters serializeParameters);

    /// <summary>OnSerializeTransmission: each serialization message, with the bits sent per channel.</summary>
    public virtual void OnSerializeTransmission(BitStream bitStream, ReplicaConnection destinationConnection, ReadOnlySpan<int> bitsPerChannel, ulong currentTime)
    {
    }

    /// <summary>Deserialize: reads what <see cref="Serialize"/> wrote, once per message received.</summary>
    public abstract void Deserialize(DeserializeParameters deserializeParameters);

    /// <summary>PostSerializeConstruction: data for <see cref="PostDeserializeConstruction"/>, after every construction of the tick.</summary>
    public virtual void PostSerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection)
    {
    }

    /// <summary>PostDeserializeConstruction: once every replica of the construction message exists, so references between them resolve.</summary>
    public virtual void PostDeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
    }

    /// <summary>PostSerializeConstructionExisting</summary>
    public virtual void PostSerializeConstructionExisting(BitStream constructionBitstream, ReplicaConnection destinationConnection)
    {
    }

    /// <summary>PostDeserializeConstructionExisting</summary>
    public virtual void PostDeserializeConstructionExisting(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
    }

    /// <summary>PreDestruction: the replica is about to be destroyed by <paramref name="sourceConnection"/>.</summary>
    public virtual void PreDestruction(ReplicaConnection sourceConnection)
    {
    }

    /// <summary>
    /// QueryConstruction_ClientConstruction: client and server may both create the replica. Its creator sends it, and the
    /// server sends it to everyone.
    /// </summary>
    public virtual ReplicaConstructionState QueryConstructionForClientConstruction(ReplicaConnection destinationConnection, bool isThisTheServer)
    {
        if (CreatingSystemGuid == MyGuid())
            return ReplicaConstructionState.SendConstruction;
        // Send back to the owner client too, because it could not assign the network ID (ReplicaManager3.cpp:1424-1427).
        return isThisTheServer ? ReplicaConstructionState.SendConstruction : ReplicaConstructionState.NeverConstruct;
    }

    /// <summary>QueryRemoteConstruction_ClientConstruction: always true.</summary>
    public virtual bool QueryRemoteConstructionForClientConstruction(ReplicaConnection sourceConnection, bool isThisTheServer) => true;

    /// <summary>QueryConstruction_ServerConstruction: only the server sends the replica.</summary>
    public virtual ReplicaConstructionState QueryConstructionForServerConstruction(ReplicaConnection destinationConnection, bool isThisTheServer) =>
        isThisTheServer ? ReplicaConstructionState.SendConstruction : ReplicaConstructionState.NeverConstruct;

    /// <summary>QueryRemoteConstruction_ServerConstruction: the server rejects constructions, clients accept them.</summary>
    public virtual bool QueryRemoteConstructionForServerConstruction(ReplicaConnection sourceConnection, bool isThisTheServer) => !isThisTheServer;

    /// <summary>QueryConstruction_PeerToPeer (ReplicaManager3.cpp:1464-1496).</summary>
    public virtual ReplicaConstructionState QueryConstructionForPeerToPeer(ReplicaConnection destinationConnection, ReplicaP2PMode p2pMode = ReplicaP2PMode.SingleOwner) => p2pMode switch
    {
        // Only the creator sends; the others neither send nor serialize it.
        ReplicaP2PMode.SingleOwner => CreatingSystemGuid == MyGuid() ? ReplicaConstructionState.SendConstruction : ReplicaConstructionState.NeverConstruct,
        ReplicaP2PMode.MultiOwnerCurrentlyAuthoritative => ReplicaConstructionState.SendConstruction,
        ReplicaP2PMode.StaticObjectCurrentlyAuthoritative => ReplicaConstructionState.AlreadyExistsRemotely,
        ReplicaP2PMode.StaticObjectNotCurrentlyAuthoritative => ReplicaConstructionState.AlreadyExistsRemotelyDoNotConstruct,
        _ => ReplicaConstructionState.AlreadyExistsRemotely,
    };

    /// <summary>QueryRemoteConstruction_PeerToPeer: always true.</summary>
    public virtual bool QueryRemoteConstructionForPeerToPeer(ReplicaConnection sourceConnection) => true;

    /// <summary>
    /// QuerySerialization_ClientSerializable: the creator sends to everyone, and the server relays to every system but the
    /// creator.
    /// </summary>
    public virtual ReplicaQuerySerializationResult QuerySerializationForClientSerializable(ReplicaConnection destinationConnection, bool isThisTheServer)
    {
        ArgumentNullException.ThrowIfNull(destinationConnection);
        if (CreatingSystemGuid == MyGuid())
            return ReplicaQuerySerializationResult.CallSerialize;
        if (isThisTheServer && destinationConnection.RakNetGuid != CreatingSystemGuid)
            return ReplicaQuerySerializationResult.CallSerialize;
        return ReplicaQuerySerializationResult.NeverCallSerialize;
    }

    /// <summary>QuerySerialization_ServerSerializable: only the server sends.</summary>
    public virtual ReplicaQuerySerializationResult QuerySerializationForServerSerializable(ReplicaConnection destinationConnection, bool isThisTheServer) =>
        isThisTheServer ? ReplicaQuerySerializationResult.CallSerialize : ReplicaQuerySerializationResult.NeverCallSerialize;

    /// <summary>QuerySerialization_PeerToPeer (ReplicaManager3.cpp:1536-1566).</summary>
    public virtual ReplicaQuerySerializationResult QuerySerializationForPeerToPeer(ReplicaConnection destinationConnection, ReplicaP2PMode p2pMode = ReplicaP2PMode.SingleOwner) => p2pMode switch
    {
        ReplicaP2PMode.SingleOwner => CreatingSystemGuid == MyGuid() ? ReplicaQuerySerializationResult.CallSerialize : ReplicaQuerySerializationResult.NeverCallSerialize,
        ReplicaP2PMode.MultiOwnerCurrentlyAuthoritative or ReplicaP2PMode.StaticObjectCurrentlyAuthoritative => ReplicaQuerySerializationResult.CallSerialize,
        _ => ReplicaQuerySerializationResult.DoNotCallSerialize,
    };

    /// <summary>QueryActionOnPopConnection_Client: a client deletes the server's replicas when it loses the server.</summary>
    public virtual ReplicaActionOnPopConnection QueryActionOnPopConnectionForClient(ReplicaConnection droppedConnection) => ReplicaActionOnPopConnection.DeleteReplica;

    /// <summary>QueryActionOnPopConnection_Server: a server deletes a lost client's replicas and tells the other clients.</summary>
    public virtual ReplicaActionOnPopConnection QueryActionOnPopConnectionForServer(ReplicaConnection droppedConnection) =>
        ReplicaActionOnPopConnection.DeleteReplicaAndBroadcastDestruction;

    /// <summary>QueryActionOnPopConnection_PeerToPeer: a peer deletes a lost peer's replicas.</summary>
    public virtual ReplicaActionOnPopConnection QueryActionOnPopConnectionForPeerToPeer(ReplicaConnection droppedConnection) => ReplicaActionOnPopConnection.DeleteReplica;

    /// <summary>BroadcastDestruction: tells every system that has this replica to destroy it. Call it before destroying it.</summary>
    /// <exception cref="InvalidOperationException">The replica was never referenced.</exception>
    public virtual void BroadcastDestruction()
    {
        var manager = ReplicaManager ?? throw new InvalidOperationException("The replica was never referenced by a ReplicaManager3.");
        manager.BroadcastDestruction(this, SystemAddress.Unassigned);
    }

    /// <summary>The C++ destructor: dereferences the replica from its manager, then stops its NetworkIDManager tracking it.</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReplicaManager?.DereferenceDisposed(this);
            _lastSentSerialization?.Dispose();
            _lastSentSerialization = null;
        }
        base.Dispose(disposing);
    }

    // replicaManager->GetRakPeerInterface()->GetGuidFromSystemAddress(UNASSIGNED_SYSTEM_ADDRESS)
    private RakNetGuid MyGuid() =>
        (ReplicaManager ?? throw new InvalidOperationException("The replica was never referenced by a ReplicaManager3.")).RequireHost().RakNetGuid;
}
