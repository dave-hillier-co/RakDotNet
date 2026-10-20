using RakDotNet.Plugins.Tests.NatPunchthrough;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.NatTypeDetection;

/// <summary>
/// NatTypeDetectionClient against NatTypeDetectionServer, from Samples/NATCompleteClient's NatTypeDetectionFramework and
/// NATCompleteServer's NatTypeDetectionServerFramework: the server has four addresses, the client is behind each kind of
/// router in turn.
/// </summary>
public sealed class NatTypeDetectionTests : IDisposable
{
    private const string ServerAddress = "203.0.113.1";

    private readonly NatMesh _mesh = new();
    private readonly NatTypeDetectionServer _server = new();
    private readonly NatTypeDetectionClient _client = new();
    private readonly RakPeer _serverPeer;

    public NatTypeDetectionTests()
    {
        _serverPeer = _mesh.Add(ServerAddress, plugins: _server);
        // NATCompleteServer.cpp:98: Startup(GetLocalIP(1), GetLocalIP(2), GetLocalIP(3)).
        Assert.True(_server.Startup("203.0.113.2", "203.0.113.3", "203.0.113.4"));
    }

    public void Dispose()
    {
        _mesh.Dispose();
        _server.Dispose();
        _client.Dispose();
    }

    [Fact]
    public void A_client_with_no_router_is_told_none()
    {
        var clientPeer = _mesh.Add("192.0.2.10", plugins: _client);

        var result = Detect(clientPeer);

        Assert.Equal(NatTypeDetectionResult.None, result);
    }

    [Theory]
    [InlineData(NatBehaviour.FullCone, NatTypeDetectionResult.FullCone)]
    [InlineData(NatBehaviour.AddressRestricted, NatTypeDetectionResult.AddressRestricted)]
    [InlineData(NatBehaviour.PortRestricted, NatTypeDetectionResult.PortRestricted)]
    [InlineData(NatBehaviour.Symmetric, NatTypeDetectionResult.Symmetric)]
    public void A_client_behind_a_router_is_told_its_kind(NatBehaviour behaviour, NatTypeDetectionResult expected)
    {
        var clientPeer = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", behaviour), plugins: _client);

        var result = Detect(clientPeer);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void The_result_is_pushed_from_the_server_generated_locally()
    {
        var clientPeer = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", NatBehaviour.FullCone), plugins: _client);

        Detect(clientPeer);

        // NatTypeDetectionClient.cpp:72-80: three bytes, from the server's address and GUID.
        var pushed = Assert.Single(_mesh.Seen(clientPeer), e => e.Id == (byte)MessageId.NatTypeDetectionResult);
        Assert.Equal([(byte)MessageId.NatTypeDetectionResult, (byte)NatTypeDetectionResult.FullCone, 0], pushed.Data);
        Assert.True(pushed.IsLocal);
        Assert.Equal(_serverPeer.Sockets[0].LocalAddress, pushed.Address);
        Assert.Equal(_serverPeer.RakNetGuid, pushed.RakNetGuid);
        // Nothing of the tests reaches the application.
        Assert.DoesNotContain(_mesh.Seen(clientPeer), e => e.Id is (byte)MessageId.OutOfBandInternal or (byte)MessageId.NatTypeDetectionRequest);
        Assert.False(_client.IsInProgress);
    }

    [Fact]
    public void The_request_is_the_id_a_true_bit_and_the_second_sockets_port()
    {
        var plainServer = _mesh.Add("203.0.113.9");
        var clientPeer = _mesh.Add("192.0.2.10", plugins: _client);
        _mesh.Connect(clientPeer, plainServer);

        Assert.True(_client.DetectNatType(plainServer.Sockets[0].LocalAddress));
        _mesh.Run(TimeSpan.FromMilliseconds(20));

        var request = Assert.Single(_mesh.Seen(plainServer), e => e.Id == (byte)MessageId.NatTypeDetectionRequest).Data;
        // NatTypeDetectionClient.cpp:66-70: Write(true) is one bit, so the port follows unaligned.
        Assert.Equal(4, request.Length);
        Assert.Equal(0x80, request[1] & 0x80);
        var port = ((request[1] & 0x7F) << 9) | (request[2] << 1) | (request[3] >> 7);
        Assert.NotEqual(clientPeer.Sockets[0].LocalAddress.Port, port);
        Assert.NotEqual(0, port);
    }

    [Fact]
    public void A_detection_in_progress_is_not_started_again()
    {
        var clientPeer = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", NatBehaviour.Symmetric), plugins: _client);
        _mesh.Connect(clientPeer, _serverPeer);

        Assert.True(_client.DetectNatType(_serverPeer.Sockets[0].LocalAddress));

        Assert.False(_client.DetectNatType(_serverPeer.Sockets[0].LocalAddress));
        Assert.True(_client.IsInProgress);
    }

    [Fact]
    public void A_client_that_never_answers_is_told_symmetric_after_the_port_restricted_tests()
    {
        // A plain peer stands in for a client whose router drops everything and that never runs the test it is asked to.
        var clientPeer = _mesh.Add("192.0.2.10");
        _mesh.Connect(clientPeer, _serverPeer);
        // NatTypeDetectionClient.cpp:66-70: the ID, true as one bit, then C2's port, here 4000.
        byte[] request = [(byte)MessageId.NatTypeDetectionRequest, 0x80 | (4000 >> 9), (4000 >> 1) & 0xFF, (4000 & 1) << 7];
        clientPeer.Send(request, _serverPeer.RakNetGuid, Reliability.Reliable);

        _mesh.RunUntil(() => _mesh.Seen(clientPeer).Any(e => e.Id == (byte)MessageId.NatTypeDetectionResult), "the result");

        var messages = _mesh.Seen(clientPeer).Where(e => e.Id is (byte)MessageId.NatTypeDetectionRequest or (byte)MessageId.NatTypeDetectionResult).ToList();
        // NatTypeDetectionServer.cpp:300-326: two requests to send to S3P4, its address as a RakString and its port, then
        // NAT_TYPE_SYMMETRIC.
        var s3p4 = _server.SocketAddresses[2];
        byte[] expectedRequest = [(byte)MessageId.NatTypeDetectionRequest, 0, 11, .. "203.0.113.3"u8, (byte)(s3p4.Port >> 8), (byte)s3p4.Port];
        Assert.Equal([expectedRequest, expectedRequest, [(byte)MessageId.NatTypeDetectionResult, (byte)NatTypeDetectionResult.Symmetric]], messages.Select(m => m.Data));
        Assert.False(messages[^1].IsLocal);
    }

    [Fact]
    public void Losing_the_server_ends_the_detection()
    {
        var clientPeer = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", NatBehaviour.Symmetric), plugins: _client);
        _mesh.Connect(clientPeer, _serverPeer);
        _client.DetectNatType(_serverPeer.Sockets[0].LocalAddress);

        clientPeer.Close(_serverPeer.RakNetGuid, notify: false);
        _mesh.Run(TimeSpan.FromMilliseconds(20));

        Assert.False(_client.IsInProgress);
        _mesh.Run(TimeSpan.FromSeconds(3));
        Assert.DoesNotContain(_mesh.Seen(clientPeer), e => e.Id == (byte)MessageId.NatTypeDetectionResult);
    }

    [Fact]
    public void Startup_reports_its_four_sockets()
    {
        var addresses = _server.SocketAddresses;

        Assert.Equal(["203.0.113.1", "203.0.113.2", "203.0.113.3", "203.0.113.4"], addresses.Select(a => a.ToString(writePort: false)));
        Assert.DoesNotContain(addresses, a => a.Port == 0 || a == _serverPeer.Sockets[0].LocalAddress);
        _server.Shutdown();
        Assert.Empty(_server.SocketAddresses);
    }

    [Fact]
    public void Startup_refuses_what_is_not_an_address()
    {
        Assert.False(_server.Startup("203.0.113.2", "not an address", "203.0.113.4"));

        Assert.Empty(_server.SocketAddresses);
    }

    private NatTypeDetectionResult Detect(RakPeer clientPeer)
    {
        _mesh.Connect(clientPeer, _serverPeer);
        Assert.True(_client.DetectNatType(_serverPeer.Sockets[0].LocalAddress));
        _mesh.RunUntil(() => _mesh.Seen(clientPeer).Any(e => e.Id == (byte)MessageId.NatTypeDetectionResult), "ID_NAT_TYPE_DETECTION_RESULT");
        var result = _mesh.Seen(clientPeer).Single(e => e.Id == (byte)MessageId.NatTypeDetectionResult);
        Assert.True(result.IsLocal);
        return (NatTypeDetectionResult)result.Data[1];
    }
}
