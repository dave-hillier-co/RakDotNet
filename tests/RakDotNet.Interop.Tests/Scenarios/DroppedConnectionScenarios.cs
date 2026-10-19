using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Losing a connection, from Samples/Dropped Connection Test/DroppedConnectionTest.cpp and
/// Samples/Tests/DroppedConnectionConvertTest.cpp: clients close with or without notification, or vanish, and reconnect;
/// the server must notice every drop within its timeout (SetTimeoutTime(5000), DroppedConnectionConvertTest.cpp:60) and
/// a client must never hold more than one connection.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class DroppedConnectionScenarios
{
    // Short enough to keep the scenarios quick, long enough that loopback pings never trip it.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    // The timeout, then up to a keepalive interval (timeout / 2, RakPeer.cpp:5857-5869) before anything is unacknowledged.
    private static readonly TimeSpan Detection = TimeSpan.FromSeconds(5);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_client_that_closes_without_notification_is_lost_by_the_server_and_can_connect_again(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        await session.Server.SetTimeoutAsync(Timeout, cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // DroppedConnectionConvertTest.cpp case 0: CloseConnection(serverID, false, 0).
        // RakPeer buffers the close for its update thread (RakPeer.cpp:4175-4182, 5642), so the state changes shortly after.
        await session.Client.CloseAsync(serverGuid, notify: false, Token);
        await ScenarioFlows.WaitForStateAsync(session.Client, serverGuid, "IS_NOT_CONNECTED", Token);

        var lost = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionLost, "ID_CONNECTION_LOST", Detection, Token);
        Assert.Equal(clientGuid, lost.PeerGuid);
        Assert.Equal(session.AddressAtServer(session.ClientPeer), lost.Address);

        // Case 1: the client connects again once the server has let go.
        await session.ConnectAsync(Token);
        Assert.Equal("IS_CONNECTED", await session.Server.GetConnectionStateAsync(clientGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Both_sides_report_the_connection_lost_when_the_network_drops_everything(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        await session.Server.SetTimeoutAsync(Timeout, cancellationToken: Token);
        await session.Client.SetTimeoutAsync(Timeout, cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        session.Proxy.Faults = new FaultOptions(LossProbability: 1.0);

        var clientLost = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionLost, "ID_CONNECTION_LOST", Detection, Token);
        var serverLost = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionLost, "ID_CONNECTION_LOST", Detection, Token);
        Assert.Equal(serverGuid, clientLost.PeerGuid);
        Assert.Equal(clientGuid, serverLost.PeerGuid);
        Assert.Equal("IS_NOT_CONNECTED", await session.Client.GetConnectionStateAsync(serverGuid, Token));
        Assert.Equal("IS_NOT_CONNECTED", await session.Server.GetConnectionStateAsync(clientGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_server_that_closes_with_notification_is_reported_to_the_client_as_a_disconnection(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // DroppedConnectionConvertTest.cpp case 2: CloseConnection(serverID, true, 0), here from the server's side.
        await session.Server.CloseAsync(clientGuid, notify: true, Token);

        var disconnected = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", InteropSession.DefaultTimeout, Token);
        Assert.Equal(serverGuid, disconnected.PeerGuid);
        Assert.Equal(session.ProxyEndPoint, disconnected.Address);
        // The client lets go once its acknowledgement of the notification has gone (RakPeer.cpp:6095-6102).
        await ScenarioFlows.WaitForStateAsync(session.Client, serverGuid, "IS_NOT_CONNECTED", Token);
    }
}
