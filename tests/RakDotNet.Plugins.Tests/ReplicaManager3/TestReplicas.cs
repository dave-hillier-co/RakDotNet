using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.ReplicaManager3;

/// <summary>A ReplicaManager3 whose connections are <see cref="TestConnection"/>s and which records what happens.</summary>
internal sealed class TestManager : Plugins.ReplicaManager3
{
    public TestManager(bool isServer = false)
    {
        IsServer = isServer;
        SetNetworkIdManager(new NetworkIdManager());
    }

    public bool IsServer { get; }

    /// <summary>What happened to replicas and connections here, in order.</summary>
    public List<string> Log { get; } = [];

    public bool GroupDownloads { get; set; }

    public ReplicaConstructionMode Mode { get; set; } = ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction;

    public List<Replica3> ReplicasToCreate { get; } = [];

    public List<Replica3> ReplicasToDestroy { get; } = [];

    public List<ReplicaConnection> Deallocated { get; } = [];

    /// <summary>Applied to every replica this manager creates or allocates, before it is referenced or deserialized.</summary>
    public Action<TestReplica>? Configure { get; set; }

    /// <summary>When set, QuerySerializationList returns these replicas.</summary>
    public List<Replica3>? SerializationList { get; set; }

    public override ReplicaConnection? AllocConnection(SystemAddress systemAddress, RakNetGuid rakNetGuid) => new TestConnection(this, systemAddress, rakNetGuid);

    public override void DeallocConnection(ReplicaConnection connection)
    {
        Deallocated.Add(connection);
        base.DeallocConnection(connection);
    }

    public List<TestReplica> Replicas()
    {
        var list = new List<Replica3>();
        GetReferencedReplicaList(list);
        return [.. list.Cast<TestReplica>()];
    }

    public TestReplica Create(string name, int value = 0)
    {
        var replica = new TestReplica(this, name) { Value = value };
        Configure?.Invoke(replica);
        Reference(replica);
        return replica;
    }
}

internal sealed class TestConnection(TestManager manager, SystemAddress systemAddress, RakNetGuid rakNetGuid) : ReplicaConnection(systemAddress, rakNetGuid)
{
    public string DownloadStartedText { get; private set; } = "";

    public string DownloadCompleteText { get; private set; } = "";

    public override Replica3? AllocReplica(ref BitStreamReader allocationIdBitstream, Plugins.ReplicaManager3 replicaManager3)
    {
        if (!allocationIdBitstream.TryReadRakString(out var name))
            return null;
        manager.Log.Add($"alloc {name}");
        if (name == "Refused")
            return null;
        var replica = new TestReplica(manager, name);
        manager.Configure?.Invoke(replica);
        return replica;
    }

    public override bool QueryGroupDownloadMessages() => manager.GroupDownloads;

    public override ReplicaConstructionMode QueryConstructionMode() => manager.Mode;

    public override void QueryReplicaList(List<Replica3> newReplicasToCreate, List<Replica3> existingReplicasToDestroy)
    {
        newReplicasToCreate.AddRange(manager.ReplicasToCreate);
        existingReplicasToDestroy.AddRange(manager.ReplicasToDestroy);
        manager.ReplicasToCreate.Clear();
        manager.ReplicasToDestroy.Clear();
    }

    public override bool QuerySerializationList(List<Replica3> replicasToSerialize)
    {
        if (manager.SerializationList is not { } list)
            return false;
        replicasToSerialize.AddRange(list);
        return true;
    }

    public override void SerializeOnDownloadStarted(BitStream bitStream) => WriteText(bitStream, "started");

    public override void DeserializeOnDownloadStarted(ref BitStreamReader bitStream)
    {
        DownloadStartedText = bitStream.TryReadRakString(out var text) ? text : "";
        manager.Log.Add("download started");
    }

    public override void SerializeOnDownloadComplete(BitStream bitStream) => WriteText(bitStream, "complete");

    public override void DeserializeOnDownloadComplete(ref BitStreamReader bitStream)
    {
        DownloadCompleteText = bitStream.TryReadRakString(out var text) ? text : "";
        manager.Log.Add("download complete");
    }

    public static void WriteText(BitStream stream, string text)
    {
        var writer = stream.BeginWrite(text.Length + 4);
        writer.WriteRakString(text);
        stream.EndWrite(writer);
    }
}

/// <summary>
/// A replica with one int on channel 0, built from RakNet's server-construction, server-serializable defaults unless the
/// test sets other policies.
/// </summary>
internal sealed class TestReplica(TestManager manager, string name) : Replica3
{
    public string Name { get; } = name;

    public int Value { get; set; }

    public string? ConstructionText { get; private set; }

    public string? DestructionText { get; private set; }

    public ulong LastTimestamp { get; private set; }

    public int DeserializeCount { get; private set; }

    public bool Deallocated { get; private set; }

    public Func<ReplicaConnection, ReplicaConstructionState>? Construction { get; set; }

    public Func<ReplicaConnection, ReplicaDestructionState>? Destruction { get; set; }

    public Func<ReplicaConnection, ReplicaQuerySerializationResult>? Serialization { get; set; }

    public Func<ReplicaConnection, ReplicaActionOnPopConnection>? ActionOnPop { get; set; }

    public Func<ReplicaConnection, bool>? RemoteConstruction { get; set; }

    public ReplicaSerializationResult Result { get; set; } = ReplicaSerializationResult.BroadcastIdentically;

    /// <summary>Called by Serialize after the value is written, to set channels and send parameters.</summary>
    public Action<SerializeParameters>? OnSerialize { get; set; }

    public bool AcceptDestruction { get; set; } = true;

    public string? PostConstructionData { get; set; }

    public string? PostConstructionText { get; private set; }

    public override void WriteAllocationId(ReplicaConnection destinationConnection, BitStream allocationIdBitstream) =>
        TestConnection.WriteText(allocationIdBitstream, Name);

    public override ReplicaConstructionState QueryConstruction(ReplicaConnection destinationConnection, Plugins.ReplicaManager3 replicaManager3) =>
        Construction?.Invoke(destinationConnection) ?? QueryConstructionForServerConstruction(destinationConnection, manager.IsServer);

    public override ReplicaDestructionState QueryDestruction(ReplicaConnection destinationConnection, Plugins.ReplicaManager3 replicaManager3) =>
        Destruction?.Invoke(destinationConnection) ?? base.QueryDestruction(destinationConnection, replicaManager3);

    public override bool QueryRemoteConstruction(ReplicaConnection sourceConnection) =>
        RemoteConstruction?.Invoke(sourceConnection) ?? QueryRemoteConstructionForServerConstruction(sourceConnection, manager.IsServer);

    public override void SerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection) =>
        TestConnection.WriteText(constructionBitstream, Name + " SerializeConstruction");

    public override bool DeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
        ConstructionText = constructionBitstream.TryReadRakString(out var text) ? text : null;
        manager.Log.Add($"construct {Name}");
        return true;
    }

    public override void SerializeConstructionExisting(BitStream constructionBitstream, ReplicaConnection destinationConnection) =>
        TestConnection.WriteText(constructionBitstream, Name + " SerializeConstructionExisting");

    public override void DeserializeConstructionExisting(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
        ConstructionText = constructionBitstream.TryReadRakString(out var text) ? text : null;
        manager.Log.Add($"construct existing {Name}");
    }

    public override void PostSerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection)
    {
        if (PostConstructionData is { } data)
            TestConnection.WriteText(constructionBitstream, data);
    }

    public override void PostDeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
    {
        PostConstructionText = constructionBitstream.TryReadRakString(out var text) ? text : "";
        manager.Log.Add($"post construct {Name}");
    }

    public override void SerializeDestruction(BitStream destructionBitstream, ReplicaConnection destinationConnection) =>
        TestConnection.WriteText(destructionBitstream, Name + " SerializeDestruction");

    public override bool DeserializeDestruction(ref BitStreamReader destructionBitstream, ReplicaConnection sourceConnection)
    {
        DestructionText = destructionBitstream.TryReadRakString(out var text) ? text : null;
        manager.Log.Add($"destruct {Name}");
        return AcceptDestruction;
    }

    public override ReplicaActionOnPopConnection QueryActionOnPopConnection(ReplicaConnection droppedConnection) =>
        ActionOnPop?.Invoke(droppedConnection) ?? (manager.IsServer ? QueryActionOnPopConnectionForServer(droppedConnection) : QueryActionOnPopConnectionForClient(droppedConnection));

    public override void DeallocReplica(ReplicaConnection? sourceConnection)
    {
        Deallocated = true;
        manager.Log.Add($"dealloc {Name}");
        base.DeallocReplica(sourceConnection);
    }

    public override ReplicaQuerySerializationResult QuerySerialization(ReplicaConnection destinationConnection) =>
        Serialization?.Invoke(destinationConnection) ?? QuerySerializationForServerSerializable(destinationConnection, manager.IsServer);

    public override ReplicaSerializationResult Serialize(SerializeParameters serializeParameters)
    {
        serializeParameters.OutputBitstream[0].WriteInt32(Value);
        OnSerialize?.Invoke(serializeParameters);
        return Result;
    }

    public override void Deserialize(DeserializeParameters deserializeParameters)
    {
        DeserializeCount++;
        LastTimestamp = deserializeParameters.Timestamp;
        var reader = deserializeParameters.GetSerializationBitstream(0);
        if (deserializeParameters.BitstreamWrittenTo[0] && reader.TryReadInt32(out var value))
            Value = value;
        manager.Log.Add($"deserialize {Name} {Value}");
    }

    public override void PreDestruction(ReplicaConnection sourceConnection) => manager.Log.Add($"pre destruct {Name}");
}
