using System.Buffers.Binary;
using System.Net;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Router2, from Samples/Router2 and NATCompleteClient's Router2Framework: four systems, A connected to B and C, B to
/// C, and D to B and C, with A and D behind symmetric routers, between which punchthrough fails. A routes to D through
/// a router, B or C, connects through it, and when that router drops A is rerouted through the other. A is of the
/// client's kind; B, C and D are of the server's, so routing is asked for and answered across kinds.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class Router2Scenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_source_routes_to_the_endpoint_through_a_shared_router_and_connects(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (a, b, c, d) = await StartAsInTheSampleAsync(session, client, server);

        await a.Relay().EstablishRoutingAsync(d.Started.PeerGuid, Token);

        // SendForwardingSuccess (Router2.cpp:792-810): from the router, the other's GUID and the forwarding port.
        var established = await a.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Router2ForwardingEstablished, "ID_ROUTER_2_FORWARDING_ESTABLISHED", Timeout, Token);
        var router = established.PeerGuid == b.Started.PeerGuid ? b : c;
        Assert.Contains(established.PeerGuid, new[] { b.Started.PeerGuid, c.Started.PeerGuid });
        var (endpoint, port) = Read(established);
        Assert.Equal(d.Started.PeerGuid, endpoint);
        Assert.Equal(router.Started.BoundAddress, established.Address);
        var rerouted = await d.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Router2Rerouted, "ID_ROUTER_2_REROUTED at the endpoint", Timeout, Token);
        Assert.Equal(router.Started.PeerGuid, rerouted.PeerGuid);
        Assert.Equal((a.Started.PeerGuid, port), Read(rerouted));
        // The mini punch and its bounces are consumed.
        Assert.DoesNotContain(a.Inbox.Unconsumed.Concat(d.Inbox.Unconsumed).Concat(router.Inbox.Unconsumed),
            p => p.MessageId is (byte)MessageId.OutOfBandInternal or (byte)MessageId.Router2Internal);

        // Samples/Router2: connect to the router's address on that port, then send a message to the endpoint.
        var forwarded = new IPEndPoint(established.Address.Address, port);
        await ConnectThroughAsync(a, d, forwarded);
        await a.Driver.SendAsync(new SendOptions(d.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum + 1 }), Token);
        var message = await d.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum + 1, "ID_USER_PACKET_ENUM+1 at the endpoint", Timeout, Token);
        Assert.Equal(a.Started.PeerGuid, message.PeerGuid);
        Assert.Equal(forwarded.Port, message.Address.Port);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task When_the_router_drops_the_source_is_rerouted_through_the_other(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (a, b, c, d) = await StartAsInTheSampleAsync(session, client, server);
        await a.Relay().EstablishRoutingAsync(d.Started.PeerGuid, Token);
        var established = await a.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Router2ForwardingEstablished, "ID_ROUTER_2_FORWARDING_ESTABLISHED", Timeout, Token);
        var (router, other) = established.PeerGuid == b.Started.PeerGuid ? (b, c) : (c, b);
        await ConnectThroughAsync(a, d, new IPEndPoint(established.Address.Address, Read(established).Port));
        await a.WaitForPingsAsync(3, Token);

        // Samples/Router2: "close the router that did the routing to reroute".
        foreach (var peer in new[] { a, d, other })
            await router.Driver.CloseAsync(peer.Started.PeerGuid, notify: true, Token);

        // Forwarding is established again through the other router, and reported to the application as rerouted
        // (Router2.cpp:1130-1145).
        var rerouted = await a.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Router2Rerouted, "ID_ROUTER_2_REROUTED at the source", Timeout, Token);
        Assert.Equal(other.Started.PeerGuid, rerouted.PeerGuid);
        var (endpoint, port) = Read(rerouted);
        Assert.Equal(d.Started.PeerGuid, endpoint);
        await d.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.Router2Rerouted && p.PeerGuid == other.Started.PeerGuid, "ID_ROUTER_2_REROUTED at the endpoint", Timeout, Token);

        // The connection carries on through the other router.
        await a.Driver.SendAsync(new SendOptions(d.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum + 1, 2 }), Token);
        var message = await d.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum + 1, "the message through the other router", Timeout, Token);
        Assert.Equal(port, message.Address.Port);
        Assert.DoesNotContain(a.Inbox.Unconsumed, p => p.MessageId is (byte)MessageId.ConnectionLost or (byte)MessageId.DisconnectionNotification
            && p.PeerGuid == d.Started.PeerGuid);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Without_a_router_connected_to_the_endpoint_the_application_hears_nothing(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var b = session.Server;
        await b.Relay().AttachRouter2Async(1, Token);
        var a = await session.AddClientAsync(client, NatType.Symmetric, Token);
        var d = await session.AddClientAsync(server, NatType.Symmetric, Token);
        await a.Relay().AttachRouter2Async(cancellationToken: Token);
        await d.Relay().AttachRouter2Async(cancellationToken: Token);
        // B is connected to A only.
        await a.ConnectToAsync(b, Token);
        await a.WaitForPingsAsync(1, Token);

        await a.Relay().EstablishRoutingAsync(d.Started.PeerGuid, Token);
        await Task.Delay(TimeSpan.FromSeconds(2), Token);

        // B replies that it cannot forward; with no router left, ID_ROUTER_2_FORWARDING_NO_PATH is pushed as not
        // generated locally, which Router2 consumes (Router2.cpp:337-341), so nothing reaches the application.
        Assert.DoesNotContain(a.Inbox.Unconsumed, p => p.MessageId is (byte)MessageId.Router2ForwardingNoPath or (byte)MessageId.Router2ForwardingEstablished
            or (byte)MessageId.Router2Internal or (byte)MessageId.ConnectionLost);
        Assert.DoesNotContain(b.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.Router2Internal);
    }

    // Samples/Router2: A to B and C, B to C, D to B and C, each running Router2; B and C forward one connection.
    private static async Task<(NatPeer A, NatPeer B, NatPeer C, NatPeer D)> StartAsInTheSampleAsync(NatSession session, PeerKind client, PeerKind server)
    {
        var b = session.Server;
        var c = await session.AddClientAsync(server, null, Token);
        var a = await session.AddClientAsync(client, NatType.Symmetric, Token);
        var d = await session.AddClientAsync(server, NatType.Symmetric, Token);
        await a.Relay().AttachRouter2Async(cancellationToken: Token);
        await b.Relay().AttachRouter2Async(1, Token);
        await c.Relay().AttachRouter2Async(1, Token);
        await d.Relay().AttachRouter2Async(cancellationToken: Token);
        await a.ConnectToAsync(b, Token);
        await a.ConnectToAsync(c, Token);
        await c.ConnectToAsync(b, Token);
        await d.ConnectToAsync(b, Token);
        await d.ConnectToAsync(c, Token);
        // Router2 asks only systems with a ping, and a router needs one to the endpoint.
        await a.WaitForPingsAsync(2, Token);
        await b.WaitForPingsAsync(3, Token);
        await c.WaitForPingsAsync(3, Token);
        await d.WaitForPingsAsync(2, Token);
        return (a, b, c, d);
    }

    private static async Task ConnectThroughAsync(NatPeer source, NatPeer endpoint, IPEndPoint forwarded)
    {
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await source.Driver.ConnectAsync(new Harness.ConnectOptions(forwarded), Token));
        var accepted = await source.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == endpoint.Started.PeerGuid,
            "ID_CONNECTION_REQUEST_ACCEPTED from the endpoint", Timeout, Token);
        Assert.Equal(forwarded, accepted.Address);
        var incoming = await endpoint.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == source.Started.PeerGuid,
            "ID_NEW_INCOMING_CONNECTION from the source", Timeout, Token);
        Assert.Equal(forwarded.Port, incoming.Address.Port);
    }

    // ID_ROUTER_2_FORWARDING_ESTABLISHED and ID_ROUTER_2_REROUTED: a GUID and a port.
    private static (ulong Guid, int Port) Read(ReceivedPacket packet)
    {
        Assert.Equal(11, packet.Data.Length);
        var data = packet.Data.Span;
        return (BinaryPrimitives.ReadUInt64BigEndian(data[1..]), BinaryPrimitives.ReadUInt16BigEndian(data[9..]));
    }
}
