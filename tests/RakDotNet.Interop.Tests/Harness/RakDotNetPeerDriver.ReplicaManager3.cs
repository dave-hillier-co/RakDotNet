using System.Buffers.Binary;
using System.Collections.Concurrent;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The rm3* commands on a <see cref="ReplicaManager3"/> with the replicas of Samples/ReplicaManager3 and the User of
/// Samples/ReplicatedLogin, ported as interop-peer has them, with its results and errors. ReplicaManager3 runs on the
/// pump's dispatch step, so every command runs there too, as interop-peer runs commands under the lock Receive() takes.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IReplicaManager3Driver
{
    private static readonly TimeSpan DispatchTimeout = TimeSpan.FromSeconds(10);

    private DispatchStep? _rm3Dispatch;
    private SampleReplicaManager? _rm3;

    public async Task AttachReplicaManager3Async(string topology, int autoSerializeMs = 30, bool groupDownloads = true, CancellationToken cancellationToken = default)
    {
        var peer = Started("rm3Attach");
        if (_rm3Dispatch is not null)
            return;
        var parsed = topology switch
        {
            "CLIENT" => SampleTopology.Client,
            "SERVER" => SampleTopology.Server,
            "P2P" => SampleTopology.P2P,
            _ => throw new PeerCommandException("rm3Attach", $"unknown topology '{topology}'"),
        };
        var dispatch = new DispatchStep();
        _rm3Dispatch = dispatch;
        await AttachAsync(peer, dispatch, cancellationToken).ConfigureAwait(false);
        _rm3 = await dispatch.RunAsync(() =>
        {
            var manager = new SampleReplicaManager(parsed, groupDownloads)
            {
                AutoSerializeInterval = autoSerializeMs < 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromMilliseconds(autoSerializeMs),
            };
            manager.SetNetworkIdManager(new NetworkIdManager());
            peer.Plugins.Attach(manager);
            peer.Plugins.Attach(new ReceiptForwarder(manager));
            return manager;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ulong>> CreateReplicasAsync(IReadOnlyList<string> kinds, string? username = null, ulong? userGuid = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
            throw new PeerCommandException("rm3Create", "field 'kinds' must be a non-empty array");
        return RunRm3Async<IReadOnlyList<ulong>>("rm3Create", manager =>
        {
            var created = new List<Replica3>();
            foreach (var kind in kinds)
            {
                var replica = manager.NewReplica(kind);
                if (replica is null)
                {
                    foreach (var made in created)
                        made.Dispose();
                    throw new PeerCommandException("rm3Create", $"unknown kind '{kind}'");
                }
                if (replica is SampleUser user)
                {
                    user.Username = username ?? "";
                    if (userGuid is { } guid)
                        user.UserGuid = new RakNetGuid(guid);
                }
                created.Add(replica);
            }
            foreach (var replica in created)
                manager.Reference(replica);
            return [.. created.Select(r => r.NetworkId)];
        }, cancellationToken);
    }

    public Task SetReplicaAsync(ulong networkId, ReplicaValues values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        return RunRm3Async("rm3Set", manager =>
        {
            switch (FindReplica("rm3Set", manager, networkId))
            {
                case SampleReplica sample:
                    sample.Var1Unreliable = values.Var1 ?? sample.Var1Unreliable;
                    sample.Var2Unreliable = values.Var2 ?? sample.Var2Unreliable;
                    sample.Var3Reliable = values.Var3 ?? sample.Var3Reliable;
                    sample.Var4Reliable = values.Var4 ?? sample.Var4Reliable;
                    break;
                case SampleUser user:
                    user.Score = values.Score ?? user.Score;
                    user.Username = values.Username ?? user.Username;
                    break;
            }
            return true;
        }, cancellationToken);
    }

    public Task<int> DestroyMyReplicasAsync(CancellationToken cancellationToken = default) =>
        RunRm3Async("rm3DestroyMine", manager =>
        {
            var replicas = new List<Replica3>();
            manager.GetReplicasCreatedByMe(replicas);
            manager.BroadcastDestructionList(replicas, SystemAddress.Unassigned);
            foreach (var replica in replicas)
                replica.Dispose();
            return replicas.Count;
        }, cancellationToken);

    public Task DestroyReplicaAsync(ulong networkId, CancellationToken cancellationToken = default) =>
        RunRm3Async("rm3Destroy", manager =>
        {
            var replica = FindReplica("rm3Destroy", manager, networkId);
            replica.BroadcastDestruction();
            manager.Dereference(replica);
            replica.Dispose();
            return true;
        }, cancellationToken);

    public Task<ReplicaManager3Info> GetReplicaManager3InfoAsync(CancellationToken cancellationToken = default) =>
        RunRm3Async("rm3Info", manager =>
        {
            var referenced = new List<Replica3>();
            manager.GetReferencedReplicaList(referenced);
            var replicas = referenced.Select(r => r switch
            {
                SampleReplica s => new ReplicaState(s.NetworkId, s.Name, s.CreatingSystemGuid.Value, s.Var1Unreliable, s.Var2Unreliable, s.Var3Reliable, s.Var4Reliable, s.ConstructionText),
                SampleUser u => new ReplicaState(u.NetworkId, "User", u.CreatingSystemGuid.Value, Score: u.Score, Username: u.Username, UserGuid: u.UserGuid.Value),
                _ => new ReplicaState(r.NetworkId, "", r.CreatingSystemGuid.Value),
            }).ToList();
            var connections = new List<ReplicaConnectionState>();
            for (var i = 0; i < manager.GetConnectionCount(); i++)
            {
                var connection = manager.GetConnectionAtIndex(i);
                var constructed = new List<Replica3>();
                connection.GetConstructedReplicas(constructed);
                connections.Add(new ReplicaConnectionState(connection.RakNetGuid.Value, connection.IsValidated, connection.DownloadWasCompleted, [.. constructed.Select(c => c.NetworkId)]));
            }
            return new ReplicaManager3Info(replicas, connections, [.. manager.Destructions], manager.GetAllConnectionDownloadsCompleted());
        }, cancellationToken);

    private static Replica3 FindReplica(string command, SampleReplicaManager manager, ulong networkId) =>
        manager.GetNetworkIdManager()!.GetObject<Replica3>(networkId) ?? throw new PeerCommandException(command, "no replica has that networkId");

    private Task<T> RunRm3Async<T>(string command, Func<SampleReplicaManager, T> work, CancellationToken cancellationToken)
    {
        Started(command);
        if (_rm3Dispatch is not { } dispatch || _rm3 is not { } manager)
            throw new PeerCommandException(command, "no ReplicaManager3 is attached; send rm3Attach first");
        return dispatch.RunAsync(() => work(manager), cancellationToken);
    }

    private static void WriteRakString(BitStream stream, string text)
    {
        var writer = stream.BeginWrite(text.Length * 4 + 4);
        writer.WriteRakString(text);
        stream.EndWrite(writer);
    }

    // PrintStringInBitstream: the RakString the sample prints.
    private static string ReadText(ref BitStreamReader reader) => reader.TryReadRakString(out var text) ? text : "";

    private enum SampleTopology
    {
        Client,
        Server,
        P2P,
    }

    /// <summary>Runs commands on the dispatch step, from its OnUpdate, which every read runs.</summary>
    private sealed class DispatchStep : IRakPlugin
    {
        private readonly ConcurrentQueue<Action> _work = new();

        public Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Enqueue(() =>
            {
                try
                {
                    completion.TrySetResult(work());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            });
            return completion.Task.WaitAsync(DispatchTimeout, cancellationToken);
        }

        void IRakPlugin.OnUpdate()
        {
            while (_work.TryDequeue(out var work))
                work();
        }
    }

    // ReplicaManager3Sample of the sample, with the commands' topology and the destructions recorded.
    private sealed class SampleReplicaManager(SampleTopology topology, bool groupDownloads) : ReplicaManager3
    {
        public SampleTopology Topology { get; } = topology;

        public List<ReplicaDestruction> Destructions { get; } = [];

        public override ReplicaConnection? AllocConnection(SystemAddress systemAddress, RakNetGuid rakNetGuid) =>
            new SampleConnection(this, systemAddress, rakNetGuid, groupDownloads);

        public Replica3? NewReplica(string kind) => kind switch
        {
            "ClientCreatible_ClientSerialized" => new SampleReplica(this, kind, SampleReplica.Construction.Client, SampleReplica.Serialization.Client, clientDoesNotSerialize: false, ReplicaActionOnPopConnection.DeleteReplica),
            "ServerCreated_ClientSerialized" => new SampleReplica(this, kind, SampleReplica.Construction.Server, SampleReplica.Serialization.Client, clientDoesNotSerialize: false, ReplicaActionOnPopConnection.DeleteReplicaAndBroadcastDestruction),
            "ClientCreatible_ServerSerialized" => new SampleReplica(this, kind, SampleReplica.Construction.Client, SampleReplica.Serialization.Server, clientDoesNotSerialize: true, ReplicaActionOnPopConnection.DeleteReplica),
            "ServerCreated_ServerSerialized" => new SampleReplica(this, kind, SampleReplica.Construction.Server, SampleReplica.Serialization.Server, clientDoesNotSerialize: true, ReplicaActionOnPopConnection.DeleteReplicaAndBroadcastDestruction),
            "P2PReplica" => new SampleReplica(this, kind, SampleReplica.Construction.PeerToPeer, SampleReplica.Serialization.PeerToPeer, clientDoesNotSerialize: false, ReplicaActionOnPopConnection.DeleteReplica),
            "User" => new SampleUser(this),
            _ => null,
        };
    }

    private sealed class SampleConnection(SampleReplicaManager manager, SystemAddress systemAddress, RakNetGuid rakNetGuid, bool groupDownloads)
        : ReplicaConnection(systemAddress, rakNetGuid)
    {
        public override bool QueryGroupDownloadMessages() => groupDownloads;

        public override Replica3? AllocReplica(ref BitStreamReader allocationIdBitstream, ReplicaManager3 replicaManager3) =>
            manager.NewReplica(ReadText(ref allocationIdBitstream));
    }

    // SampleReplica and its four client/server subclasses and P2PReplica.
    private sealed class SampleReplica : Replica3
    {
        private readonly SampleReplicaManager _manager;
        private readonly Construction _construction;
        private readonly Serialization _serialization;
        private readonly bool _clientDoesNotSerialize;
        private readonly ReplicaActionOnPopConnection _onPop;
        private readonly VariableDeltaSerializer _variableDeltaSerializer = new();

        public SampleReplica(SampleReplicaManager manager, string name, Construction construction, Serialization serialization, bool clientDoesNotSerialize, ReplicaActionOnPopConnection onPop)
        {
            _manager = manager;
            Name = name;
            _construction = construction;
            _serialization = serialization;
            _clientDoesNotSerialize = clientDoesNotSerialize;
            _onPop = onPop;
        }

        public enum Construction
        {
            Client,
            Server,
            PeerToPeer,
        }

        public enum Serialization
        {
            Client,
            Server,
            PeerToPeer,
        }

        public string Name { get; }

        public string ConstructionText { get; private set; } = "";

        public int Var1Unreliable;
        public int Var2Unreliable;
        public int Var3Reliable;
        public int Var4Reliable;

        private bool IsServer => _manager.Topology != SampleTopology.Client;

        public override void WriteAllocationId(ReplicaConnection destinationConnection, BitStream allocationIdBitstream) => WriteRakString(allocationIdBitstream, Name);

        public override ReplicaConstructionState QueryConstruction(ReplicaConnection destinationConnection, ReplicaManager3 replicaManager3) => _construction switch
        {
            Construction.Client => QueryConstructionForClientConstruction(destinationConnection, IsServer),
            Construction.Server => QueryConstructionForServerConstruction(destinationConnection, IsServer),
            _ => QueryConstructionForPeerToPeer(destinationConnection),
        };

        public override bool QueryRemoteConstruction(ReplicaConnection sourceConnection) => _construction switch
        {
            Construction.Client => QueryRemoteConstructionForClientConstruction(sourceConnection, IsServer),
            Construction.Server => QueryRemoteConstructionForServerConstruction(sourceConnection, IsServer),
            _ => QueryRemoteConstructionForPeerToPeer(sourceConnection),
        };

        public override ReplicaQuerySerializationResult QuerySerialization(ReplicaConnection destinationConnection) => _serialization switch
        {
            Serialization.Client => QuerySerializationForClientSerializable(destinationConnection, IsServer),
            Serialization.Server => QuerySerializationForServerSerializable(destinationConnection, IsServer),
            _ => QuerySerializationForPeerToPeer(destinationConnection),
        };

        public override ReplicaActionOnPopConnection QueryActionOnPopConnection(ReplicaConnection droppedConnection) => _onPop;

        // AddRemoteSystemVariableHistory does nothing in RakNet, and has no counterpart.
        public override void SerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection) =>
            WriteRakString(constructionBitstream, Name + " SerializeConstruction");

        public override bool DeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
        {
            ConstructionText = ReadText(ref constructionBitstream);
            return true;
        }

        public override void SerializeDestruction(BitStream destructionBitstream, ReplicaConnection destinationConnection)
        {
            _variableDeltaSerializer.RemoveRemoteSystemVariableHistory(destinationConnection.RakNetGuid);
            WriteRakString(destructionBitstream, Name + " SerializeDestruction");
        }

        public override bool DeserializeDestruction(ref BitStreamReader destructionBitstream, ReplicaConnection sourceConnection)
        {
            _manager.Destructions.Add(new ReplicaDestruction(NetworkId, Name, ReadText(ref destructionBitstream), DeletingSystemGuid.Value));
            return true;
        }

        public override void OnUserReplicaPreSerializeTick() => _variableDeltaSerializer.OnPreSerializeTick();

        public override ReplicaSerializationResult Serialize(SerializeParameters serializeParameters)
        {
            if (_clientDoesNotSerialize && !IsServer)
                return ReplicaSerializationResult.DoNotSerialize;
            var host = ReplicaManager!.Host!;
            var context = new VariableDeltaSerializationContext();

            serializeParameters.Pro[0].Reliability = Reliability.UnreliableWithAckReceipt;
            serializeParameters.Pro[0].SendReceipt = host.Peer!.ReserveReceipt().Value;
            serializeParameters.MessageTimestamp = host.GetTime();

            _variableDeltaSerializer.BeginUnreliableAckedSerialize(context, serializeParameters.DestinationConnection!.RakNetGuid, serializeParameters.OutputBitstream[0], serializeParameters.Pro[0].SendReceipt);
            _variableDeltaSerializer.SerializeVariable(context, Var1Unreliable);
            _variableDeltaSerializer.SerializeVariable(context, Var2Unreliable);
            _variableDeltaSerializer.EndSerialize(context);

            serializeParameters.Pro[1].Reliability = Reliability.ReliableOrdered;

            _variableDeltaSerializer.BeginIdenticalSerialize(context, serializeParameters.WhenLastSerialized == 0, serializeParameters.OutputBitstream[1]);
            _variableDeltaSerializer.SerializeVariable(context, Var3Reliable);
            _variableDeltaSerializer.SerializeVariable(context, Var4Reliable);
            _variableDeltaSerializer.EndSerialize(context);

            return ReplicaSerializationResult.SerializedAlways;
        }

        public override void Deserialize(DeserializeParameters deserializeParameters)
        {
            var unreliable = deserializeParameters.GetSerializationBitstream(0);
            VariableDeltaSerializer.DeserializeVariable(ref unreliable, ref Var1Unreliable);
            VariableDeltaSerializer.DeserializeVariable(ref unreliable, ref Var2Unreliable);
            var reliable = deserializeParameters.GetSerializationBitstream(1);
            VariableDeltaSerializer.DeserializeVariable(ref reliable, ref Var3Reliable);
            VariableDeltaSerializer.DeserializeVariable(ref reliable, ref Var4Reliable);
        }

        public override void OnPoppedConnection(ReplicaConnection droppedConnection) =>
            _variableDeltaSerializer.RemoveRemoteSystemVariableHistory(droppedConnection.RakNetGuid);

        public void NotifyReplicaOfMessageDeliveryStatus(RakNetGuid guid, uint receiptId, bool messageArrived) =>
            _variableDeltaSerializer.OnMessageReceipt(guid, receiptId, messageArrived);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _variableDeltaSerializer.Dispose();
            base.Dispose(disposing);
        }
    }

    // User of ReplicatedLogin, with runningAsServer from the topology.
    private sealed class SampleUser(SampleReplicaManager manager) : Replica3
    {
        public int Score { get; set; }

        public string Username { get; set; } = "";

        public RakNetGuid UserGuid { get; set; } = RakNetGuid.Unassigned;

        private bool RunningAsServer => manager.Topology == SampleTopology.Server;

        public override void WriteAllocationId(ReplicaConnection destinationConnection, BitStream allocationIdBitstream) => WriteRakString(allocationIdBitstream, "User");

        public override ReplicaConstructionState QueryConstruction(ReplicaConnection destinationConnection, ReplicaManager3 replicaManager3) =>
            QueryConstructionForServerConstruction(destinationConnection, RunningAsServer);

        public override bool QueryRemoteConstruction(ReplicaConnection sourceConnection) => QueryRemoteConstructionForServerConstruction(sourceConnection, RunningAsServer);

        public override void SerializeConstruction(BitStream constructionBitstream, ReplicaConnection destinationConnection) => SerializeToBitStream(constructionBitstream);

        public override bool DeserializeConstruction(ref BitStreamReader constructionBitstream, ReplicaConnection sourceConnection)
        {
            DeserializeToBitStream(ref constructionBitstream);
            return true;
        }

        public override void SerializeDestruction(BitStream destructionBitstream, ReplicaConnection destinationConnection)
        {
        }

        public override bool DeserializeDestruction(ref BitStreamReader destructionBitstream, ReplicaConnection sourceConnection)
        {
            manager.Destructions.Add(new ReplicaDestruction(NetworkId, "User", "", DeletingSystemGuid.Value));
            return true;
        }

        public override ReplicaActionOnPopConnection QueryActionOnPopConnection(ReplicaConnection droppedConnection) =>
            RunningAsServer ? QueryActionOnPopConnectionForServer(droppedConnection) : QueryActionOnPopConnectionForClient(droppedConnection);

        public override ReplicaQuerySerializationResult QuerySerialization(ReplicaConnection destinationConnection) =>
            QuerySerializationForServerSerializable(destinationConnection, RunningAsServer);

        public override ReplicaSerializationResult Serialize(SerializeParameters serializeParameters)
        {
            SerializeToBitStream(serializeParameters.OutputBitstream[0]);
            return ReplicaSerializationResult.BroadcastIdentically;
        }

        public override void Deserialize(DeserializeParameters deserializeParameters)
        {
            if (!deserializeParameters.BitstreamWrittenTo[0])
                return;
            var reader = deserializeParameters.GetSerializationBitstream(0);
            DeserializeToBitStream(ref reader);
        }

        private void SerializeToBitStream(BitStream bitStream)
        {
            bitStream.WriteInt32(Score);
            WriteRakString(bitStream, Username);
            bitStream.WriteRakNetGuid(UserGuid);
        }

        private void DeserializeToBitStream(ref BitStreamReader bitStream)
        {
            if (bitStream.TryReadInt32(out var score))
                Score = score;
            if (bitStream.TryReadRakString(out var username))
                Username = username;
            if (bitStream.TryReadRakNetGuid(out var guid))
                UserGuid = guid;
        }
    }

    // The sample's handling of ID_SND_RECEIPT_ACKED and ID_SND_RECEIPT_LOSS, as a plugin that passes them on.
    private sealed class ReceiptForwarder(SampleReplicaManager manager) : IRakPlugin
    {
        private readonly List<Replica3> _mine = [];

        PluginResult IRakPlugin.OnEvent(PeerEvent e)
        {
            var data = e.Data;
            if (data.Length >= 5 && (data[0] == (byte)MessageId.SndReceiptAcked || data[0] == (byte)MessageId.SndReceiptLoss))
            {
                var msgNumber = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]);
                manager.GetReplicasCreatedByMe(_mine);
                foreach (var replica in _mine)
                {
                    if (replica is SampleReplica sample)
                        sample.NotifyReplicaOfMessageDeliveryStatus(e.RakNetGuid, msgNumber, data[0] == (byte)MessageId.SndReceiptAcked);
                }
            }
            return PluginResult.Continue;
        }
    }
}
