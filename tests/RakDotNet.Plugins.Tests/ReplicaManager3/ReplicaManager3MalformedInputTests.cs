using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.ReplicaManager3;

// Messages a remote system could send that SendConstruction and SendSerialize never write: truncated, with offsets that
// point backwards or past the end, or with counts the message cannot hold.
public sealed class ReplicaManager3MalformedInputTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly TestManager _serverManager = new(isServer: true);
    private readonly TestManager _clientManager = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;

    public ReplicaManager3MalformedInputTests()
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
    public void A_construction_entry_whose_end_points_back_into_its_own_header_is_not_read_again()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // 50 entries announced, one written: an existing replica whose end offset is its own start, bit 32. RakNet seeks
        // back and reads the same entry for every count.
        FromServer([(byte)MessageId.ReplicaManagerConstruction, 0, 0, 50, .. BigEndian(32u), .. BigEndian(replica.NetworkId), 0x00]);
        _mesh.Settle();

        Assert.DoesNotContain("construct existing A", _clientManager.Log);
    }

    [Fact]
    public void A_construction_of_a_replica_the_connection_has_already_does_not_serialize_it_to_that_system_twice()
    {
        // In QUERY_CONNECTION_FOR_REPLICA_LIST each construction of an existing replica made RakNet list it for
        // serialization again, though the constructed list refused it (ReplicaManager3.cpp:1948-1958).
        _clientManager.Mode = ReplicaConstructionMode.QueryConnectionForReplicaList;
        _clientManager.Configure = r =>
        {
            r.Serialization = _ => ReplicaQuerySerializationResult.CallSerialize;
            r.Result = ReplicaSerializationResult.SerializedAlways;
        };
        _clientManager.AutoSerializeInterval = TimeSpan.FromMilliseconds(10);
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        Assert.Single(_clientManager.Replicas());

        FromServer([(byte)MessageId.ReplicaManagerConstruction, 0, 0, 3,
                    .. ExistingEntry(32, replica.NetworkId), .. ExistingEntry(232, replica.NetworkId), .. ExistingEntry(432, replica.NetworkId), 0, 0, 0]);
        _mesh.Settle();
        var before = SerializationsAtServer();
        _mesh.Run(TimeSpan.FromMilliseconds(100));

        Assert.InRange(SerializationsAtServer() - before, 9, 11);
    }

    [Fact]
    public void A_construction_entry_whose_end_lies_past_the_message_is_not_constructed()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var message = Construction(1001, "A");
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(4), 100_000);

        FromServer(message);
        _mesh.Settle();

        Assert.Empty(_clientManager.Replicas());
        Assert.DoesNotContain("alloc A", _clientManager.Log);
    }

    [Fact]
    public void A_truncated_construction_is_ignored_and_the_next_is_read()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        FromServer(Construction(1001, "A")[..10]);
        FromServer(Construction(1002, "B"));
        _mesh.Settle();

        Assert.Equal(["B"], _clientManager.Replicas().Select(r => r.Name));
        Assert.DoesNotContain("alloc A", _clientManager.Log);
    }

    [Fact]
    public void A_construction_count_larger_than_the_message_stops_after_the_entries_it_holds()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var message = Construction(1001, "A");
        message[2] = 0xFF;
        message[3] = 0xFF;

        FromServer(message);
        _mesh.Settle();

        // The entry read is constructed and referenced; reading stops at the second, before post-construction, so the
        // sender is not recorded as having it.
        var copy = Assert.Single(_clientManager.Replicas());
        Assert.Contains("construct A", _clientManager.Log);
        Assert.DoesNotContain("post construct A", _clientManager.Log);
        Assert.False(_clientManager.GetConnectionByGuid(_server.RakNetGuid)!.HasReplicaConstructed(copy));
    }

    [Fact]
    public void A_destruction_entry_whose_end_points_back_before_its_header_stops_the_message()
    {
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());
        copy.AcceptDestruction = false;

        // Three destructions announced: A with empty destruction data, refused so it stays, then an unknown NetworkID, skipped by its end offset,
        // which points back at A's entry, bit 48. RakNet reads A's destruction again as the third.
        FromServer([(byte)MessageId.ReplicaManagerConstruction, 0, 0, 0, 0, 3,
                    .. BigEndian(replica.NetworkId), .. BigEndian(224u), .. BigEndian(_server.RakNetGuid.Value), .. RakString(""),
                    .. BigEndian(999ul), .. BigEndian(48u)]);
        _mesh.Settle();

        Assert.Single(_clientManager.Log, l => l == "destruct A");
    }

    [Fact]
    public void A_serialization_too_short_for_its_network_id_or_its_bit_count_changes_nothing()
    {
        var replica = _serverManager.Create("A", value: 5);
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var copy = Assert.Single(_clientManager.Replicas());

        FromServer([(byte)MessageId.ReplicaManagerSerialize, 0, .. BigEndian(replica.NetworkId)[..5]]);
        // Channel 0 written, then the message ends inside its compressed bit count.
        FromServer([(byte)MessageId.ReplicaManagerSerialize, 0, .. BigEndian(replica.NetworkId), 0x80]);
        // A bit count of 32 with two bytes of data.
        FromServer([(byte)MessageId.ReplicaManagerSerialize, 0, .. BigEndian(replica.NetworkId), 0x80, 0, 0, 0x08, 0, 9, 9]);
        _mesh.Settle();

        Assert.Equal(5, copy.Value);
    }

    [Fact]
    public void A_timestamp_with_no_room_for_a_message_id_is_consumed_and_one_with_no_world_id_continues()
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();

        // ReplicaManager3.cpp:657-672: nine bytes or fewer stop processing; RakNet reads the world ID of a ten-byte
        // message past its end, here it continues untouched.
        FromServer([(byte)MessageId.Timestamp, 0, 0, 0, 0, 0, 0, 0, 1]);
        FromServer([(byte)MessageId.Timestamp, 0, 0, 0, 0, 0, 0, 0, 2, (byte)MessageId.ReplicaManagerConstruction]);
        _mesh.Settle();

        var timestamps = _mesh.Seen(_client).Where(e => e.Id == (byte)MessageId.Timestamp).ToList();
        Assert.Equal(10, Assert.Single(timestamps).Data.Length);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(255)]
    public void A_message_for_a_world_that_does_not_exist_continues_to_the_user_and_changes_nothing(byte worldId)
    {
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        var message = Construction(1001, "A");
        message[1] = worldId;

        FromServer(message);
        _mesh.Settle();

        Assert.Empty(_clientManager.Replicas());
        Assert.Contains(_mesh.Seen(_client), e => e.Id == (byte)MessageId.ReplicaManagerConstruction && e.Data[1] == worldId);
    }

    [Fact]
    public void A_grouped_download_holding_more_messages_than_the_limit_is_released_in_order_and_holds_no_more()
    {
        _clientManager.GroupDownloads = true;
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        _clientManager.MaxDownloadGroupMessages = 4;
        _clientManager.Log.Clear();

        // A download that never completes.
        FromServer([(byte)MessageId.ReplicaManagerDownloadStarted, 0]);
        for (var value = 1; value <= 6; value++)
            FromServer(Serialization(replica.NetworkId, value));
        _mesh.Settle();

        Assert.Equal(["download started", .. Enumerable.Range(1, 6).Select(v => $"deserialize A {v}")], _clientManager.Log);
    }

    [Fact]
    public void A_grouped_download_holding_more_bytes_than_the_limit_is_released_in_order_and_holds_no_more()
    {
        _clientManager.GroupDownloads = true;
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        // The start, two bytes, and two 21-byte serializations fit; the third does not.
        _clientManager.MaxDownloadGroupBytes = 50;
        _clientManager.Log.Clear();

        FromServer([(byte)MessageId.ReplicaManagerDownloadStarted, 0]);
        for (var value = 1; value <= 4; value++)
            FromServer(Serialization(replica.NetworkId, value));
        _mesh.Settle();

        Assert.Equal(["download started", .. Enumerable.Range(1, 4).Select(v => $"deserialize A {v}")], _clientManager.Log);
    }

    [Fact]
    public void Messages_held_behind_a_completion_past_the_limit_are_dropped_as_they_would_never_be_returned()
    {
        _clientManager.GroupDownloads = true;
        var replica = _serverManager.Create("A");
        _mesh.Connect(_client, _server);
        _mesh.Settle();
        _clientManager.MaxDownloadGroupMessages = 2;
        _clientManager.Log.Clear();

        // The completion returns the start behind the serializations already queued, which the still open group holds
        // (see A_construction_queued_behind_a_grouped_download_s_completion_is_held_and_never_returned). With no start
        // among them, returning them would only hold them again.
        FromServer([(byte)MessageId.ReplicaManagerDownloadStarted, 0]);
        FromServer([(byte)MessageId.ReplicaManagerDownloadComplete, 0]);
        for (var value = 1; value <= 3; value++)
            FromServer(Serialization(replica.NetworkId, value));
        _mesh.Settle();
        FromServer(Serialization(replica.NetworkId, 4));
        _mesh.Settle();

        Assert.Equal(["download started", "download complete", "deserialize A 4"], _clientManager.Log);
    }

    [Fact]
    public void Download_group_limits_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _clientManager.MaxDownloadGroupMessages = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => _clientManager.MaxDownloadGroupBytes = 0);
    }

    private int SerializationsAtServer() => _mesh.Seen(_server).Count(e => e.Id == (byte)MessageId.ReplicaManagerSerialize);

    private void FromServer(byte[] message)
    {
        var server = _client.Connections.Single();
        _client.Inject(message, server.Address, server.RakNetGuid);
    }

    // SendSerialize's message for a TestReplica: channel 0 with 32 bits, then fifteen unwritten channels; 21 bytes.
    private static byte[] Serialization(ulong networkId, int value) =>
        [(byte)MessageId.ReplicaManagerSerialize, 0, .. BigEndian(networkId), 0x80, 0, 0, 0x08, 0, .. BigEndian((uint)value), 0, 0];

    // A construction entry for a replica that exists here: its end, the NetworkID, the create bit aligned, the creating
    // GUID and the end of an empty allocation ID; 25 bytes.
    private byte[] ExistingEntry(uint start, ulong networkId) =>
        [.. BigEndian(start + 200), .. BigEndian(networkId), 0x80, .. BigEndian(_server.RakNetGuid.Value), .. BigEndian(start + 200)];

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

    private static byte[] RakString(string text) => [(byte)(text.Length >> 8), (byte)text.Length, .. System.Text.Encoding.ASCII.GetBytes(text)];
}
