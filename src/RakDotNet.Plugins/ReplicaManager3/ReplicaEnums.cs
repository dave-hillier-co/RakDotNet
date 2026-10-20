namespace RakDotNet.Plugins;

/// <summary>
/// Connection_RM3::ConstructionMode: how <see cref="ReplicaManager3"/> finds which replicas should exist on a remote
/// system.
/// </summary>
public enum ReplicaConstructionMode
{
    /// <summary>
    /// QUERY_REPLICA_FOR_CONSTRUCTION: <see cref="Replica3.QueryConstruction"/> every tick for each replica the remote
    /// system lacks; <see cref="Replica3.QueryDestruction"/> is never called.
    /// </summary>
    QueryReplicaForConstruction,

    /// <summary>
    /// QUERY_REPLICA_FOR_CONSTRUCTION_AND_DESTRUCTION, the default: as <see cref="QueryReplicaForConstruction"/>, and
    /// <see cref="Replica3.QueryDestruction"/> every tick for each replica the remote system has.
    /// </summary>
    QueryReplicaForConstructionAndDestruction,

    /// <summary>
    /// QUERY_CONNECTION_FOR_REPLICA_LIST: <see cref="ReplicaConnection.QueryReplicaList"/> names the replicas to create and
    /// destroy, which suits large worlds (see <see cref="GridSectorizer{T}"/>).
    /// </summary>
    QueryConnectionForReplicaList,
}

/// <summary>RM3ConstructionState: what <see cref="Replica3.QueryConstruction"/> asks for on a remote system.</summary>
public enum ReplicaConstructionState
{
    /// <summary>RM3CS_SEND_CONSTRUCTION: the replica should exist there; send its construction.</summary>
    SendConstruction,

    /// <summary>
    /// RM3CS_ALREADY_EXISTS_REMOTELY: the remote system has it already (a level object, say); treat it as constructed,
    /// send <see cref="Replica3.SerializeConstructionExisting"/> and serialize it.
    /// </summary>
    AlreadyExistsRemotely,

    /// <summary>RM3CS_ALREADY_EXISTS_REMOTELY_DO_NOT_CONSTRUCT: as <see cref="AlreadyExistsRemotely"/>, sending nothing.</summary>
    AlreadyExistsRemotelyDoNotConstruct,

    /// <summary>RM3CS_NEVER_CONSTRUCT: never send or serialize it to that system.</summary>
    NeverConstruct,

    /// <summary>RM3CS_NO_ACTION: ask again next tick.</summary>
    NoAction,
}

/// <summary>RM3DestructionState: what <see cref="Replica3.QueryDestruction"/> asks for on a remote system.</summary>
public enum ReplicaDestructionState
{
    /// <summary>RM3DS_SEND_DESTRUCTION: the replica should no longer exist there; send its destruction.</summary>
    SendDestruction,

    /// <summary>RM3DS_DO_NOT_QUERY_DESTRUCTION: never ask again for that system.</summary>
    DoNotQueryDestruction,

    /// <summary>RM3DS_NO_ACTION: ask again next tick.</summary>
    NoAction,
}

/// <summary>RM3SerializationResult: what <see cref="Replica3.Serialize"/> wrote, and how to compare and send it.</summary>
public enum ReplicaSerializationResult
{
    /// <summary>
    /// RM3SR_BROADCAST_IDENTICALLY: the same bytes for every system; channels whose bytes differ from the last ones sent
    /// to anyone are sent.
    /// </summary>
    BroadcastIdentically,

    /// <summary>RM3SR_BROADCAST_IDENTICALLY_FORCE_SERIALIZATION: as <see cref="BroadcastIdentically"/>, sending every written channel.</summary>
    BroadcastIdenticallyForceSerialization,

    /// <summary>RM3SR_SERIALIZED_UNIQUELY: bytes per system; channels whose bytes differ from the last ones sent to this system are sent.</summary>
    SerializedUniquely,

    /// <summary>RM3SR_SERIALIZED_ALWAYS: send every channel without comparing.</summary>
    SerializedAlways,

    /// <summary>RM3SR_SERIALIZED_ALWAYS_IDENTICALLY, deprecated in RakNet in favour of <see cref="BroadcastIdenticallyForceSerialization"/>.</summary>
    SerializedAlwaysIdentically,

    /// <summary>RM3SR_DO_NOT_SERIALIZE: send nothing this tick.</summary>
    DoNotSerialize,

    /// <summary>RM3SR_NEVER_SERIALIZE_FOR_THIS_CONNECTION: never serialize this replica to this system again.</summary>
    NeverSerializeForThisConnection,
}

/// <summary>RM3QuerySerializationResult: whether to call <see cref="Replica3.Serialize"/> for a system.</summary>
public enum ReplicaQuerySerializationResult
{
    /// <summary>RM3QSR_CALL_SERIALIZE</summary>
    CallSerialize,

    /// <summary>RM3QSR_DO_NOT_CALL_SERIALIZE: not this tick.</summary>
    DoNotCallSerialize,

    /// <summary>RM3QSR_NEVER_CALL_SERIALIZE: never again for this system.</summary>
    NeverCallSerialize,
}

/// <summary>RM3ActionOnPopConnection: what happens to a replica when the system that created it goes.</summary>
public enum ReplicaActionOnPopConnection
{
    /// <summary>RM3AOPC_DO_NOTHING</summary>
    DoNothing,

    /// <summary>RM3AOPC_DELETE_REPLICA: <see cref="Replica3.PreDestruction"/> then <see cref="Replica3.DeallocReplica"/>.</summary>
    DeleteReplica,

    /// <summary>RM3AOPC_DELETE_REPLICA_AND_BROADCAST_DESTRUCTION: as <see cref="DeleteReplica"/>, telling the other systems first.</summary>
    DeleteReplicaAndBroadcastDestruction,
}

/// <summary>
/// Replica3P2PMode: how a replica is owned on a peer to peer network, for
/// <see cref="Replica3.QueryConstructionForPeerToPeer"/> and <see cref="Replica3.QuerySerializationForPeerToPeer"/>.
/// </summary>
public enum ReplicaP2PMode
{
    /// <summary>R3P2PM_SINGLE_OWNER: only the creating system constructs and serializes it.</summary>
    SingleOwner,

    /// <summary>R3P2PM_MULTI_OWNER_CURRENTLY_AUTHORITATIVE: owned in turn, and this system is the owner now.</summary>
    MultiOwnerCurrentlyAuthoritative,

    /// <summary>R3P2PM_MULTI_OWNER_NOT_CURRENTLY_AUTHORITATIVE: owned in turn, and another system is the owner now.</summary>
    MultiOwnerNotCurrentlyAuthoritative,

    /// <summary>R3P2PM_STATIC_OBJECT_CURRENTLY_AUTHORITATIVE: on every system already, and this system is the owner now.</summary>
    StaticObjectCurrentlyAuthoritative,

    /// <summary>R3P2PM_STATIC_OBJECT_NOT_CURRENTLY_AUTHORITATIVE: on every system already, and another system is the owner now.</summary>
    StaticObjectNotCurrentlyAuthoritative,
}

/// <summary>SendSerializeIfChangedResult</summary>
internal enum SendSerializeResult
{
    SentData,
    DidNotSendData,
    NeverSerialize,
}
