using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

// RakPeer.cpp:5800-5818 and CloseConnectionInternal: by the time Receive hands out ID_DISCONNECTION_NOTIFICATION or
// ID_CONNECTION_LOST the remote system's slot is already closed, so a query made on reading the packet sees the
// connection gone. A Background peer hands events out while its cycle is still running, so the query must not see the
// connection as it was before the close, nor a new connection as missing once its connection event is read.
public sealed class DisconnectVisibilityTests
{
    private const int Rounds = 40;

    [Fact]
    public async Task A_background_peer_reports_a_connection_as_its_event_says_as_soon_as_the_event_is_read()
    {
        var token = TestContext.Current.CancellationToken;
        using var server = RakPeer.Start(Options(maxIncoming: 4));
        using var client = RakPeer.Start(Options(maxIncoming: 0));

        for (var round = 0; round < Rounds; round++)
        {
            var connected = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: token);
            Assert.True(connected.IsConnected);
            var clientGuid = client.RakNetGuid;
            Assert.Equal(ConnectionState.Connected, await WaitForAsync(server, PeerEventKind.Connected, clientGuid, token));

            await connected.Connection!.CloseAsync(notify: true, cancellationToken: token);

            // Read and query on the reader's side with nothing in between, as an application loop does.
            var states = await WaitForAsync(server, PeerEventKind.Disconnected, clientGuid, token);
            Assert.Equal(ConnectionState.NotConnected, states);
            DrainClient(client);
        }
    }

    private static async Task<ConnectionState> WaitForAsync(RakPeer peer, PeerEventKind kind, RakNetGuid guid, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            while (peer.Events.TryRead(out var e))
            {
                if (e.Kind == kind && e.RakNetGuid == guid)
                    return peer.GetConnectionState(guid);
            }
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        }
        throw new TimeoutException($"No {kind} arrived.");
    }

    private static void DrainClient(RakPeer client)
    {
        while (client.Events.TryRead(out _))
        {
        }
    }

    private static RakPeerOptions Options(int maxIncoming) => new()
    {
        MaxConnections = 4,
        MaxIncomingConnections = maxIncoming,
        Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
        Threading = PeerThreading.Background,
    };
}
