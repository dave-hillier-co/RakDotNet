using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// Connection_RM3: a remote system taking part in <see cref="ReplicaManager3"/>. Derive from it and implement
/// <see cref="AllocReplica"/>, the factory that creates a replica from the identifier its
/// <see cref="Replica3.WriteAllocationId"/> wrote.
/// </summary>
/// <remarks>
/// Ported from Connection_RM3 in ReplicaManager3.cpp. A connection keeps four lists: replicas both systems have (ordered by
/// the order they were first referenced here), replicas the remote system lacks and that are queried for construction,
/// and of the constructed ones those queried for serialization and for destruction.
/// </remarks>
public abstract class ReplicaConnection : IDisposable
{
    // constructedReplicaList: an OrderedList keyed on Replica3::referenceIndex (Replica3LSRComp).
    private readonly List<LastSerializationResult> _constructed = [];
    private readonly List<LastSerializationResult> _queryToConstruct = [];
    private readonly List<LastSerializationResult> _queryToSerialize = [];
    private readonly List<LastSerializationResult> _queryToDestruct = [];
    private readonly List<Replica3> _constructedCulled = [];
    private readonly List<Replica3> _destroyedCulled = [];
    private readonly List<OwnedPeerEvent> _downloadGroup = [];

    // The message SendSerialize builds, reused because serialization runs every tick.
    private readonly BitStream _serializeOutput = new();

    protected ReplicaConnection(SystemAddress systemAddress, RakNetGuid rakNetGuid)
    {
        SystemAddress = systemAddress;
        RakNetGuid = rakNetGuid;
    }

    /// <summary>GetSystemAddress</summary>
    public SystemAddress SystemAddress { get; }

    /// <summary>GetRakNetGUID</summary>
    public RakNetGuid RakNetGuid { get; }

    /// <summary>GetDownloadWasCompleted: ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE arrived from this system.</summary>
    public bool DownloadWasCompleted { get; private set; }

    /// <summary>isValidated: the remote system has this connection too, so replicas are constructed to it.</summary>
    public bool IsValidated { get; internal set; }

    // isFirstConstruction: the next construction is wrapped in the download messages.
    internal bool IsFirstConstruction { get; private set; } = true;

    // groupConstructionAndSerialize: messages from this system are held until its download completes.
    internal bool GroupConstructionAndSerialize { get; set; }

    internal List<LastSerializationResult> QueryToSerializeReplicaList => _queryToSerialize;

    internal int DownloadGroupCount => _downloadGroup.Count;

    // The bytes of the messages held.
    internal long DownloadGroupBytes { get; private set; }

    // An ID_REPLICA_MANAGER_DOWNLOAD_STARTED that opened the group is held, so returning the group closes it.
    internal bool DownloadGroupHoldsStart { get; private set; }

    /// <summary>
    /// AllocReplica: creates the replica whose type <paramref name="allocationIdBitstream"/> identifies, as written by
    /// <see cref="Replica3.WriteAllocationId"/>; null when it cannot, though the other system then still believes this one
    /// has it.
    /// </summary>
    public abstract Replica3? AllocReplica(ref BitStreamReader allocationIdBitstream, ReplicaManager3 replicaManager3);

    /// <summary>GetConstructedReplicas: the replicas both systems have, in the order they were first referenced here.</summary>
    public void GetConstructedReplicas(List<Replica3> objectsTheyDoHave)
    {
        ArgumentNullException.ThrowIfNull(objectsTheyDoHave);
        objectsTheyDoHave.Clear();
        foreach (var lsr in _constructed)
            objectsTheyDoHave.Add(lsr.Replica);
    }

    /// <summary>HasReplicaConstructed: whether this system is believed to have <paramref name="replica"/>.</summary>
    public bool HasReplicaConstructed(Replica3 replica)
    {
        ArgumentNullException.ThrowIfNull(replica);
        return FindConstructed(replica, out _);
    }

    /// <summary>SerializeOnDownloadStarted: data for the remote <see cref="DeserializeOnDownloadStarted"/>.</summary>
    public virtual void SerializeOnDownloadStarted(BitStream bitStream)
    {
    }

    /// <summary>DeserializeOnDownloadStarted: reads ID_REPLICA_MANAGER_DOWNLOAD_STARTED's data.</summary>
    public virtual void DeserializeOnDownloadStarted(ref BitStreamReader bitStream)
    {
    }

    /// <summary>SerializeOnDownloadComplete: data for the remote <see cref="DeserializeOnDownloadComplete"/>.</summary>
    public virtual void SerializeOnDownloadComplete(BitStream bitStream)
    {
    }

    /// <summary>DeserializeOnDownloadComplete: reads ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE's data.</summary>
    public virtual void DeserializeOnDownloadComplete(ref BitStreamReader bitStream)
    {
    }

    /// <summary>
    /// QueryGroupDownloadMessages: true holds this system's download messages, from ID_REPLICA_MANAGER_DOWNLOAD_STARTED to
    /// ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE, and then returns them all together.
    /// </summary>
    /// <remarks>
    /// The remote system decides how much is held. Once more than <see cref="ReplicaManager3.MaxDownloadGroupMessages"/>
    /// messages or <see cref="ReplicaManager3.MaxDownloadGroupBytes"/> bytes are held, they are returned at once, in order,
    /// and the rest of that download is not grouped.
    /// </remarks>
    public virtual bool QueryGroupDownloadMessages() => false;

    /// <summary>QueryConstructionMode: how to find the replicas this system should have; return the same value every time.</summary>
    public virtual ReplicaConstructionMode QueryConstructionMode() => ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction;

    /// <summary>
    /// QueryReplicaList: for <see cref="ReplicaConstructionMode.QueryConnectionForReplicaList"/>, adds the replicas to
    /// construct to and destroy on this system. A replica this system has already is serialized to it once, however
    /// often it is named.
    /// </summary>
    public virtual void QueryReplicaList(List<Replica3> newReplicasToCreate, List<Replica3> existingReplicasToDestroy)
    {
    }

    /// <summary>
    /// QuerySerializationList: return true to serialize only the replicas added to <paramref name="replicasToSerialize"/>,
    /// in that order, this tick; false serializes every constructed replica in reference order.
    /// </summary>
    public virtual bool QuerySerializationList(List<Replica3> replicasToSerialize) => false;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The C++ destructor: releases what this connection kept of each replica.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
            return;
        foreach (var lsr in _constructed)
            lsr.Dispose();
        foreach (var lsr in _queryToConstruct)
            lsr.Dispose();
        ClearDownloadGroup();
        _serializeOutput.Dispose();
    }

    internal void ClearDownloadGroup()
    {
        foreach (var owned in _downloadGroup)
            owned.Dispose();
        _downloadGroup.Clear();
        DownloadGroupBytes = 0;
        DownloadGroupHoldsStart = false;
    }

    internal void HoldDownloadMessage(OwnedPeerEvent owned, int length, bool isStart)
    {
        _downloadGroup.Add(owned);
        DownloadGroupBytes += length;
        DownloadGroupHoldsStart |= isStart;
    }

    // The held messages, oldest first, leaving the group empty.
    internal OwnedPeerEvent[] TakeDownloadGroup()
    {
        var held = _downloadGroup.ToArray();
        _downloadGroup.Clear();
        DownloadGroupBytes = 0;
        DownloadGroupHoldsStart = false;
        return held;
    }

    internal void MarkDownloadComplete() => DownloadWasCompleted = true;

    // SendValidation (ReplicaManager3.cpp:2372-2379): ID_REPLICA_MANAGER_SCOPE_CHANGE, hijacked to mean validation.
    internal void SendValidation(IPluginHost host, byte worldId)
    {
        using var message = new BitStream(2);
        message.WriteByte((byte)MessageId.ReplicaManagerScopeChange);
        message.WriteByte(worldId);
        host.Send(message, SystemAddress, Reliability.ReliableOrdered, Priority.High, 0);
    }

    // AutoConstructByQuery (ReplicaManager3.cpp:723-863).
    internal void AutoConstructByQuery(ReplicaManager3 replicaManager3, byte worldId)
    {
        var constructionMode = QueryConstructionMode();
        _constructedCulled.Clear();
        _destroyedCulled.Clear();

        if (constructionMode is ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
        {
            var index = 0;
            while (index < _queryToConstruct.Count)
            {
                var lsr = _queryToConstruct[index];
                var constructionState = lsr.Replica.QueryConstruction(this, replicaManager3);
                if (constructionState is ReplicaConstructionState.AlreadyExistsRemotely or ReplicaConstructionState.AlreadyExistsRemotelyDoNotConstruct)
                {
                    OnConstructToThisConnection(index);
                    if (constructionState == ReplicaConstructionState.AlreadyExistsRemotely)
                        _constructedCulled.Add(lsr.Replica);
                }
                else if (constructionState == ReplicaConstructionState.SendConstruction)
                {
                    OnConstructToThisConnection(index);
                    _constructedCulled.Add(lsr.Replica);
                }
                else if (constructionState == ReplicaConstructionState.NeverConstruct)
                {
                    // OnNeverConstruct
                    _queryToConstruct.RemoveAt(index);
                    lsr.Dispose();
                }
                else
                {
                    index++;
                }
            }

            if (constructionMode == ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
            {
                index = 0;
                while (index < _queryToDestruct.Count)
                {
                    var lsr = _queryToDestruct[index];
                    var destructionState = lsr.Replica.QueryDestruction(this, replicaManager3);
                    if (destructionState == ReplicaDestructionState.SendDestruction)
                    {
                        OnSendDestructionFromQuery(index);
                        _destroyedCulled.Add(lsr.Replica);
                    }
                    else if (destructionState == ReplicaDestructionState.DoNotQueryDestruction)
                    {
                        // OnDoNotQueryDestruction
                        _queryToDestruct.RemoveAt(index);
                    }
                    else
                    {
                        index++;
                    }
                }
            }
        }
        else if (constructionMode == ReplicaConstructionMode.QueryConnectionForReplicaList)
        {
            QueryReplicaList(_constructedCulled, _destroyedCulled);
            foreach (var replica in _constructedCulled)
                OnConstructToThisConnection(replica, replicaManager3);
            foreach (var replica in _destroyedCulled)
            {
                if (!FindConstructed(replica, out var constructedIndex))
                    continue;
                _constructed.RemoveAt(constructedIndex);
                var serializeIndex = _queryToSerialize.FindIndex(l => l.Replica == replica);
                if (serializeIndex >= 0)
                    _queryToSerialize.RemoveAt(serializeIndex);
            }
        }

        SendConstruction(_constructedCulled, _destroyedCulled, replicaManager3.DefaultSendParameters, replicaManager3.RequireHost(), worldId, replicaManager3);
    }

    // SendConstruction (ReplicaManager3.cpp:2189-2368). Constructions and destructions of a tick go in one message, so
    // replicas that need each other arrive together.
    internal void SendConstruction(List<Replica3> newObjects, List<Replica3> deletedObjects, ReplicaSendParameters sendParameters,
                                   IPluginHost host, byte worldId, ReplicaManager3 replicaManager3)
    {
        if (newObjects.Count == 0 && deletedObjects.Count == 0)
            return;

        using var bsOut = new BitStream();
        if (IsFirstConstruction)
        {
            bsOut.WriteByte((byte)MessageId.ReplicaManagerDownloadStarted);
            bsOut.WriteByte(worldId);
            SerializeOnDownloadStarted(bsOut);
            host.Send(bsOut, SystemAddress, Reliability.ReliableOrdered, sendParameters.Priority, sendParameters.OrderingChannel, new SendReceipt(sendParameters.SendReceipt));
        }

        bsOut.Reset();
        bsOut.WriteByte((byte)MessageId.ReplicaManagerConstruction);
        bsOut.WriteByte(worldId);
        bsOut.WriteUInt16((ushort)newObjects.Count);

        foreach (var replica in newObjects)
        {
            // Each entry starts with the bit offset of its end, written when the end is known.
            var offsetStart = bsOut.BitsWritten;
            bsOut.WriteUInt32((uint)offsetStart);
            bsOut.WriteUInt64(replica.NetworkId);
            var actuallyCreateObject = replica.QueryConstruction(this, replicaManager3) == ReplicaConstructionState.SendConstruction;
            bsOut.WriteBit(actuallyCreateObject);
            bsOut.AlignToByte();
            if (actuallyCreateObject)
            {
                bsOut.WriteRakNetGuid(replica.CreatingSystemGuid);
                // The offset of the end of the allocation ID, written when it is known.
                var offsetStart2 = bsOut.BitsWritten;
                bsOut.WriteUInt32((uint)offsetStart2);
                bsOut.AlignToByte();
                replica.WriteAllocationId(this, bsOut);
                bsOut.AlignToByte();
                PatchOffset(bsOut, offsetStart2);
                replica.SerializeConstruction(bsOut, this);
            }
            else
            {
                replica.SerializeConstructionExisting(bsOut, this);
            }
            bsOut.AlignToByte();
            PatchOffset(bsOut, offsetStart);
        }

        using (var bsOut2 = new BitStream())
        {
            foreach (var replica in newObjects)
            {
                bsOut2.Reset();
                if (replica.QueryConstruction(this, replicaManager3) == ReplicaConstructionState.SendConstruction)
                    replica.PostSerializeConstruction(bsOut2, this);
                else
                    replica.PostSerializeConstructionExisting(bsOut2, this);
                if (bsOut2.BitsWritten > 0)
                {
                    bsOut.WriteBit(true);
                    bsOut.AlignToByte();
                    var offsetStart = bsOut.BitsWritten;
                    bsOut.WriteUInt32((uint)offsetStart);
                    bsOut.WriteUInt64(replica.NetworkId);
                    bsOut.AlignToByte();
                    var post = bsOut2.CreateReader();
                    bsOut.Write(ref post);
                    bsOut.AlignToByte();
                    PatchOffset(bsOut, offsetStart);
                }
                else
                {
                    bsOut.WriteBit(false);
                }
            }
        }
        bsOut.AlignToByte();

        bsOut.WriteUInt16((ushort)deletedObjects.Count);
        foreach (var replica in deletedObjects)
        {
            bsOut.WriteUInt64(replica.NetworkId);
            var offsetStart = bsOut.BitsWritten;
            bsOut.WriteUInt32((uint)offsetStart);
            replica.DeletingSystemGuid = host.RakNetGuid;
            bsOut.WriteRakNetGuid(replica.DeletingSystemGuid);
            replica.SerializeDestruction(bsOut, this);
            bsOut.AlignToByte();
            PatchOffset(bsOut, offsetStart);
        }
        host.Send(bsOut, SystemAddress, Reliability.ReliableOrdered, sendParameters.Priority, sendParameters.OrderingChannel, new SendReceipt(sendParameters.SendReceipt));

        // The first serialization of each new replica follows its construction, so a replica that never changes again
        // still reaches the new system (ReplicaManager3.cpp:2313-2355). A uniquely serialized one waits for the update tick.
        var sp = replicaManager3.ConstructionSerializeParameters;
        sp.WhenLastSerialized = 0;
        for (var i = 0; i < SerializeParameters.ChannelCount; i++)
        {
            sp.SetLastSent(i, null);
            sp.Pro[i] = sendParameters with { Reliability = Reliability.ReliableOrdered };
        }
        sp.BitsWrittenSoFar = 0;
        sp.CurrentTime = host.GetTime();
        Span<bool> allIndices = stackalloc bool[SerializeParameters.ChannelCount];
        allIndices.Fill(true);
        foreach (var replica in newObjects)
        {
            sp.DestinationConnection = this;
            sp.MessageTimestamp = 0;
            foreach (var stream in sp.OutputBitstream)
                stream.Reset();
            var result = replica.Serialize(sp);
            if (result is ReplicaSerializationResult.NeverSerializeForThisConnection or ReplicaSerializationResult.DoNotSerialize or ReplicaSerializationResult.SerializedUniquely)
                continue;
            foreach (var stream in sp.OutputBitstream)
                sp.BitsWrittenSoFar += stream.BitsWritten;
            SendSerialize(replica, allIndices, sp.OutputBitstream, sp.MessageTimestamp, sp.Pro, host, worldId, host.GetTime());
        }

        if (IsFirstConstruction)
        {
            bsOut.Reset();
            bsOut.WriteByte((byte)MessageId.ReplicaManagerDownloadComplete);
            bsOut.WriteByte(worldId);
            SerializeOnDownloadComplete(bsOut);
            host.Send(bsOut, SystemAddress, Reliability.ReliableOrdered, sendParameters.Priority, sendParameters.OrderingChannel, new SendReceipt(sendParameters.SendReceipt));
        }
        IsFirstConstruction = false;
    }

    // SendSerialize (ReplicaManager3.cpp:1517-1609): channels whose send parameters match share a message, so a change of
    // parameters sends what was gathered with every later channel marked unwritten, and starts again with every earlier
    // channel marked unwritten.
    internal SendSerializeResult SendSerialize(Replica3 replica, ReadOnlySpan<bool> indicesToSend, IReadOnlyList<BitStream> serializationData, ulong timestamp,
                                               ReadOnlySpan<ReplicaSendParameters> sendParameters, IPluginHost host, byte worldId, ulong currentTime)
    {
        long sum = 0;
        for (var z = 0; z < SerializeParameters.ChannelCount; z++)
        {
            if (indicesToSend[z])
                sum += serializationData[z].BitsWritten;
        }

        var output = _serializeOutput;
        output.Reset();
        Span<int> bitsPerChannel = stackalloc int[SerializeParameters.ChannelCount];
        if (sum == 0)
        {
            bitsPerChannel.Clear();
            replica.OnSerializeTransmission(output, this, bitsPerChannel, currentTime);
            return SendSerializeResult.DidNotSendData;
        }

        var lastPro = sendParameters[0];
        for (var channelIndex = 0; channelIndex < SerializeParameters.ChannelCount; channelIndex++)
        {
            if (channelIndex == 0)
            {
                WriteSerializeHeader(replica, timestamp, output, worldId);
            }
            else if (lastPro != sendParameters[channelIndex])
            {
                for (var rest = channelIndex; rest < SerializeParameters.ChannelCount; rest++)
                {
                    bitsPerChannel[rest] = 0;
                    output.WriteBit(false);
                }
                replica.OnSerializeTransmission(output, this, bitsPerChannel, currentTime);
                Send(host, output, lastPro);

                var anyData = false;
                for (var rest = channelIndex; rest < SerializeParameters.ChannelCount; rest++)
                {
                    if (serializationData[rest].BitsWritten > 0)
                    {
                        anyData = true;
                        break;
                    }
                }
                if (!anyData)
                    return SendSerializeResult.SentData;

                WriteSerializeHeader(replica, timestamp, output, worldId);
                for (var earlier = 0; earlier < channelIndex; earlier++)
                {
                    bitsPerChannel[earlier] = 0;
                    output.WriteBit(false);
                }
                lastPro = sendParameters[channelIndex];
            }

            var bitsUsed = serializationData[channelIndex].BitsWritten;
            var channelHasData = indicesToSend[channelIndex] && bitsUsed > 0;
            output.WriteBit(channelHasData);
            if (channelHasData)
            {
                bitsPerChannel[channelIndex] = bitsUsed;
                var writer = output.BeginWrite(6);
                writer.WriteCompressed((uint)bitsUsed);
                writer.AlignToByte();
                output.EndWrite(writer);
                var data = serializationData[channelIndex].CreateReader();
                output.Write(ref data);
            }
            else
            {
                bitsPerChannel[channelIndex] = 0;
            }
        }
        replica.OnSerializeTransmission(output, this, bitsPerChannel, currentTime);
        Send(host, output, lastPro);
        return SendSerializeResult.SentData;
    }

    // SendSerializeIfChanged (ReplicaManager3.cpp:1613-1766).
    internal SendSerializeResult SendSerializeIfChanged(LastSerializationResult lsr, SerializeParameters sp, IPluginHost host, byte worldId, ReplicaManager3 replicaManager, ulong currentTime)
    {
        var replica = lsr.Replica;
        if (replica.NetworkId == Protocol.Wire.Messages.RakNetConstants.UnassignedNetworkId)
            return SendSerializeResult.DidNotSendData;

        var query = replica.QuerySerialization(this);
        if (query == ReplicaQuerySerializationResult.NeverCallSerialize)
        {
            OnNeverSerialize(lsr);
            return SendSerializeResult.NeverSerialize;
        }
        if (query == ReplicaQuerySerializationResult.DoNotCallSerialize)
            return SendSerializeResult.DidNotSendData;

        if (replica.ForceSendUntilNextUpdate)
        {
            var last = replica.LastSentSerialization;
            for (var z = 0; z < SerializeParameters.ChannelCount; z++)
            {
                if (last.IndicesToSend[z])
                    sp.BitsWrittenSoFar += last.Streams[z].BitsWritten;
            }
            return SendSerialize(replica, last.IndicesToSend, last.Streams, sp.MessageTimestamp, sp.Pro, host, worldId, currentTime);
        }

        for (var i = 0; i < SerializeParameters.ChannelCount; i++)
        {
            sp.OutputBitstream[i].Reset();
            sp.SetLastSent(i, lsr.Channels is { } channels ? channels.Streams[i] : replica.LastSentSerialization.Streams[i]);
        }

        var serializationResult = replica.Serialize(sp);
        if (serializationResult == ReplicaSerializationResult.NeverSerializeForThisConnection)
        {
            OnNeverSerialize(lsr);
            return SendSerializeResult.NeverSerialize;
        }
        if (serializationResult == ReplicaSerializationResult.DoNotSerialize)
            return SendSerializeResult.DidNotSendData;

        long sum = 0;
        foreach (var stream in sp.OutputBitstream)
            sum += stream.BitsWritten;
        if (sum == 0)
            return SendSerializeResult.DidNotSendData;

        if (serializationResult == ReplicaSerializationResult.SerializedAlways)
        {
            Span<bool> allIndices = stackalloc bool[SerializeParameters.ChannelCount];
            allIndices.Fill(true);
            var channels = lsr.AllocChannels();
            for (var z = 0; z < SerializeParameters.ChannelCount; z++)
            {
                sp.BitsWrittenSoFar += sp.OutputBitstream[z].BitsWritten;
                CopyBits(sp.OutputBitstream[z], channels.Streams[z]);
            }
            return SendSerialize(replica, allIndices, sp.OutputBitstream, sp.MessageTimestamp, sp.Pro, host, worldId, currentTime);
        }

        if (serializationResult == ReplicaSerializationResult.SerializedAlwaysIdentically)
        {
            var last = replica.LastSentSerialization;
            for (var z = 0; z < SerializeParameters.ChannelCount; z++)
            {
                last.IndicesToSend[z] = sp.OutputBitstream[z].BitsWritten > 0;
                sp.BitsWrittenSoFar += sp.OutputBitstream[z].BitsWritten;
                CopyBits(sp.OutputBitstream[z], last.Streams[z]);
                replica.ForceSendUntilNextUpdate = true;
            }
            return SendSerialize(replica, last.IndicesToSend, sp.OutputBitstream, sp.MessageTimestamp, sp.Pro, host, worldId, currentTime);
        }

        Span<bool> indicesToSend = stackalloc bool[SerializeParameters.ChannelCount];
        if (serializationResult is ReplicaSerializationResult.BroadcastIdentically or ReplicaSerializationResult.BroadcastIdenticallyForceSerialization)
        {
            var last = replica.LastSentSerialization;
            for (var z = 0; z < SerializeParameters.ChannelCount; z++)
            {
                var output = sp.OutputBitstream[z];
                if (output.BitsWritten > 0 &&
                    (serializationResult == ReplicaSerializationResult.BroadcastIdenticallyForceSerialization || !SameBits(output, last.Streams[z])))
                {
                    indicesToSend[z] = true;
                    last.IndicesToSend[z] = true;
                    sp.BitsWrittenSoFar += output.BitsWritten;
                    CopyBits(output, last.Streams[z]);
                    replica.ForceSendUntilNextUpdate = true;
                }
                else
                {
                    indicesToSend[z] = false;
                    last.IndicesToSend[z] = false;
                }
            }
        }
        else
        {
            // SerializedUniquely, and any other value, compared with what was last sent to this system.
            var channels = lsr.AllocChannels();
            for (var z = 0; z < SerializeParameters.ChannelCount; z++)
            {
                var output = sp.OutputBitstream[z];
                if (output.BitsWritten > 0 && !SameBits(output, channels.Streams[z]))
                {
                    indicesToSend[z] = true;
                    sp.BitsWrittenSoFar += output.BitsWritten;
                    CopyBits(output, channels.Streams[z]);
                }
                else
                {
                    indicesToSend[z] = false;
                }
            }
        }

        if (serializationResult is ReplicaSerializationResult.BroadcastIdentically or ReplicaSerializationResult.BroadcastIdenticallyForceSerialization)
            replica.ForceSendUntilNextUpdate = true;
        return SendSerialize(replica, indicesToSend, sp.OutputBitstream, sp.MessageTimestamp, sp.Pro, host, worldId, currentTime);
    }

    // OnLocalReference (ReplicaManager3.cpp:1770-1796).
    internal void OnLocalReference(Replica3 replica3, ReplicaManager3 replicaManager) =>
        _queryToConstruct.Add(new LastSerializationResult(replica3, replicaManager.RequireHost().GetTime()));

    // OnDereference (ReplicaManager3.cpp:1800-1854): the replica leaves every list.
    internal void OnDereference(Replica3 replica3)
    {
        if (replica3.Manager is null)
            return;
        LastSerializationResult? lsr = null;
        if (FindConstructed(replica3, out var index))
        {
            lsr = _constructed[index];
            _constructed.RemoveAt(index);
        }
        lsr = RemoveFirst(_queryToConstruct, replica3) ?? lsr;
        lsr = RemoveFirst(_queryToSerialize, replica3) ?? lsr;
        lsr = RemoveFirst(_queryToDestruct, replica3) ?? lsr;
        lsr?.Dispose();
    }

    // OnDownloadFromThisSystem (ReplicaManager3.cpp:1858-1889): this system sent the replica, so it has it.
    internal void OnDownloadFromThisSystem(Replica3 replica3, ReplicaManager3 replicaManager)
    {
        var lsr = new LastSerializationResult(replica3, replicaManager.RequireHost().GetTime());
        if (QueryConstructionMode() is ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
        {
            RemoveFirst(_queryToConstruct, replica3);
            _queryToDestruct.Add(lsr);
        }
        if (InsertConstructed(lsr))
            _queryToSerialize.Add(lsr);
    }

    // OnDownloadFromOtherSystem (ReplicaManager3.cpp:1893-1909): another system sent it, so query it for this one.
    internal void OnDownloadFromOtherSystem(Replica3 replica3, ReplicaManager3 replicaManager)
    {
        if (QueryConstructionMode() is not (ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction))
            return;
        if (_queryToConstruct.Exists(l => l.Replica == replica3))
            return;
        OnLocalReference(replica3, replicaManager);
    }

    // OnDownloadExisting (ReplicaManager3.cpp:2001-2022): a construction for a replica this system already has.
    internal void OnDownloadExisting(Replica3 replica3, ReplicaManager3 replicaManager)
    {
        if (QueryConstructionMode() is ReplicaConstructionMode.QueryReplicaForConstruction or ReplicaConstructionMode.QueryReplicaForConstructionAndDestruction)
        {
            var index = _queryToConstruct.FindIndex(l => l.Replica == replica3);
            if (index >= 0)
                OnConstructToThisConnection(index);
        }
        else
        {
            OnConstructToThisConnection(replica3, replicaManager);
        }
    }

    // OnConstructToThisConnection by index, also OnReplicaAlreadyExists (ReplicaManager3.cpp:1928-1944, 1981-1997).
    private void OnConstructToThisConnection(int queryToConstructIndex)
    {
        var lsr = _queryToConstruct[queryToConstructIndex];
        _queryToConstruct.RemoveAt(queryToConstructIndex);
        InsertConstructed(lsr);
        _queryToDestruct.Add(lsr);
        _queryToSerialize.Add(lsr);
    }

    // OnConstructToThisConnection by replica, for QUERY_CONNECTION_FOR_REPLICA_LIST (ReplicaManager3.cpp:1948-1958). RakNet
    // lists the replica for serialization even when the constructed list refuses it as a duplicate, so each construction
    // of a replica the connection has adds another serialization per tick; here a duplicate is dropped.
    private void OnConstructToThisConnection(Replica3 replica, ReplicaManager3 replicaManager)
    {
        var lsr = new LastSerializationResult(replica, replicaManager.RequireHost().GetTime());
        if (!InsertConstructed(lsr))
        {
            lsr.Dispose();
            return;
        }
        _queryToSerialize.Add(lsr);
    }

    // OnNeverSerialize (ReplicaManager3.cpp:1962-1977).
    private void OnNeverSerialize(LastSerializationResult lsr)
    {
        var index = _queryToSerialize.IndexOf(lsr);
        if (index >= 0)
            _queryToSerialize.RemoveAt(index);
    }

    // OnSendDestructionFromQuery (ReplicaManager3.cpp:2025-2054): back to being queried for construction.
    private void OnSendDestructionFromQuery(int queryToDestructIndex)
    {
        var lsr = _queryToDestruct[queryToDestructIndex];
        _queryToDestruct.RemoveAt(queryToDestructIndex);
        RemoveFirst(_queryToSerialize, lsr.Replica);
        var constructedIndex = _constructed.FindIndex(l => l.Replica == lsr.Replica);
        if (constructedIndex >= 0)
            _constructed.RemoveAt(constructedIndex);
        _queryToConstruct.Add(lsr);
    }

    // SendSerializeHeader (ReplicaManager3.cpp:1496-1508).
    private static void WriteSerializeHeader(Replica3 replica, ulong timestamp, BitStream output, byte worldId)
    {
        output.Reset();
        if (timestamp != 0)
        {
            output.WriteByte((byte)MessageId.Timestamp);
            output.WriteUInt64(timestamp);
        }
        output.WriteByte((byte)MessageId.ReplicaManagerSerialize);
        output.WriteByte(worldId);
        output.WriteUInt64(replica.NetworkId);
    }

    private void Send(IPluginHost host, BitStream message, ReplicaSendParameters pro) =>
        host.Send(message, SystemAddress, pro.Reliability, pro.Priority, pro.OrderingChannel, new SendReceipt(pro.SendReceipt));

    // OrderedList::GetIndexFromKey with Replica3LSRComp: a binary search on referenceIndex.
    private bool FindConstructed(Replica3 replica, out int index)
    {
        int low = 0, high = _constructed.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) >> 1);
            var key = _constructed[mid].Replica.ReferenceIndex;
            if (replica.ReferenceIndex == key)
            {
                index = mid;
                return true;
            }
            if (replica.ReferenceIndex < key)
                high = mid - 1;
            else
                low = mid + 1;
        }
        index = low;
        return false;
    }

    // OrderedList::Insert, which refuses a key already present.
    private bool InsertConstructed(LastSerializationResult lsr)
    {
        if (FindConstructed(lsr.Replica, out var index))
            return false;
        _constructed.Insert(index, lsr);
        return true;
    }

    private static LastSerializationResult? RemoveFirst(List<LastSerializationResult> list, Replica3 replica)
    {
        var index = list.FindIndex(l => l.Replica == replica);
        if (index < 0)
            return null;
        var lsr = list[index];
        list.RemoveAt(index);
        return lsr;
    }

    // Overwrites the uint32 placeholder at offset with the stream's current end, then returns to the end.
    private static void PatchOffset(BitStream stream, int offset)
    {
        var end = stream.BitsWritten;
        stream.SetWriteOffset(offset);
        stream.WriteUInt32((uint)end);
        stream.SetWriteOffset(end);
    }

    private static void CopyBits(BitStream source, BitStream destination)
    {
        destination.Reset();
        var reader = source.CreateReader();
        destination.Write(ref reader);
    }

    // The memcmp of GetNumberOfBytesUsed bytes after comparing bit counts; bits past the end are zero in both.
    private static bool SameBits(BitStream left, BitStream right) =>
        left.BitsWritten == right.BitsWritten && left.Written.SequenceEqual(right.Written);
}
