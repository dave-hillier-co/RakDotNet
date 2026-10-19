using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Two peers connecting to each other at once, from Samples/CrossConnectionTest/CrossConnectionTest.cpp and
/// Samples/Tests/CrossConnectionConvertTest.cpp: the client pings the server, both call Connect at the same moment,
/// and each must end up with a single working connection to the other. The server reaches the client at the address
/// the proxy gives it, so both directions cross the proxy.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class CrossConnectionScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Peers_that_connect_to_each_other_at_the_same_time_end_with_one_connection_each(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var serverGuid = session.ServerStarted.PeerGuid;
        var clientGuid = session.ClientStarted.PeerGuid;

        // CrossConnectionConvertTest.cpp:45 and :71-80: the client pings first, and the server learns its address
        // from the ping.
        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, cancellationToken: Token));
        var ping = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.UnconnectedPing, "ID_UNCONNECTED_PING", InteropSession.DefaultTimeout, Token);
        await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", InteropSession.DefaultTimeout, Token);

        // CrossConnectionConvertTest.cpp:114-119: both connect at once.
        var results = await Task.WhenAll(
            session.Server.ConnectAsync(new Harness.ConnectOptions(ping.Address), Token),
            session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        Assert.All(results, result => Assert.Equal("CONNECTION_ATTEMPT_STARTED", result));

        await ScenarioFlows.WaitForStateAsync(session.Client, serverGuid, "IS_CONNECTED", Token);
        await ScenarioFlows.WaitForStateAsync(session.Server, clientGuid, "IS_CONNECTED", Token);
        // CrossConnectionConvertTest.cpp:56-64 and :121-127: each side is told it is connected, one way or the other.
        await session.ClientInbox.WaitForPacketAsync(IsConnectedNotification(serverGuid), "a connection notification", InteropSession.DefaultTimeout, Token);
        await session.ServerInbox.WaitForPacketAsync(IsConnectedNotification(clientGuid), "a connection notification", InteropSession.DefaultTimeout, Token);

        // "Everything connects and sends normally" (CrossConnectionConvertTest.cpp:13).
        await session.Client.SendAsync(new SendOptions(serverGuid, new byte[] { ScenarioFlows.UserPacket, 1 }), Token);
        await session.Server.SendAsync(new SendOptions(clientGuid, new byte[] { ScenarioFlows.UserPacket, 2 }), Token);
        var atServer = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == ScenarioFlows.UserPacket, "the client's message", InteropSession.DefaultTimeout, Token);
        var atClient = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == ScenarioFlows.UserPacket, "the server's message", InteropSession.DefaultTimeout, Token);
        Assert.Equal(clientGuid, atServer.PeerGuid);
        Assert.Equal(serverGuid, atClient.PeerGuid);
        Assert.Equal(2, atClient.Data.Span[1]);
        await AssertOneConnectionAsync(session.ClientPeer, serverGuid);
        await AssertOneConnectionAsync(session.ServerPeer, clientGuid);
    }

    private static Func<ReceivedPacket, bool> IsConnectedNotification(ulong guid) =>
        p => p.PeerGuid == guid && p.MessageId is (byte)MessageId.ConnectionRequestAccepted or (byte)MessageId.NewIncomingConnection;

    // CrossConnectionTest.cpp's failure was a second, half-open connection; a peer that can list its connections must
    // list exactly one.
    private static async Task AssertOneConnectionAsync(InteropPeer peer, ulong remote)
    {
        if (PeerMatrix.PendingReason(peer.Kind, PeerMatrix.Milestone5.ConnectionQueries) is not null)
        {
            return;
        }
        var connection = Assert.Single((await peer.Driver.GetInfoAsync(Token)).Connections);
        Assert.Equal(remote, connection.PeerGuid);
    }
}
