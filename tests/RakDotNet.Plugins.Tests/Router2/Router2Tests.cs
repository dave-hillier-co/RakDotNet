using RakDotNet.Plugins.Tests.NatPunchthrough;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Router2;

/// <summary>
/// Router2, from Samples/Router2: four systems, A connected to B and C, B to C, and D to B and C. A routes to D through
/// one of the routers, connects through it, and when that router leaves is rerouted through the other. A and D are
/// behind symmetric routers, so neither could reach the other directly. The routers are on 127.0.0.1, because the
/// in-memory network sends from a socket bound to every address as 127.0.0.1.
/// </summary>
public sealed class Router2Tests : IDisposable
{
    private readonly NatMesh _mesh = new();
    private readonly Plugins.Router2 _sourcePlugin = new(PeerThreading.Manual);
    private readonly Plugins.Router2 _endpointPlugin = new(PeerThreading.Manual);
    private readonly Plugins.Router2 _routerBPlugin = new(PeerThreading.Manual) { MaximumForwardingRequests = 1 };
    private readonly Plugins.Router2 _routerCPlugin = new(PeerThreading.Manual) { MaximumForwardingRequests = 1 };
    private readonly RakPeer _a;
    private readonly RakPeer _b;
    private readonly RakPeer _c;
    private readonly RakPeer _d;

    public Router2Tests()
    {
        _a = _mesh.Add("10.0.0.2", _mesh.Nat("198.51.100.1", NatBehaviour.Symmetric), plugins: _sourcePlugin);
        _b = _mesh.Add("127.0.0.1", plugins: _routerBPlugin);
        _c = _mesh.Add("127.0.0.1", plugins: _routerCPlugin);
        _d = _mesh.Add("10.0.1.2", _mesh.Nat("198.51.100.2", NatBehaviour.Symmetric), plugins: _endpointPlugin);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void The_source_routes_through_a_shared_router_and_connects_to_the_endpoint()
    {
        ConnectAsInTheSample();

        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        var established = WaitFor(_a, MessageId.Router2ForwardingEstablished);

        // SendForwardingSuccess (Router2.cpp:792-810): from the router, the endpoint's GUID and the forwarding port.
        var router = established.RakNetGuid == _b.RakNetGuid ? _b : _c;
        Assert.Contains(established.RakNetGuid, new[] { _b.RakNetGuid, _c.RakNetGuid });
        var reader = new BitStreamReader(established.Data);
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var endpoint);
        reader.TryReadUInt16(out var port);
        Assert.Equal(_d.RakNetGuid, endpoint);
        Assert.Equal(11, established.Data.Length);
        // The endpoint was told the source now comes from the forwarding port.
        var rerouted = WaitFor(_d, MessageId.Router2Rerouted);
        Assert.Equal(router.RakNetGuid, rerouted.RakNetGuid);
        reader = new BitStreamReader(rerouted.Data);
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var source);
        reader.TryReadUInt16(out var reroutedPort);
        Assert.Equal(_a.RakNetGuid, source);
        Assert.Equal(port, reroutedPort);

        // The sample connects to the router's address on that port.
        var forwarded = established.Address.WithPort(port);
        var connection = _mesh.Connect(_a, forwarded, _d);
        var incoming = _mesh.Seen(_d).Last(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(_a.RakNetGuid, incoming.RakNetGuid);
        Assert.Equal(port, incoming.Address.Port);

        // ID_ROUTER_2_INCREASE_TIMEOUT: both add 10 s to the connection's timeout.
        _mesh.Settle();
        Assert.Equal(_a.DefaultTimeout + TimeSpan.FromSeconds(10), connection.Timeout);
        Assert.True(_d.TryGetConnection(_a.RakNetGuid, out var atEndpoint));
        Assert.Equal(_d.DefaultTimeout + TimeSpan.FromSeconds(10), atEndpoint.Timeout);

        // Samples/Router2: a message to the endpoint.
        _a.Send(new byte[] { (byte)MessageId.UserPacketEnum + 1 }, _d.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(_d).Any(e => e.Id == (byte)MessageId.UserPacketEnum + 1), "the message through the router");
    }

    [Fact]
    public void When_the_router_leaves_the_connection_is_rerouted_through_the_other()
    {
        ConnectAsInTheSample();
        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        var established = WaitFor(_a, MessageId.Router2ForwardingEstablished);
        var (router, other) = established.RakNetGuid == _b.RakNetGuid ? (_b, _c) : (_c, _b);
        var reader = new BitStreamReader(established.Data);
        reader.IgnoreBytes(9);
        reader.TryReadUInt16(out var port);
        _mesh.Connect(_a, established.Address.WithPort(port), _d);
        _mesh.Settle();

        // Samples/Router2: "close the router that did the routing to reroute".
        router.Close(_a.RakNetGuid, notify: true);
        router.Close(_d.RakNetGuid, notify: true);
        router.Close(other.RakNetGuid, notify: true);

        // Forwarding through the other router, reported as rerouted, with the address changed to its port.
        var rerouted = WaitFor(_a, MessageId.Router2Rerouted, e => e.RakNetGuid == other.RakNetGuid);
        reader = new BitStreamReader(rerouted.Data);
        reader.IgnoreBytes(1);
        reader.TryReadRakNetGuid(out var endpoint);
        reader.TryReadUInt16(out var newPort);
        Assert.Equal(_d.RakNetGuid, endpoint);
        _mesh.Step();
        Assert.Equal(rerouted.Address.WithPort(newPort), _a.GetAddressFromGuid(_d.RakNetGuid));
        Assert.Equal(other.Sockets[0].LocalAddress.WithPort(newPort), _a.GetAddressFromGuid(_d.RakNetGuid));

        _a.Send(new byte[] { (byte)MessageId.UserPacketEnum + 1, 2 }, _d.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(_d).Any(e => e.Data is [(byte)MessageId.UserPacketEnum + 1, 2]), "the message through the other router");
        Assert.DoesNotContain(_mesh.Seen(_a), e => e.Kind == PeerEventKind.Disconnected && e.RakNetGuid == _d.RakNetGuid);
    }

    [Fact]
    public void Without_a_router_connected_to_the_endpoint_the_application_hears_nothing()
    {
        // A connects to B only, and B is not connected to D.
        _mesh.Connect(_a, _b);
        _mesh.Connect(_d, _c);
        _mesh.Run(TimeSpan.FromMilliseconds(500));

        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        _mesh.Run(TimeSpan.FromSeconds(3));

        // ID_ROUTER_2_FORWARDING_NO_PATH is pushed as not generated locally, which Router2 consumes (Router2.cpp:337-341).
        Assert.DoesNotContain(_mesh.Seen(_a), e => e.Id is (byte)MessageId.Router2ForwardingNoPath or (byte)MessageId.Router2ForwardingEstablished);
        Assert.DoesNotContain(_mesh.Seen(_a), e => e.Id == (byte)MessageId.Router2Internal);
    }

    [Fact]
    public void A_system_that_does_not_forward_refuses_and_the_attempt_ends()
    {
        _routerBPlugin.MaximumForwardingRequests = 0;
        _mesh.Connect(_a, _b);
        _mesh.Connect(_d, _b);
        _mesh.Run(TimeSpan.FromMilliseconds(500));
        var replies = new List<byte[]>();
        Spy(_a, replies);

        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        _mesh.RunUntil(() => replies.Count > 0, "the reply");
        _mesh.Settle();

        // SendFailureOnCannotForward (Router2.cpp:657-665): the endpoint's GUID and false.
        var expected = new byte[11];
        var writer = new BitStreamWriter(expected);
        writer.WriteByte((byte)MessageId.Router2Internal);
        writer.WriteByte(1);
        writer.WriteRakNetGuid(_d.RakNetGuid);
        writer.WriteBool(false);
        Assert.Equal(expected, replies[0]);
        Assert.Null(_routerBPlugin.Forwarder);
        // The attempt is over, so routing can be asked for again.
        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        _mesh.RunUntil(() => replies.Count > 1, "the second reply");
    }

    [Fact]
    public void A_router_that_can_forward_replies_with_its_ping_and_its_entries_in_use_as_a_32_bit_int()
    {
        _mesh.Connect(_a, _b);
        _mesh.Connect(_d, _b);
        _mesh.Run(TimeSpan.FromMilliseconds(500));
        var replies = new List<byte[]>();
        Spy(_a, replies);
        Assert.True(_b.TryGetConnection(_d.RakNetGuid, out var bToD));
        var ping = (ushort)bToD.AveragePing!.Value.TotalMilliseconds;

        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        _mesh.RunUntil(() => replies.Count > 0, "the reply");

        // Router2.cpp:725-729: (unsigned short)used/2 is an int, written in 32 bits.
        var expected = new byte[17];
        var writer = new BitStreamWriter(expected);
        writer.WriteByte((byte)MessageId.Router2Internal);
        writer.WriteByte(1);
        writer.WriteRakNetGuid(_d.RakNetGuid);
        writer.WriteBool(true);
        writer.WriteUInt16(ping);
        writer.WriteInt32(0);
        Assert.Equal(expected, replies[0]);
        WaitFor(_a, MessageId.Router2ForwardingEstablished);
    }

    [Fact]
    public void Routing_to_a_connected_endpoint_or_with_no_pinged_connection_does_nothing()
    {
        var sent = _mesh.Network.DatagramsSent;
        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        Assert.Equal(sent, _mesh.Network.DatagramsSent);

        _mesh.Connect(_b, _c);
        _mesh.Settle();
        var replies = new List<byte[]>();
        Spy(_b, replies);
        _routerBPlugin.EstablishRouting(_c.RakNetGuid);
        _mesh.Run(TimeSpan.FromSeconds(2));
        Assert.Empty(replies);
    }

    [Fact]
    public void A_failed_connection_attempt_to_the_forwarding_port_names_the_endpoint()
    {
        ConnectAsInTheSample();
        _sourcePlugin.EstablishRouting(_d.RakNetGuid);
        var established = WaitFor(_a, MessageId.Router2ForwardingEstablished);
        var router = established.RakNetGuid == _b.RakNetGuid ? _routerBPlugin : _routerCPlugin;
        var reader = new BitStreamReader(established.Data);
        reader.IgnoreBytes(9);
        reader.TryReadUInt16(out var port);
        // The router stops forwarding before the source connects.
        router.MaximumForwardingRequests = 0;

        _a.Connect(established.Address.WithPort(port), new ConnectOptions { AttemptCount = 2, AttemptInterval = TimeSpan.FromMilliseconds(100) });
        var failed = WaitFor(_a, MessageId.ConnectionAttemptFailed);

        // OnFailedConnectionAttempt (Router2.cpp:520-540) sets the packet's GUID to the endpoint's.
        Assert.Equal(_d.RakNetGuid, failed.RakNetGuid);
        Assert.Equal(established.Address.WithPort(port), failed.Address);
    }

    [Fact]
    public void A_router_forwards_only_while_attached_with_forwarding_requests_and_until_its_peer_shuts_down()
    {
        var plugin = new Plugins.Router2(PeerThreading.Manual) { MaximumForwardingRequests = 2 };
        Assert.Null(plugin.Forwarder);
        var peer = _mesh.Add("127.0.0.1", plugins: plugin);
        Assert.True(plugin.Forwarder!.IsRunning);

        plugin.MaximumForwardingRequests = 0;
        Assert.Null(plugin.Forwarder);
        plugin.MaximumForwardingRequests = 1;
        var forwarder = plugin.Forwarder!;

        peer.Dispose();
        Assert.Null(plugin.Forwarder);
        Assert.False(forwarder.IsRunning);
    }

    // Samples/Router2: A to B and C, B to C, D to B and C; then the pings settle.
    private void ConnectAsInTheSample()
    {
        _mesh.Connect(_a, _b);
        _mesh.Connect(_a, _c);
        _mesh.Connect(_b, _c);
        _mesh.Connect(_d, _b);
        _mesh.Connect(_d, _c);
        _mesh.Run(TimeSpan.FromMilliseconds(500));
    }

    private SeenEvent WaitFor(RakPeer peer, MessageId id, Func<SeenEvent, bool>? also = null)
    {
        _mesh.RunUntil(() => _mesh.Seen(peer).Any(e => e.Id == (byte)id && (also?.Invoke(e) ?? true)), id.ToString());
        return _mesh.Seen(peer).First(e => e.Id == (byte)id && (also?.Invoke(e) ?? true));
    }

    // Records ID_ROUTER_2_INTERNAL replies ahead of Router2.
    private void Spy(RakPeer peer, List<byte[]> replies)
    {
        var plugin = peer == _a ? _sourcePlugin : _routerBPlugin;
        peer.Plugins.Detach(plugin);
        _mesh.Step();
        peer.Plugins.Attach(new ReplySpy(replies));
        peer.Plugins.Attach(plugin);
        _mesh.Step();
    }

    private sealed class ReplySpy(List<byte[]> replies) : IRakPlugin
    {
        public PluginResult OnEvent(PeerEvent e)
        {
            if (e.Data.Length > 1 && e.Data[0] == (byte)MessageId.Router2Internal && e.Data[1] == 1)
                replies.Add(e.ToArray());
            return PluginResult.Continue;
        }
    }
}
