using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// The client's MTU probes (RakPeer.cpp:5691-5829) and the server's answers (RakPeer.cpp:5127-5197, 3595-3650).
public class MtuDiscoveryTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);
    private static readonly IPEndPoint Nobody = new(IPAddress.Parse("10.0.0.9"), 9);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public MtuDiscoveryTests()
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        _server = _link.Attach(ServerAddress, new PeerCore(new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8 }));
    }

    // Probe sizes the native interop-peer sent to a silent UDP socket with attemptIntervalMs 100, recorded as datagram
    // length + 28: the MTU index is requestsMade / (attempts / 3), capped at the last entry.
    [Theory]
    [InlineData(12, new[] { 1492, 1492, 1492, 1492, 1200, 1200, 1200, 1200, 576, 576, 576, 576, 576 })]
    [InlineData(7, new[] { 1492, 1492, 1200, 1200, 576, 576, 576, 576 })]
    [InlineData(3, new[] { 1492, 1200, 576, 576 })]
    public void Probes_step_down_through_1492_1200_and_576_as_the_native_client_does(int attempts, int[] expectedMtus)
    {
        _client.Connect(Nobody, default, attempts, 100, 0, _link.NowUs);

        _link.RunUntil(() => _link.EventsOf(_client).Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(expectedMtus, ProbeMtus(Nobody));
        Assert.Equal(MessageId.ConnectionAttemptFailed, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(_link.EventsOf(_client))).Reason);
    }

    // RakPeer.cpp:5750 divides by attempts / 3, which is 0 below three attempts. Integer division by zero yields 0 on
    // arm64, where the native interop-peer probed 1492 for attempts 0 and 2; the core probes 1492 likewise.
    [Theory]
    [InlineData(0, new[] { 1492 })]
    [InlineData(2, new[] { 1492, 1492, 1492 })]
    public void Fewer_than_three_attempts_probe_1492_every_time(int attempts, int[] expectedMtus)
    {
        _client.Connect(Nobody, default, attempts, 100, 0, _link.NowUs);

        _link.RunUntil(() => _link.EventsOf(_client).Count > 0, TimeSpan.FromSeconds(5));

        Assert.Equal(expectedMtus, ProbeMtus(Nobody));
    }

    [Fact]
    public void Each_probe_is_the_whole_datagram_padded_with_zeros_to_the_mtu_less_28()
    {
        // RakPeer.cpp:5758-5761: ID, magic, protocol version, PadWithZeroToByteLength(mtu - UDP_HEADER_SIZE).
        _client.Connect(Nobody, default, 3, 100, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(_client).Count > 0, TimeSpan.FromSeconds(5));

        foreach (var probe in Probes(Nobody))
        {
            Assert.Equal(MessageId.OpenConnectionRequest1, (MessageId)probe[0]);
            Assert.Equal(RakNetConstants.ProtocolVersion, probe[17]);
            Assert.All(probe[18..], b => Assert.Equal(0, b));
            Assert.Equal(probe.Length + RakNetConstants.UdpHeaderSize, Decode<OpenConnectionRequest1>(probe).MtuSize);
        }
    }

    [Fact]
    public void Only_probes_ask_the_transport_not_to_fragment()
    {
        // RakPeer.cpp:5784-5787 and 5818-5821 set the don't-fragment flag around the probe's send only.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _client.Update(_link.NowUs + 1_000);

        var probe = Assert.Single(Datagrams(_client));
        Assert.True(probe.DoNotFragment);

        _server.Receive(ClientAddress, probe.Data, _link.NowUs + 1_000);
        Assert.False(Assert.Single(Datagrams(_server)).DoNotFragment);
    }

    [Fact]
    public void A_path_that_drops_1492_byte_datagrams_connects_at_1200()
    {
        _link.Drop = (_, _, data) => data.Length > 1200 - RakNetConstants.UdpHeaderSize;

        Connect(attempts: 12, intervalMs: 100);

        Assert.Equal(1200, _client.GetMtuSize(ServerAddress));
        Assert.Equal(1200, _server.GetMtuSize(ClientAddress));
        Assert.Equal([1492, 1492, 1492, 1492, 1200], ProbeMtus(ServerAddress));
    }

    [Fact]
    public void A_path_that_drops_anything_over_576_connects_at_576()
    {
        _link.Drop = (_, _, data) => data.Length > 576 - RakNetConstants.UdpHeaderSize;

        Connect(attempts: 6, intervalMs: 100);

        Assert.Equal(576, _client.GetMtuSize(ServerAddress));
        Assert.Equal(576, _server.GetMtuSize(ClientAddress));
    }

    [Fact]
    public void After_connecting_at_576_messages_are_packed_into_datagrams_that_fit()
    {
        _link.Drop = (_, _, data) => data.Length > 576 - RakNetConstants.UdpHeaderSize;
        Connect(attempts: 6, intervalMs: 100);
        var sentBefore = _link.Sent.Count;
        var messages = Enumerable.Range(0, 20).Select(i => Enumerable.Repeat((byte)i, 100).Prepend((byte)MessageId.UserPacketEnum).ToArray()).ToList();

        foreach (var message in messages)
            _client.Send(ServerAddress, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(_server).OfType<MessageReceived>().Count() == messages.Count, TimeSpan.FromSeconds(5));

        Assert.Equal(messages, _link.EventsOf(_server).OfType<MessageReceived>().Select(m => m.Packet.ToArray()));
        Assert.All(_link.Sent.Skip(sentBefore), d => Assert.InRange(d.Data.Length, 1, 576 - RakNetConstants.UdpHeaderSize));
    }

    [Fact]
    public void An_unobstructed_path_connects_at_1492()
    {
        Connect(attempts: 12, intervalMs: 500);

        Assert.Equal(1492, _client.GetMtuSize(ServerAddress));
        Assert.Equal(1492, _server.GetMtuSize(ClientAddress));
    }

    [Fact]
    public void The_mtu_of_an_unknown_system_is_the_default_576()
    {
        // GetMTUSize, RakPeer.cpp:2570-2580: defaultMTUSize is mtuSizes[NUM_MTU_SIZES-1].
        Assert.Equal(576, _client.GetMtuSize(Nobody));
    }

    [Fact]
    public void The_mtu_of_a_disconnected_system_is_the_default_576()
    {
        Connect(attempts: 12, intervalMs: 500);
        Assert.Equal(1492, _client.GetMtuSize(ServerAddress));

        _client.CloseConnection(ServerAddress, false, 0, Priority.Low, _link.NowUs);

        // GetMTUSize looks up only active systems (RakPeer.cpp:2575), though the slot still holds the address.
        Assert.Equal(576, _client.GetMtuSize(ServerAddress));
    }

    [Theory]
    [InlineData(1492, 1492)]
    [InlineData(1200, 1200)]
    [InlineData(576, 576)]
    [InlineData(1600, 1492)]
    public void The_server_offers_the_probe_size_capped_at_1492(int probeMtu, int offeredMtu)
    {
        // RakPeer.cpp:5182-5186.
        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest1(RakNetConstants.ProtocolVersion, probeMtu)), _link.NowUs);

        var reply = Decode<OpenConnectionReply1>(Assert.Single(Datagrams(_server)).Data);
        Assert.Equal(offeredMtu, reply.MtuSize);
        Assert.Equal(ServerGuid, reply.ServerGuid);
    }

    [Fact]
    public void The_client_asks_for_the_mtu_the_server_offered()
    {
        // RakPeer.cpp:4845-4852: the MTU read from the reply is written into ID_OPEN_CONNECTION_REQUEST_2.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, Encode(new OpenConnectionReply1(ServerGuid, 1200)), _link.NowUs);

        var request = Decode<OpenConnectionRequest2>(Assert.Single(Datagrams(_client)).Data);
        Assert.Equal(1200, request.MtuSize);
        Assert.Equal(ServerAddress, request.ServerAddress);
        Assert.Equal(ClientGuid, request.ClientGuid);
    }

    [Theory]
    [InlineData(1200, 1200)]
    [InlineData(400, 576)]
    [InlineData(9000, 1492)]
    public void The_server_uses_the_requested_mtu_but_no_less_than_576_and_no_more_than_1492(int requestedMtu, int usedMtu)
    {
        // AssignSystemAddressToRemoteSystemList (RakPeer.cpp:3638-3644) starts from defaultMTUSize and raises it to the
        // incoming MTU; the C++ asserts the result is at most MAXIMUM_MTU_SIZE, which the core enforces.
        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, (ushort)requestedMtu, ClientGuid)), _link.NowUs);

        Assert.Equal(usedMtu, _server.GetMtuSize(ClientAddress));
        Assert.Equal(requestedMtu, Decode<OpenConnectionReply2>(Assert.Single(Datagrams(_server)).Data).MtuSize);
    }

    [Fact]
    public void A_probe_too_large_for_the_path_moves_on_to_the_next_mtu_at_once()
    {
        // RakPeer.cpp:5791-5797: a send failing with WSAEMSGSIZE skips the rest of that MTU's attempts and probes again
        // on the next update.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _client.Update(_link.NowUs + 1_000);
        Assert.Equal(1492, Assert.Single(Datagrams(_client)).Data.Length + RakNetConstants.UdpHeaderSize);

        _client.ReportDatagramTooLarge(ServerAddress, _link.NowUs + 1_000);
        _client.Update(_link.NowUs + 2_000);

        Assert.Equal(1200, Assert.Single(Datagrams(_client)).Data.Length + RakNetConstants.UdpHeaderSize);
    }

    [Fact]
    public void A_probe_at_576_too_large_for_the_path_keeps_probing_576_until_the_attempts_run_out()
    {
        _client.Connect(Nobody, default, 3, 100, 0, _link.NowUs);
        for (var i = 0; i < 3; i++)
        {
            _link.Step();
            _client.ReportDatagramTooLarge(Nobody, _link.NowUs);
        }

        _link.RunUntil(() => _link.EventsOf(_client).Count > 0, TimeSpan.FromSeconds(1));

        Assert.Equal([1492, 1200, 576, 576], ProbeMtus(Nobody));
    }

    [Fact]
    public void With_fewer_than_three_attempts_a_probe_too_large_for_the_path_ends_the_attempt()
    {
        // RakPeer.cpp:5795 would set requestsMade to 0 and probe 1492 again on every update without end; the core fails
        // the attempt instead.
        _client.Connect(Nobody, default, 2, 100, 0, _link.NowUs);
        _link.Step();

        _client.ReportDatagramTooLarge(Nobody, _link.NowUs);
        _link.Step();

        Assert.Equal(MessageId.ConnectionAttemptFailed, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(_link.EventsOf(_client))).Reason);
        Assert.Equal([1492], ProbeMtus(Nobody));
    }

    [Fact]
    public void A_datagram_too_large_for_an_address_with_no_attempt_is_ignored()
    {
        _client.ReportDatagramTooLarge(Nobody, _link.NowUs);
        _client.Update(_link.NowUs + 1_000);

        Assert.Empty(Datagrams(_client));
        Assert.Empty(Events(_client));
    }

    private void Connect(int attempts, int intervalMs)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, attempts, intervalMs, 0, _link.NowUs));
        _link.RunUntil(
            () => _link.EventsOf(_client).OfType<OutgoingConnectionCompleted>().Any() && _link.EventsOf(_server).OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(10)
        );
    }

    private IEnumerable<byte[]> Probes(IPEndPoint to) =>
        _link.Sent.Where(d => d.To.Equals(to) && d.Data[0] == (byte)MessageId.OpenConnectionRequest1).Select(d => d.Data);

    private List<int> ProbeMtus(IPEndPoint to) => Probes(to).Select(p => p.Length + RakNetConstants.UdpHeaderSize).ToList();
}
