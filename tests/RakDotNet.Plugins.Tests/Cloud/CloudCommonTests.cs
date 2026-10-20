using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Cloud;

/// <summary>CloudCommon.cpp's encodings, checked against the bytes native RakNet writes (tools/file-vectors, cloud_*).</summary>
public sealed class CloudCommonTests
{
    private const string Row = "000f4170706c69636174696f6e4e616d650000000104f5fffffeea620480fffffeee48112233445566778899aabbccddeeff00000000084453314331533100";
    private const string UnassignedRow = "000f4170706c69636174696f6e4e616d650000000204ffffffff000004ffffffff00000000000000000005000000000000000600000000";

    private static readonly CloudQueryRow DataRow = new(
        new CloudKey("ApplicationName", 1),
        "DS1C1S1\0"u8.ToArray(),
        new SystemAddress(IPAddress.Parse("10.0.0.1"), 60002),
        new SystemAddress(IPAddress.Loopback, 61000),
        new RakNetGuid(0x1122334455667788),
        new RakNetGuid(0x99aabbccddeeff00));

    private static readonly CloudQueryRow PlaceholderRow = new(
        new CloudKey("ApplicationName", 2), ReadOnlyMemory<byte>.Empty, SystemAddress.Unassigned, SystemAddress.Unassigned, new RakNetGuid(5), new RakNetGuid(6));

    [Fact]
    public void A_key_is_its_primary_key_as_a_RakString_then_its_secondary_key()
    {
        Assert.Equal(("000f4170706c69636174696f6e4e616d6500000001", 168), Encode(new CloudKey("ApplicationName", 1)));
        Assert.Equal(("000001020304", 48), Encode(new CloudKey("", 0x01020304)));
    }

    [Fact]
    public void A_key_reads_back_from_the_native_bytes()
    {
        var reader = new BitStreamReader(Convert.FromHexString("000f4170706c69636174696f6e4e616d6500000001"));

        Assert.True(CloudKey.TryRead(ref reader, out var key));

        Assert.Equal(new CloudKey("ApplicationName", 1), key);
        Assert.Equal("ApplicationName", key.PrimaryKey);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void A_primary_key_ends_at_its_first_NUL_as_a_RakString_does()
    {
        var key = new CloudKey("App\0Name", 3);

        Assert.Equal("App", key.PrimaryKey);
        Assert.Equal(new CloudKey("App", 3), key);
    }

    [Fact]
    public void Keys_order_by_primary_key_bytes_then_secondary_key_as_CloudKeyComp_does()
    {
        // CloudKeyComp (CloudCommon.cpp:19-30): RakString's operator< is strcmp, which compares unsigned bytes.
        CloudKey[] keys = [new("b", 0), new("a", 2), new("a", 1), new("é", 0), new("B", 9), new("", 4)];

        Array.Sort(keys);

        Assert.Equal([new("", 4), new("B", 9), new("a", 1), new("a", 2), new("b", 0), new("é", 0)], keys);
    }

    [Fact]
    public void A_query_flags_zero_values_and_writes_only_the_others()
    {
        // CloudQuery::Serialize (CloudCommon.cpp:53-85).
        var all = new CloudQuery { Keys = { new("ApplicationName", 1), new("ApplicationName", 2) } };
        var paged = new CloudQuery { Keys = { new("k", 0x01020304) }, StartingRowIndex = 3, MaxRowsToReturn = 7, SubscribeToResults = true };
        var maxOnly = new CloudQuery { Keys = { new("k", 9) }, MaxRowsToReturn = 2 };

        Assert.Equal(("c0004001e04170706c69636174696f6e4e616d6500000001000f4170706c69636174696f6e4e616d6500000002", 360), Encode(all));
        Assert.Equal(("2000000060000000e0002000206b01020304", 144), Encode(paged));
        Assert.Equal(("8000000040002000206b00000009", 112), Encode(maxOnly));
    }

    [Fact]
    public void A_query_reads_back_from_the_native_bytes()
    {
        var reader = new BitStreamReader(Convert.FromHexString("2000000060000000e0002000206b01020304"), 144);

        Assert.True(CloudQuery.TryRead(ref reader, out var query));

        Assert.Equal([new CloudKey("k", 0x01020304)], query.Keys);
        Assert.Equal((3u, 7u, true), (query.StartingRowIndex, query.MaxRowsToReturn, query.SubscribeToResults));
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void A_row_is_its_key_addresses_GUIDs_then_its_data_aligned()
    {
        // CloudQueryRow::Serialize (CloudCommon.cpp:86-115).
        Assert.Equal((Row, 504), Encode(DataRow));
        Assert.Equal((UnassignedRow, 440), Encode(PlaceholderRow));
    }

    [Fact]
    public void A_row_reads_back_from_the_native_bytes()
    {
        var reader = new BitStreamReader(Convert.FromHexString(Row));

        Assert.True(CloudQueryRow.TryRead(ref reader, out var row));

        Assert.Equal(DataRow.Key, row.Key);
        Assert.Equal(DataRow.Data.ToArray(), row.Data.ToArray());
        Assert.Equal((DataRow.ServerSystemAddress, DataRow.ClientSystemAddress), (row.ServerSystemAddress, row.ClientSystemAddress));
        Assert.Equal((DataRow.ServerGuid, DataRow.ClientGuid), (row.ServerGuid, row.ClientGuid));
    }

    [Fact]
    public void A_row_whose_data_is_cut_short_is_refused()
    {
        var bytes = Convert.FromHexString(Row);
        var reader = new BitStreamReader(bytes.AsSpan(0, bytes.Length - 1));

        Assert.False(CloudQueryRow.TryRead(ref reader, out _));
    }

    [Fact]
    public void A_get_response_is_the_query_its_subscription_flag_a_row_count_then_the_rows()
    {
        // CloudQueryResult::Serialize (CloudCommon.cpp:161-167) after ID_CLOUD_GET_RESPONSE, as CloudServer writes it.
        var result = new CloudQueryResult(new CloudQuery { Keys = { new("ApplicationName", 1), new("ApplicationName", 2) } }, [DataRow, PlaceholderRow], false);
        var buffer = new byte[256];
        var writer = new BitStreamWriter(buffer);

        writer.WriteByte((byte)MessageId.CloudGetResponse);
        result.Write(ref writer);

        Assert.Equal(1352, writer.BitsWritten);
        Assert.Equal("76c0004001e04170706c69636174696f6e4e616d6500000001000f4170706c69636174696f6e4e616d6500000002000000010007804170706c69636174696f6e4e616d650000000104f5fffffeea620480fffffeee48112233445566778899aabbccddeeff00000000084453314331533100000f4170706c69636174696f6e4e616d650000000204ffffffff000004ffffffff00000000000000000005000000000000000600000000",
            Convert.ToHexStringLower(writer.Written));
    }

    [Fact]
    public void A_get_response_reads_back_from_the_native_bytes()
    {
        var bytes = Convert.FromHexString("76c0004001e04170706c69636174696f6e4e616d6500000001000f4170706c69636174696f6e4e616d6500000002000000010007804170706c69636174696f6e4e616d650000000104f5fffffeea620480fffffeee48112233445566778899aabbccddeeff00000000084453314331533100000f4170706c69636174696f6e4e616d650000000204ffffffff000004ffffffff00000000000000000005000000000000000600000000");

        Assert.True(CloudClient.TryReadGetResponse(bytes, out var result));

        Assert.Equal([new CloudKey("ApplicationName", 1), new CloudKey("ApplicationName", 2)], result.Query.Keys);
        Assert.False(result.SubscribeToResults);
        Assert.Collection(result.Rows,
            r =>
            {
                Assert.Equal("DS1C1S1\0"u8.ToArray(), r.Data.ToArray());
                Assert.Equal(DataRow.ClientGuid, r.ClientGuid);
            },
            r => Assert.Equal((0, SystemAddress.Unassigned, new RakNetGuid(6)), (r.Data.Length, r.ServerSystemAddress, r.ClientGuid)));
    }

    [Fact]
    public void A_get_response_claiming_more_rows_than_it_holds_is_refused()
    {
        // A row count of 0x10000000 with no rows behind it.
        var bytes = Convert.FromHexString("76c0004001e04170706c69636174696f6e4e616d6500000001000f4170706c69636174696f6e4e616d65000000020800000000");

        Assert.False(CloudClient.TryReadGetResponse(bytes, out _));
    }

    [Fact]
    public void A_subscription_notification_is_whether_it_was_updated_then_the_row()
    {
        // CloudServer::NotifyClientSubscribersOfDataChange (CloudServer.cpp:1067-1087).
        var bytes = Convert.FromHexString("798007804170706c69636174696f6e4e616d650000000104f5fffffeea620480fffffeee48112233445566778899aabbccddeeff00000000084453314331533100");

        Assert.True(CloudClient.TryReadSubscriptionNotification(bytes, out var wasUpdated, out var row));

        Assert.True(wasUpdated);
        Assert.Equal((DataRow.Key, DataRow.ClientGuid), (row.Key, row.ClientGuid));
        Assert.Equal(DataRow.Data.ToArray(), row.Data.ToArray());
    }

    [Fact]
    public void Messages_with_another_ID_are_not_read_as_cloud_messages()
    {
        Assert.False(CloudClient.TryReadGetResponse([(byte)MessageId.CloudSubscriptionNotification, 0], out _));
        Assert.False(CloudClient.TryReadSubscriptionNotification([(byte)MessageId.CloudGetResponse, 0], out _, out _));
    }

    private static (string Hex, int Bits) Encode<T>(T value)
        where T : Protocol.Wire.Messages.IWireMessage<T>
    {
        var buffer = new byte[256];
        var writer = new BitStreamWriter(buffer);
        value.Write(ref writer);
        return (Convert.ToHexStringLower(writer.Written), writer.BitsWritten);
    }
}
