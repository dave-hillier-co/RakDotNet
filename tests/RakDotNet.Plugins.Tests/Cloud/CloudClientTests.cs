using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Cloud;

/// <summary>The requests CloudClient.cpp sends, and how it hands responses to the application.</summary>
public sealed class CloudClientTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly CloudClient _client = new();
    private readonly RakPeer _clientPeer;
    private readonly RakPeer _serverPeer;

    public CloudClientTests()
    {
        _clientPeer = _mesh.Add(maxIncoming: 0, _client);
        _serverPeer = _mesh.Add(maxIncoming: 4);
        _mesh.Connect(_clientPeer, _serverPeer);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void Post_sends_the_key_the_length_then_the_data_aligned()
    {
        _client.Post(new CloudKey("k", 1), "abc"u8, _serverPeer.RakNetGuid);
        _client.Post(new CloudKey("k", 2), [], _serverPeer.RakNetGuid);

        // CloudClient.cpp:37-50.
        Assert.Equal(["7300016b0000000100000003616263", "7300016b0000000200000000"], Received(0x73));
    }

    [Fact]
    public void Release_sends_a_u16_count_then_the_keys()
    {
        _client.Release([new CloudKey("k", 1), new CloudKey("k", 2)], _serverPeer.RakNetGuid);

        // CloudClient.cpp:51-62.
        Assert.Equal(["74000200016b0000000100016b00000002"], Received(0x74));
    }

    [Fact]
    public void Get_sends_the_query_then_the_specific_systems()
    {
        var query = new CloudQuery { Keys = { new CloudKey("k", 1) } };

        _client.Get(query, _serverPeer.RakNetGuid);
        _client.Get(query, [new RakNetGuid(0x0102030405060708), new RakNetGuid(9)], _serverPeer.RakNetGuid);

        // CloudClient.cpp:63-85: the query (110, one key "k",1), then a u16 count of GUIDs.
        Assert.Equal(["75c00020002" + "06b000000010000", "75c00020002" + "06b00000001000201020304050607080000000000000009"], Received(0x75));
    }

    [Fact]
    public void Get_naming_systems_by_rows_sends_a_bit_then_a_GUID_or_an_address()
    {
        var query = new CloudQuery { Keys = { new CloudKey("k", 1) } };
        var byGuid = new CloudQueryRow(new CloudKey("k", 1), Array.Empty<byte>(), SystemAddress.Unassigned, SystemAddress.Unassigned, RakNetGuid.Unassigned, new RakNetGuid(7));
        var byAddress = byGuid with { ClientGuid = RakNetGuid.Unassigned, ClientSystemAddress = new SystemAddress(IPAddress.Parse("10.1.2.3"), 4000) };

        _client.Get(query, [byGuid, byAddress], _serverPeer.RakNetGuid);

        // CloudClient.cpp:86-108: a set bit and the GUID, or a clear bit and the address.
        var expected = new byte[64];
        var writer = new BitStreamWriter(expected);
        writer.WriteByte((byte)MessageId.CloudGetRequest);
        query.Write(ref writer);
        writer.WriteUInt16(2);
        writer.WriteBool(true);
        writer.WriteRakNetGuid(new RakNetGuid(7));
        writer.WriteBool(false);
        writer.WriteAddress(byAddress.ClientSystemAddress);
        Assert.Equal([Convert.ToHexStringLower(writer.Written)], Received(0x75));
    }

    [Fact]
    public void Unsubscribe_sends_the_keys_then_the_specific_systems()
    {
        _client.Unsubscribe([new CloudKey("k", 1)], _serverPeer.RakNetGuid);
        _client.Unsubscribe([new CloudKey("k", 1)], [new RakNetGuid(5)], _serverPeer.RakNetGuid);

        // CloudClient.cpp:109-139.
        Assert.Equal(["77000100016b000000010000", "77000100016b0000000100010000000000000005"], Received(0x77));
    }

    [Fact]
    public void Responses_reach_the_application_and_go_to_the_handler_passed_or_the_default()
    {
        var server = new CloudServer();
        _serverPeer.Plugins.Attach(server);
        var defaultHandler = new RecordingCloudHandler();
        var passed = new RecordingCloudHandler();
        _client.DefaultHandler = defaultHandler;
        _client.Post(new CloudKey("k", 1), "abc"u8, _serverPeer.RakNetGuid);
        _client.Get(new CloudQuery { Keys = { new CloudKey("k", 1) } }, _serverPeer.RakNetGuid);
        _mesh.RunUntil(() => _mesh.Seen(_clientPeer).Any(e => e.Id == (byte)MessageId.CloudGetResponse), "the get response");

        // CloudClient::OnReceive passes everything on (CloudClient.cpp:167-171).
        var response = _mesh.Seen(_clientPeer).Single(e => e.Id == (byte)MessageId.CloudGetResponse).Data;
        Assert.True(_client.OnGetResponse(response));
        Assert.True(_client.OnGetResponse(response, passed));

        Assert.Equal("abc"u8.ToArray(), Assert.Single(Assert.Single(defaultHandler.Gets).Rows).Data.ToArray());
        Assert.Single(passed.Gets);
        Assert.False(_client.OnGetResponse([(byte)MessageId.CloudGetResponse, 0xff]));
    }

    [Fact]
    public void Reading_a_response_with_no_handler_set_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(() => _client.OnGetResponse([(byte)MessageId.CloudGetResponse]));
        Assert.Throws<InvalidOperationException>(() => _client.OnSubscriptionNotification([(byte)MessageId.CloudSubscriptionNotification]));
    }

    [Fact]
    public void Requests_need_the_plugin_attached()
    {
        var detached = new CloudClient();

        Assert.Throws<InvalidOperationException>(() => detached.Post(new CloudKey("k", 1), [], _serverPeer.RakNetGuid));
    }

    private List<string> Received(byte id)
    {
        _mesh.Settle();
        return [.. _mesh.Seen(_serverPeer).Where(e => e.Kind == PeerEventKind.Message && e.Id == id).Select(e => Convert.ToHexStringLower(e.Data))];
    }
}
