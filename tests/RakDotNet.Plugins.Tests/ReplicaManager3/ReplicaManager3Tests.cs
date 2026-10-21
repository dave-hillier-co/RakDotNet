using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.ReplicaManager3;

public sealed class ReplicaManager3Tests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly TestManager _serverManager = new(isServer: true);
    private readonly TestManager _clientManager = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;

    public ReplicaManager3Tests()
    {
        _server = _mesh.Add(maxIncoming: 4, _serverManager);
        _client = _mesh.Add(maxIncoming: 4, _clientManager);
    }

    public void Dispose()
    {
        _mesh.Dispose();
        _serverManager.Dispose();
        _clientManager.Dispose();
    }

    [Fact]
    public void Each_system_sends_scope_change_to_validate_a_new_connection_and_answers_the_other_once()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // ReplicaManager3.cpp:2372-2379, 706-716: each side sends one on PushConnection and one in reply.
        Assert.Equal(2, _mesh.Seen(_client).Count(e => e.Id == (byte)MessageId.ReplicaManagerScopeChange));
        Assert.Equal([(byte)MessageId.ReplicaManagerScopeChange, 0], _mesh.Seen(_client).First(e => e.Id == (byte)MessageId.ReplicaManagerScopeChange).Data);
        Assert.True(_serverManager.GetConnectionByGuid(_client.RakNetGuid)!.IsValidated);
        Assert.True(_clientManager.GetConnectionByGuid(_server.RakNetGuid)!.IsValidated);
    }

    [Fact]
    public void A_referenced_replica_is_constructed_on_a_new_system_with_its_id_creator_and_construction_data()
    {
        var replica = _serverManager.Create("Soldier", value: 7);
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        var copy = Assert.Single(_clientManager.Replicas());
        Assert.Equal("Soldier", copy.Name);
        Assert.Equal(replica.NetworkId, copy.NetworkId);
        Assert.Equal(_server.RakNetGuid, copy.CreatingSystemGuid);
        Assert.Equal("Soldier SerializeConstruction", copy.ConstructionText);
        Assert.Same(_clientManager, copy.ReplicaManager);
        Assert.Same(copy, _clientManager.GetNetworkIdManager()!.GetObject<Replica3>(replica.NetworkId));
        // The serialization that follows a construction carries the value at once.
        Assert.Equal(7, copy.Value);
        var connection = (TestConnection)_clientManager.GetConnectionByGuid(_server.RakNetGuid)!;
        Assert.True(connection.DownloadWasCompleted);
        Assert.Equal(("started", "complete"), (connection.DownloadStartedText, connection.DownloadCompleteText));
        Assert.True(connection.HasReplicaConstructed(copy));
        Assert.True(_serverManager.GetConnectionByGuid(_client.RakNetGuid)!.HasReplicaConstructed(replica));
    }

    [Fact]
    public void A_construction_message_holds_offsets_to_each_end_the_id_the_creator_the_allocation_id_and_the_data()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // SendConstruction (ReplicaManager3.cpp:2189-2310), derived from the C++: the ID, world 0, one construction; its
        // end offset in bits, NetworkID, the create bit aligned, the creating GUID, the end of the allocation ID, the
        // allocation ID and the construction data as RakStrings; no post-construction data (one bit, aligned); no
        // destructions.
        var expected = new List<byte> { (byte)MessageId.ReplicaManagerConstruction, 0, 0, 1 };
        expected.AddRange(BigEndian(456u));
        expected.AddRange(BigEndian(replica.NetworkId));
        expected.Add(0x80);
        expected.AddRange(BigEndian(_server.RakNetGuid.Value));
        expected.AddRange(BigEndian(256u));
        expected.AddRange(RakString("A"));
        expected.AddRange(RakString("A SerializeConstruction"));
        expected.AddRange([0, 0, 0]);
        Assert.Equal(expected, _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.ReplicaManagerConstruction).Data);
        // ID_REPLICA_MANAGER_DOWNLOAD_STARTED and _COMPLETE: the ID, the world and what the connection wrote.
        Assert.Equal([(byte)MessageId.ReplicaManagerDownloadStarted, 0, .. RakString("started")], _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.ReplicaManagerDownloadStarted).Data);
        Assert.Equal([(byte)MessageId.ReplicaManagerDownloadComplete, 0, .. RakString("complete")], _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.ReplicaManagerDownloadComplete).Data);
    }

    [Fact]
    public void A_serialization_message_holds_the_id_and_per_channel_a_written_bit_then_the_compressed_bit_count_and_aligned_data()
    {
        var replica = _serverManager.Create("A", value: 0x01020304);
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // SendSerialize (ReplicaManager3.cpp:1517-1609): channel 0 written, its bit count 32 compressed as RakNet does on
        // a little-endian host (a 0 bit then all four bytes), aligned, the data; then 15 unwritten channels.
        List<byte> expected = [(byte)MessageId.ReplicaManagerSerialize, 0, .. BigEndian(replica.NetworkId), 0x80, 0, 0, 0x08, 0, 1, 2, 3, 4, 0, 0];
        Assert.Equal(expected, _mesh.Seen(_client).First(e => e.Id == (byte)MessageId.ReplicaManagerSerialize).Data);
    }

    [Fact]
    public void Download_messages_arrive_started_construction_serialization_complete_and_the_first_update_serializes_again()
    {
        _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // The serialization after a construction does not record what it sent (ReplicaManager3.cpp:2339-2352), so the
        // first update tick compares with nothing and sends the same state again.
        byte[] order = [.. _mesh.Seen(_client).Select(e => e.Id).Where(id => id is >= (byte)MessageId.ReplicaManagerConstruction and <= (byte)MessageId.ReplicaManagerDownloadComplete and not (byte)MessageId.ReplicaManagerScopeChange)];
        Assert.Equal([(byte)MessageId.ReplicaManagerDownloadStarted, (byte)MessageId.ReplicaManagerConstruction, (byte)MessageId.ReplicaManagerSerialize, (byte)MessageId.ReplicaManagerDownloadComplete, (byte)MessageId.ReplicaManagerSerialize], order);
    }

    [Fact]
    public void A_change_is_serialized_at_the_next_interval_and_an_unchanged_value_is_not_sent_again()
    {
        var replica = _serverManager.Create("A", value: 1);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());
        var count = copy.DeserializeCount;

        _mesh.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(count, copy.DeserializeCount);

        replica.Value = 2;
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, copy.Value);
        Assert.Equal(count + 1, copy.DeserializeCount);
    }

    [Fact]
    public void Serialization_waits_for_the_auto_serialize_interval_and_an_infinite_interval_never_serializes()
    {
        var replica = _serverManager.Create("A", value: 1);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());

        _serverManager.AutoSerializeInterval = Timeout.InfiniteTimeSpan;
        replica.Value = 2;
        _mesh.Run(TimeSpan.FromMilliseconds(300));
        Assert.Equal(1, copy.Value);

        // A second after the last serialization, about 50 ms in.
        _serverManager.AutoSerializeInterval = TimeSpan.FromSeconds(1);
        Assert.Equal(TimeSpan.FromSeconds(1), _serverManager.AutoSerializeInterval);
        _mesh.Run(TimeSpan.FromMilliseconds(600));
        Assert.Equal(1, copy.Value);
        _mesh.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal(2, copy.Value);
    }

    [Fact]
    public void Channels_with_different_send_parameters_go_in_separate_messages_each_marking_the_other_channels_unwritten()
    {
        var replica = _serverManager.Create("A", value: 5);
        replica.OnSerialize = sp =>
        {
            sp.OutputBitstream[1].WriteByte(9);
            sp.Pro[1].OrderingChannel = 3;
        };
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // ReplicaManager3.cpp:1549-1584: channel 0 then fifteen unwritten; then the header again, channel 0 unwritten,
        // channel 1 and fourteen unwritten.
        var serializations = _mesh.Seen(_client).Where(e => e.Id == (byte)MessageId.ReplicaManagerSerialize).Select(e => e.Data[10..]).Take(2).ToList();
        Assert.Equal([0x80, 0, 0, 0x08, 0, 0, 0, 0, 5, 0, 0], serializations[0]);
        Assert.Equal([0x40, 0, 0, 0x01, 0, 9, 0, 0], serializations[1]);
    }

    [Fact]
    public void A_message_timestamp_sends_the_serialization_after_ID_TIMESTAMP_and_reaches_deserialize()
    {
        var replica = _serverManager.Create("A", value: 5);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());

        replica.OnSerialize = sp => sp.MessageTimestamp = 1234;
        replica.Value = 6;
        _mesh.Run(TimeSpan.FromMilliseconds(100));

        var timestamped = _mesh.Seen(_client).Last(e => e.Id == (byte)MessageId.Timestamp);
        Assert.Equal((byte)MessageId.ReplicaManagerSerialize, timestamped.Data[9]);
        Assert.Equal(6, copy.Value);
        // The time is shifted by the clock differential, near 0 here because both peers share one clock.
        Assert.InRange(copy.LastTimestamp, 1230ul, 1238ul);
    }

    [Fact]
    public void Broadcast_destruction_destroys_the_replica_on_systems_that_have_it_with_the_destruction_data()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());

        replica.BroadcastDestruction();
        replica.Dispose();
        _mesh.Settle();

        Assert.Empty(_clientManager.Replicas());
        Assert.Equal("A SerializeDestruction", copy.DestructionText);
        Assert.Equal(_server.RakNetGuid, copy.DeletingSystemGuid);
        Assert.True(copy.Deallocated);
        Assert.Null(_clientManager.GetNetworkIdManager()!.GetObject<Replica3>(replica.NetworkId));
        Assert.Equal(["pre destruct A", "dealloc A"], _clientManager.Log.SkipWhile(l => l != "destruct A").Skip(1));
        Assert.Empty(_serverManager.Replicas());
    }

    [Fact]
    public void A_destruction_message_is_a_construction_message_with_no_constructions_and_each_replica_s_id_end_offset_and_deleter()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        replica.BroadcastDestruction();
        _mesh.Settle();

        // BroadcastDestructionList (ReplicaManager3.cpp:1006-1077): no constructions, one destruction: the NetworkID,
        // the end offset, the deleting GUID and the destruction data.
        List<byte> expected = [(byte)MessageId.ReplicaManagerConstruction, 0, 0, 0, 0, 1, .. BigEndian(replica.NetworkId), .. BigEndian(400u),
                               .. BigEndian(_server.RakNetGuid.Value), .. RakString("A SerializeDestruction")];
        Assert.Equal(expected, _mesh.Seen(_client).Last(e => e.Id == (byte)MessageId.ReplicaManagerConstruction).Data);
    }

    [Fact]
    public void A_rejected_destruction_keeps_the_replica_but_forgets_that_the_sender_has_it()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());
        copy.AcceptDestruction = false;

        replica.BroadcastDestruction();
        _mesh.Settle();

        Assert.Same(copy, Assert.Single(_clientManager.Replicas()));
        Assert.False(_clientManager.GetConnectionByGuid(_server.RakNetGuid)!.HasReplicaConstructed(copy));
        Assert.Contains("pre destruct A", _clientManager.Log);
        Assert.False(copy.Deallocated);
    }

    [Fact]
    public void A_client_s_construction_is_rejected_by_a_server_that_only_constructs_itself()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var replica = _clientManager.Create("A");
        // QueryConstruction_ServerConstruction on the client never sends it; force it to test the server's side.
        replica.Construction = _ => ReplicaConstructionState.SendConstruction;
        _mesh.Settle();

        // QueryRemoteConstruction_ServerConstruction: the server refuses, deallocating what it allocated.
        Assert.Empty(_serverManager.Replicas());
        Assert.Equal(["alloc A", "dealloc A"], _serverManager.Log.Where(l => l.EndsWith(" A", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_replica_the_connection_cannot_allocate_is_skipped_and_the_rest_of_the_message_is_read()
    {
        _serverManager.Create("Refused");
        _serverManager.Create("B");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        Assert.Equal("B", Assert.Single(_clientManager.Replicas()).Name);
    }

    [Fact]
    public void A_client_deletes_the_server_s_replicas_when_it_loses_the_server()
    {
        _serverManager.Create("A");
        _serverManager.Create("B");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Equal(2, _clientManager.Replicas().Count);

        _client.Close(_server.RakNetGuid, notify: true);
        _mesh.Settle();

        // QueryActionOnPopConnection_Client: RM3AOPC_DELETE_REPLICA, after PreDestruction.
        Assert.Empty(_clientManager.Replicas());
        Assert.Equal(["pre destruct A", "dealloc A", "pre destruct B", "dealloc B"], _clientManager.Log.Where(l => l.StartsWith("pre", StringComparison.Ordinal) || l.StartsWith("dealloc", StringComparison.Ordinal)));
        Assert.Equal(0, _clientManager.GetConnectionCount());
        Assert.Single(_clientManager.Deallocated);
        Assert.Equal(2, _serverManager.GetReplicaCount());
    }

    [Fact]
    public void A_server_deletes_a_lost_client_s_replicas_and_tells_the_other_clients()
    {
        using var second = new TestManager();
        var secondPeer = _mesh.Add(maxIncoming: 4, second);
        _serverManager.Configure = ClientConstructionOnServer;
        _mesh.Connect(_client, _server);
        _mesh.Connect(secondPeer, _server);
        _mesh.Settle();
        var replica = _clientManager.Create("Avatar");
        replica.Construction = c => replica.QueryConstructionForClientConstruction(c, isThisTheServer: false);
        _mesh.Settle();
        Assert.Equal("Avatar", Assert.Single(second.Replicas()).Name);
        Assert.Single(_serverManager.Replicas());

        _client.Close(_server.RakNetGuid, notify: true);
        _mesh.Settle();

        // QueryActionOnPopConnection_Server: RM3AOPC_DELETE_REPLICA_AND_BROADCAST_DESTRUCTION.
        Assert.Empty(_serverManager.Replicas());
        Assert.Empty(second.Replicas());
    }

    [Fact]
    public void A_server_relays_a_client_s_replica_to_the_other_clients_and_a_destruction_received_to_them_too()
    {
        using var second = new TestManager();
        var secondPeer = _mesh.Add(maxIncoming: 4, second);
        _serverManager.Configure = ClientConstructionOnServer;
        _mesh.Connect(_client, _server);
        _mesh.Connect(secondPeer, _server);
        _mesh.Settle();

        var replica = _clientManager.Create("Avatar", value: 4);
        replica.Construction = c => replica.QueryConstructionForClientConstruction(c, isThisTheServer: false);
        replica.Serialization = c => replica.QuerySerializationForClientSerializable(c, isThisTheServer: false);
        _mesh.Settle();
        var onServer = Assert.Single(_serverManager.Replicas());
        var relayed = Assert.Single(second.Replicas());
        Assert.Equal(replica.NetworkId, relayed.NetworkId);
        Assert.Equal(_client.RakNetGuid, relayed.CreatingSystemGuid);
        Assert.Equal((4, 4), (onServer.Value, relayed.Value));

        replica.Value = 8;
        _mesh.Run(TimeSpan.FromMilliseconds(200));
        Assert.Equal((8, 8), (onServer.Value, relayed.Value));

        // The destruction reaches the server, which relays it (QueryRelayDestruction is true by default).
        replica.BroadcastDestruction();
        replica.Dispose();
        _mesh.Settle();
        Assert.Empty(_serverManager.Replicas());
        Assert.Empty(second.Replicas());
        Assert.Equal(_client.RakNetGuid, relayed.DeletingSystemGuid);
    }

    [Fact]
    public void Grouped_download_messages_are_held_until_the_download_completes_then_returned_in_order()
    {
        _clientManager.GroupDownloads = true;
        _serverManager.Create("A");
        _serverManager.Create("B");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        Assert.Equal(["download started", "alloc A", "construct A", "alloc B", "construct B", "post construct A", "post construct B", "deserialize A 0", "deserialize B 0", "download complete", "deserialize A 0", "deserialize B 0"], _clientManager.Log);
        byte[] order = [.. _mesh.Seen(_client).Select(e => e.Id).Where(id => id is (byte)MessageId.ReplicaManagerDownloadStarted or (byte)MessageId.ReplicaManagerConstruction or (byte)MessageId.ReplicaManagerSerialize or (byte)MessageId.ReplicaManagerDownloadComplete)];
        Assert.Equal([(byte)MessageId.ReplicaManagerDownloadStarted, (byte)MessageId.ReplicaManagerConstruction, (byte)MessageId.ReplicaManagerSerialize,
                      (byte)MessageId.ReplicaManagerSerialize, (byte)MessageId.ReplicaManagerDownloadComplete,
                      (byte)MessageId.ReplicaManagerSerialize, (byte)MessageId.ReplicaManagerSerialize], order);
    }

    [Fact]
    public void A_construction_queued_behind_a_grouped_download_s_completion_is_held_and_never_returned()
    {
        _clientManager.GroupDownloads = true;
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var server = _client.Connections.Single();

        // ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE pushes the held messages and itself back behind what is already queued
        // (ReplicaManager3.cpp:960-972), with the group still open, so a construction queued meanwhile is held; the replayed
        // download then closes the group and nothing releases it. Native RakNet loses it the same way.
        _client.Inject([(byte)MessageId.ReplicaManagerDownloadStarted, 0], server.Address, server.RakNetGuid);
        _client.Inject(Construction(1001, "A"), server.Address, server.RakNetGuid);
        _client.Inject([(byte)MessageId.ReplicaManagerDownloadComplete, 0], server.Address, server.RakNetGuid);
        _client.Inject(Construction(1002, "B"), server.Address, server.RakNetGuid);
        _mesh.Settle();

        Assert.Equal(["A"], _clientManager.Replicas().Select(r => r.Name));
        Assert.True(_clientManager.GetConnectionByGuid(_server.RakNetGuid)!.DownloadWasCompleted);
        Assert.DoesNotContain("alloc B", _clientManager.Log);
    }

    [Fact]
    public void Post_construction_data_is_read_once_every_replica_of_the_message_exists()
    {
        var a = _serverManager.Create("A");
        a.PostConstructionData = "after A";
        _serverManager.Create("B");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        Assert.Equal(["construct A", "construct B", "post construct A", "post construct B"], _clientManager.Log.Where(l => l.Contains("construct", StringComparison.Ordinal)));
        Assert.Equal(["after A", ""], _clientManager.Replicas().Select(r => r.PostConstructionText));
    }

    [Fact]
    public void A_replica_that_already_exists_remotely_is_sent_its_existing_construction_data_and_serialized()
    {
        // R3P2PM_STATIC_OBJECT_CURRENTLY_AUTHORITATIVE on the owner, NOT_CURRENTLY on the other; both made the object.
        var owner = _serverManager.Create("Level", value: 3);
        owner.Construction = c => owner.QueryConstructionForPeerToPeer(c, ReplicaP2PMode.StaticObjectCurrentlyAuthoritative);
        owner.Serialization = c => owner.QuerySerializationForPeerToPeer(c, ReplicaP2PMode.StaticObjectCurrentlyAuthoritative);
        var local = new TestReplica(_clientManager, "Level") { NetworkId = owner.NetworkId };
        local.Construction = c => local.QueryConstructionForPeerToPeer(c, ReplicaP2PMode.StaticObjectNotCurrentlyAuthoritative);
        local.Serialization = c => local.QuerySerializationForPeerToPeer(c, ReplicaP2PMode.StaticObjectNotCurrentlyAuthoritative);
        _clientManager.Reference(local);
        Assert.Equal(owner.NetworkId, local.NetworkId);

        _mesh.Connect(_client, _server);
        _mesh.Settle();

        Assert.Equal("Level SerializeConstructionExisting", local.ConstructionText);
        Assert.Equal(3, local.Value);
        Assert.Same(local, Assert.Single(_clientManager.Replicas()));
        Assert.DoesNotContain("alloc Level", _clientManager.Log);
        Assert.True(_serverManager.GetConnectionByGuid(_client.RakNetGuid)!.HasReplicaConstructed(owner));
    }

    [Fact]
    public void Query_destruction_sends_the_destruction_and_queries_construction_again()
    {
        var replica = _serverManager.Create("A");
        var destroy = false;
        replica.Destruction = _ => destroy ? ReplicaDestructionState.SendDestruction : ReplicaDestructionState.NoAction;
        var construct = true;
        replica.Construction = _ => construct ? ReplicaConstructionState.SendConstruction : ReplicaConstructionState.NoAction;
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Single(_clientManager.Replicas());

        construct = false;
        destroy = true;
        _mesh.Settle();
        Assert.Empty(_clientManager.Replicas());
        Assert.False(_serverManager.GetConnectionByGuid(_client.RakNetGuid)!.HasReplicaConstructed(replica));

        destroy = false;
        construct = true;
        _mesh.Settle();
        Assert.Single(_clientManager.Replicas());
    }

    [Fact]
    public void A_connection_that_queries_its_replica_list_is_sent_the_replicas_it_names()
    {
        _serverManager.Mode = ReplicaConstructionMode.QueryConnectionForReplicaList;
        var a = _serverManager.Create("A");
        _serverManager.Create("B");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Empty(_clientManager.Replicas());

        _serverManager.ReplicasToCreate.Add(a);
        _mesh.Settle();
        Assert.Equal("A", Assert.Single(_clientManager.Replicas()).Name);

        // Naming it to destroy forgets it for this connection and sends its destruction.
        _serverManager.ReplicasToDestroy.Add(a);
        _mesh.Settle();
        Assert.False(_serverManager.GetConnectionByGuid(_client.RakNetGuid)!.HasReplicaConstructed(a));
        Assert.Empty(_clientManager.Replicas());
    }

    [Fact]
    public void Destructions_sent_with_constructions_are_lost_because_RakNet_reads_their_count_unaligned()
    {
        var kept = _serverManager.Create("Kept");
        var destroy = false;
        kept.Destruction = _ => destroy ? ReplicaDestructionState.SendDestruction : ReplicaDestructionState.NoAction;
        kept.Construction = _ => destroy ? ReplicaConstructionState.NoAction : ReplicaConstructionState.SendConstruction;
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Single(_clientManager.Replicas());

        // One update constructs New and destroys Kept, in one message.
        destroy = true;
        _serverManager.Create("New");
        _mesh.Settle();

        var message = _mesh.Seen(_client).Last(e => e.Id == (byte)MessageId.ReplicaManagerConstruction).Data;
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2)));
        Assert.Equal(["Kept", "New"], _clientManager.Replicas().Select(r => r.Name));
        Assert.DoesNotContain("destruct Kept", _clientManager.Log);
    }

    [Fact]
    public void Serialize_never_for_this_connection_stops_serializing_the_replica_to_it()
    {
        var replica = _serverManager.Create("A", value: 1);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());

        replica.Result = ReplicaSerializationResult.NeverSerializeForThisConnection;
        replica.Value = 2;
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        replica.Result = ReplicaSerializationResult.BroadcastIdentically;
        replica.Value = 3;
        _mesh.Run(TimeSpan.FromMilliseconds(100));

        Assert.Equal(1, copy.Value);
    }

    [Fact]
    public void Serialized_uniquely_compares_with_what_was_last_sent_to_each_system()
    {
        var second = new TestManager();
        var secondPeer = _mesh.Add(maxIncoming: 4, second);
        var replica = _serverManager.Create("A", value: 1);
        replica.Result = ReplicaSerializationResult.SerializedUniquely;
        _mesh.Connect(_client, _server);
        _mesh.Connect(secondPeer, _server);
        _mesh.Settle();
        var first = Assert.Single(_clientManager.Replicas());
        var other = Assert.Single(second.Replicas());

        // A uniquely serialized replica is not sent after its construction (ReplicaManager3.cpp:2340-2345), but at the next
        // update to each system.
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal((1, 1), (first.Value, other.Value));
        var counts = (first.DeserializeCount, other.DeserializeCount);
        _mesh.Run(TimeSpan.FromMilliseconds(100));
        Assert.Equal(counts, (first.DeserializeCount, other.DeserializeCount));
        second.Dispose();
    }

    [Fact]
    public void Serialized_always_sends_every_tick_even_unchanged()
    {
        var replica = _serverManager.Create("A", value: 1);
        replica.Result = ReplicaSerializationResult.SerializedAlways;
        _serverManager.AutoSerializeInterval = TimeSpan.FromMilliseconds(10);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());
        var count = copy.DeserializeCount;

        _mesh.Run(TimeSpan.FromMilliseconds(100));

        Assert.InRange(copy.DeserializeCount - count, 9, 11);
    }

    [Fact]
    public void Clear_forgets_every_replica_which_loses_its_manager_and_network_id()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        _serverManager.Clear();

        Assert.Equal(0, _serverManager.GetReplicaCount());
        Assert.Equal(0, _serverManager.GetConnectionCount());
        Assert.Null(replica.ReplicaManager);
        Assert.Null(replica.Manager);
        Assert.Equal(Protocol.Wire.Messages.RakNetConstants.UnassignedNetworkId, replica.NetworkId);
    }

    [Fact]
    public void Worlds_are_added_and_removed_and_world_zero_exists_from_the_start()
    {
        Assert.Equal(1, _serverManager.WorldCount);
        _serverManager.AddWorld(3);
        _serverManager.AddWorld(5);
        Assert.Throws<InvalidOperationException>(() => _serverManager.AddWorld(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => _serverManager.AddWorld(255));

        _serverManager.RemoveWorld(0);

        // RemoveAtIndexFast: the last world takes the removed one's place.
        Assert.Equal([5, 3], Enumerable.Range(0, _serverManager.WorldCount).Select(i => (int)_serverManager.GetWorldIdAtIndex(i)));
        Assert.Throws<InvalidOperationException>(() => _serverManager.GetReplicaCount(0));
    }

    [Fact]
    public void A_replica_in_another_world_is_replicated_with_that_world_s_id()
    {
        _serverManager.AddWorld(2);
        _clientManager.AddWorld(2);
        _serverManager.SetNetworkIdManager(new NetworkIdManager(), 2);
        _clientManager.SetNetworkIdManager(new NetworkIdManager(), 2);
        _serverManager.AutoCreateConnections = false;
        _clientManager.AutoCreateConnections = false;
        _mesh.Connect(_client, _server);
        Assert.True(_serverManager.PushConnection(_serverManager.AllocConnection(_server.Connections.Single().Address, _client.RakNetGuid)!, 2));
        Assert.True(_clientManager.PushConnection(_clientManager.AllocConnection(_client.Connections.Single().Address, _server.RakNetGuid)!, 2));
        var replica = new TestReplica(_serverManager, "W");
        _serverManager.Reference(replica, 2);
        _mesh.Settle();

        var list = new List<Replica3>();
        _clientManager.GetReferencedReplicaList(list, 2);
        Assert.Equal(replica.NetworkId, Assert.Single(list).NetworkId);
        Assert.Equal(0, _clientManager.GetReplicaCount());
        Assert.Equal(2, _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.ReplicaManagerConstruction).Data[1]);
    }

    [Fact]
    public void Without_auto_create_no_connection_takes_part_until_pushed_and_a_second_push_is_refused()
    {
        _serverManager.SetAutoManageConnections(autoCreate: false, autoDestroy: true);
        _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Empty(_clientManager.Replicas());

        var connections = new List<ReplicaConnection>();
        _serverManager.AutoCreateConnectionList([_client.RakNetGuid], connections);
        _serverManager.AutoCreateConnectionList([_client.RakNetGuid], connections);
        _mesh.Settle();

        Assert.Single(connections);
        Assert.False(_serverManager.PushConnection(new TestConnection(_serverManager, default, _client.RakNetGuid)));
        Assert.Single(_clientManager.Replicas());
    }

    [Fact]
    public void Query_serialization_list_serializes_only_the_replicas_it_names()
    {
        var a = _serverManager.Create("A", value: 1);
        var b = _serverManager.Create("B", value: 1);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copies = _clientManager.Replicas();

        _serverManager.SerializationList = [b];
        a.Value = 2;
        b.Value = 2;
        _mesh.Run(TimeSpan.FromMilliseconds(100));

        Assert.Equal([1, 2], copies.Select(c => c.Value));
    }

    [Fact]
    public void Topology_defaults_follow_ReplicaManager3_cpp()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var toClient = _serverManager.GetConnectionByGuid(_client.RakNetGuid)!;
        var mine = _serverManager.Create("Mine");
        var theirs = new TestReplica(_serverManager, "Theirs") { CreatingSystemGuid = _client.RakNetGuid };
        _serverManager.Reference(theirs);
        var third = new TestReplica(_serverManager, "Third") { CreatingSystemGuid = new RakNetGuid(12345) };
        _serverManager.Reference(third);

        Assert.Equal(ReplicaConstructionState.SendConstruction, mine.QueryConstructionForClientConstruction(toClient, isThisTheServer: false));
        Assert.Equal(ReplicaConstructionState.SendConstruction, theirs.QueryConstructionForClientConstruction(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaConstructionState.NeverConstruct, theirs.QueryConstructionForClientConstruction(toClient, isThisTheServer: false));
        Assert.True(theirs.QueryRemoteConstructionForClientConstruction(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaConstructionState.SendConstruction, theirs.QueryConstructionForServerConstruction(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaConstructionState.NeverConstruct, mine.QueryConstructionForServerConstruction(toClient, isThisTheServer: false));
        Assert.False(mine.QueryRemoteConstructionForServerConstruction(toClient, isThisTheServer: true));
        Assert.True(mine.QueryRemoteConstructionForServerConstruction(toClient, isThisTheServer: false));

        Assert.Equal(ReplicaConstructionState.SendConstruction, mine.QueryConstructionForPeerToPeer(toClient));
        Assert.Equal(ReplicaConstructionState.NeverConstruct, theirs.QueryConstructionForPeerToPeer(toClient));
        Assert.Equal(ReplicaConstructionState.SendConstruction, theirs.QueryConstructionForPeerToPeer(toClient, ReplicaP2PMode.MultiOwnerCurrentlyAuthoritative));
        Assert.Equal(ReplicaConstructionState.AlreadyExistsRemotely, theirs.QueryConstructionForPeerToPeer(toClient, ReplicaP2PMode.MultiOwnerNotCurrentlyAuthoritative));
        Assert.Equal(ReplicaConstructionState.AlreadyExistsRemotely, theirs.QueryConstructionForPeerToPeer(toClient, ReplicaP2PMode.StaticObjectCurrentlyAuthoritative));
        Assert.Equal(ReplicaConstructionState.AlreadyExistsRemotelyDoNotConstruct, theirs.QueryConstructionForPeerToPeer(toClient, ReplicaP2PMode.StaticObjectNotCurrentlyAuthoritative));
        Assert.True(theirs.QueryRemoteConstructionForPeerToPeer(toClient));

        // ClientSerializable: the creator sends; the server relays to every system but the creator.
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, mine.QuerySerializationForClientSerializable(toClient, isThisTheServer: false));
        Assert.Equal(ReplicaQuerySerializationResult.NeverCallSerialize, theirs.QuerySerializationForClientSerializable(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, third.QuerySerializationForClientSerializable(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaQuerySerializationResult.NeverCallSerialize, third.QuerySerializationForClientSerializable(toClient, isThisTheServer: false));
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, theirs.QuerySerializationForServerSerializable(toClient, isThisTheServer: true));
        Assert.Equal(ReplicaQuerySerializationResult.NeverCallSerialize, mine.QuerySerializationForServerSerializable(toClient, isThisTheServer: false));
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, mine.QuerySerializationForPeerToPeer(toClient));
        Assert.Equal(ReplicaQuerySerializationResult.NeverCallSerialize, theirs.QuerySerializationForPeerToPeer(toClient));
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, theirs.QuerySerializationForPeerToPeer(toClient, ReplicaP2PMode.MultiOwnerCurrentlyAuthoritative));
        Assert.Equal(ReplicaQuerySerializationResult.DoNotCallSerialize, theirs.QuerySerializationForPeerToPeer(toClient, ReplicaP2PMode.MultiOwnerNotCurrentlyAuthoritative));
        Assert.Equal(ReplicaQuerySerializationResult.CallSerialize, theirs.QuerySerializationForPeerToPeer(toClient, ReplicaP2PMode.StaticObjectCurrentlyAuthoritative));
        Assert.Equal(ReplicaQuerySerializationResult.DoNotCallSerialize, theirs.QuerySerializationForPeerToPeer(toClient, ReplicaP2PMode.StaticObjectNotCurrentlyAuthoritative));

        Assert.Equal(ReplicaActionOnPopConnection.DeleteReplica, mine.QueryActionOnPopConnectionForClient(toClient));
        Assert.Equal(ReplicaActionOnPopConnection.DeleteReplicaAndBroadcastDestruction, mine.QueryActionOnPopConnectionForServer(toClient));
        Assert.Equal(ReplicaActionOnPopConnection.DeleteReplica, mine.QueryActionOnPopConnectionForPeerToPeer(toClient));
    }

    [Fact]
    public void Reference_needs_an_attached_manager_with_a_network_id_manager()
    {
        using var detached = new TestManager();
        Assert.Throws<InvalidOperationException>(() => detached.Create("A"));
        _serverManager.SetNetworkIdManager(null);
        Assert.Throws<InvalidOperationException>(() => _serverManager.Create("A"));
    }

    // One construction of a TestReplica named name, as SendConstruction writes it with no construction data.
    private byte[] Construction(ulong networkId, string name)
    {
        var allocationEnd = 232 + (8 * (2 + name.Length));
        return [(byte)MessageId.ReplicaManagerConstruction, 0, 0, 1, .. BigEndian((uint)allocationEnd), .. BigEndian(networkId), 0x80,
                .. BigEndian(_server.RakNetGuid.Value), .. BigEndian((uint)allocationEnd), .. RakString(name), 0, 0, 0];
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] BigEndian(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        return bytes;
    }

    // RakString::Serialize: a u16 length then the bytes.
    private static byte[] RakString(string text) => [.. BigEndian16((ushort)text.Length), .. System.Text.Encoding.ASCII.GetBytes(text)];

    private static byte[] BigEndian16(ushort value) => [(byte)(value >> 8), (byte)value];

    // The server's copies of client-created replicas: accepted, sent to every system, relayed between clients.
    private static void ClientConstructionOnServer(TestReplica replica)
    {
        replica.RemoteConstruction = c => replica.QueryRemoteConstructionForClientConstruction(c, isThisTheServer: true);
        replica.Construction = c => replica.QueryConstructionForClientConstruction(c, isThisTheServer: true);
        replica.Serialization = c => replica.QuerySerializationForClientSerializable(c, isThisTheServer: true);
    }
}
