using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class ConnectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Connecting_reports_Connected_on_both_sides_with_a_connection_object()
    {
        using var peers = new ManualPeers();

        var server = peers.Connect();

        var accepted = peers.ClientEvents.Single(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(MessageId.ConnectionRequestAccepted, accepted.Id);
        Assert.False(accepted.IsIncoming);
        Assert.Equal(peers.Server.RakNetGuid, accepted.RakNetGuid);
        Assert.Equal(peers.ServerAddress, accepted.Address);
        var incoming = peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(MessageId.NewIncomingConnection, incoming.Id);
        Assert.True(incoming.IsIncoming);
        Assert.Equal(peers.Client.RakNetGuid, incoming.RakNetGuid);

        Assert.Same(peers.Client, server.Peer);
        Assert.Equal(peers.Server.RakNetGuid, server.RakNetGuid);
        Assert.Equal(peers.ServerAddress, server.Address);
        Assert.False(server.IsIncoming);
        Assert.True(server.IsConnected);
        Assert.Equal(ConnectionState.Connected, server.State);
        Assert.Same(peers.Client.Sockets[0], server.Socket);
        Assert.Equal(1492, server.Mtu);
        Assert.Equal(TimeSpan.FromSeconds(10), server.Timeout);
        Assert.Equal(peers.Client.Sockets[0].LocalAddress, server.ExternalAddress);
        Assert.True(incoming.Connection!.IsIncoming);

        Assert.Equal(1, peers.Client.ConnectionCount);
        Assert.Same(server, Assert.Single(peers.Client.Connections));
        Assert.True(peers.Client.TryGetConnection(peers.Server.RakNetGuid, out var byGuid));
        Assert.Same(server, byGuid);
        Assert.True(peers.Client.TryGetConnection(peers.ServerAddress, out var byAddress));
        Assert.Same(server, byAddress);
        Assert.True(peers.Client.TryGetConnectionByIndex(server.SystemIndex, out var byIndex));
        Assert.Same(server, byIndex);
        Assert.Equal(ConnectionState.Connected, peers.Client.GetConnectionState(peers.Server.RakNetGuid));
        Assert.Equal(ConnectionState.Connected, peers.Client.GetConnectionState(peers.ServerAddress));
        Assert.Equal(peers.ServerAddress, peers.Client.GetAddressFromGuid(peers.Server.RakNetGuid));
        Assert.Equal(peers.Server.RakNetGuid, peers.Client.GetGuidFromAddress(peers.ServerAddress));
        Assert.True(server.Closed.IsCompleted is false);
    }

    [Fact]
    public void A_second_attempt_is_refused_synchronously()
    {
        using var peers = new ManualPeers();

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));
        Assert.Equal(ConnectAttemptResult.ConnectionAttemptAlreadyInProgress, peers.Client.Connect(peers.ServerAddress));
        Assert.Equal(ConnectionState.Pending, peers.Client.GetConnectionState(peers.ServerAddress));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.Connected), "connected");

        Assert.Equal(ConnectAttemptResult.AlreadyConnectedToEndpoint, peers.Client.Connect(peers.ServerAddress));
        Assert.Throws<ArgumentException>(() => peers.Client.Connect(new SystemAddress(IPAddress.Loopback, 0)));
    }

    [Fact]
    public void A_server_with_no_free_incoming_slots_refuses_the_attempt()
    {
        using var peers = new ManualPeers(server: o => o with { MaxIncomingConnections = 0 });

        peers.Client.Connect(peers.ServerAddress);
        peers.RunUntil(() => peers.ClientEvents.Count > 0, "a refusal");

        var refused = Assert.Single(peers.ClientEvents);
        Assert.Equal(PeerEventKind.ConnectFailed, refused.Kind);
        Assert.Equal(ConnectFailure.NoFreeIncomingConnections, refused.ConnectFailure);
        Assert.Equal(MessageId.NoFreeIncomingConnections, refused.Id);
        Assert.Equal(ConnectionState.NotConnected, peers.Client.GetConnectionState(peers.ServerAddress));
    }

    [Fact]
    public void An_unanswered_attempt_fails_after_its_attempts_on_the_peers_clock()
    {
        using var peers = new ManualPeers();
        using var silent = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        peers.Client.Connect((IPEndPoint)silent.LocalEndPoint!, new ConnectOptions { AttemptCount = 2, AttemptInterval = TimeSpan.FromMilliseconds(100) });
        for (var i = 0; i < 20; i++)
            peers.Step(TimeSpan.FromMilliseconds(50));

        var failed = Assert.Single(peers.ClientEvents);
        Assert.Equal(ConnectFailure.ConnectionAttemptFailed, failed.ConnectFailure);
        Assert.Equal([(byte)MessageId.ConnectionAttemptFailed], failed.Data);
    }

    [Fact]
    public async Task ConnectAsync_completes_with_the_connection()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, MaxIncomingConnections = 2, Bindings = [ManualPeers.Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var result = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token);

        Assert.Equal(ConnectAttemptResult.Started, result.AttemptResult);
        Assert.True(result.IsConnected);
        Assert.Equal(server.RakNetGuid, result.EnsureConnected().RakNetGuid);
        Assert.True(await client.Events.WaitToReadAsync(Token));
        Assert.True(client.Events.TryRead(out var e));
        Assert.Equal(PeerEventKind.Connected, e.Kind);
        Assert.Same(result.Connection, e.Connection);
    }

    [Fact]
    public async Task ConnectAsync_by_host_name_resolves_it()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, MaxIncomingConnections = 2, Bindings = [ManualPeers.Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var result = await client.ConnectAsync("127.0.0.1", server.Sockets[0].LocalAddress.Port, cancellationToken: Token);

        Assert.True(result.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_reports_a_refusal_and_a_synchronous_rejection()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });

        var refused = client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token);
        var duplicate = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token);

        Assert.Equal(ConnectAttemptResult.ConnectionAttemptAlreadyInProgress, duplicate.AttemptResult);
        Assert.Null(duplicate.Failure);
        var result = await refused;
        Assert.Equal(ConnectFailure.NoFreeIncomingConnections, result.Failure);
        Assert.False(result.IsConnected);
    }

    [Fact]
    public async Task Cancelling_ConnectAsync_throws_and_stops_the_attempt_without_an_event()
    {
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        using var silent = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Udp);
        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await client.ConnectAsync((IPEndPoint)silent.LocalEndPoint!, new ConnectOptions { AttemptCount = 100 }, cancel.Token));

        await Task.Delay(100, Token);
        Assert.Equal(ConnectionState.NotConnected, client.GetConnectionState((IPEndPoint)silent.LocalEndPoint!));
        Assert.False(client.Events.TryRead(out _));
    }

    [Fact]
    public void A_queued_attempt_can_be_cancelled()
    {
        using var peers = new ManualPeers();

        peers.Client.Connect(peers.ServerAddress);
        peers.Client.CancelConnectionAttempt(peers.ServerAddress);
        peers.Settle();

        Assert.Empty(peers.ClientEvents);
        Assert.Empty(peers.ServerEvents);
        Assert.Equal(ConnectionState.NotConnected, peers.Client.GetConnectionState(peers.ServerAddress));
    }

    [Fact]
    public void Cancelling_frees_the_address_for_a_new_attempt_at_once()
    {
        using var peers = new ManualPeers();

        // RakPeer.cpp:1686-1710 takes the attempt out of requestedConnectionQueue under its mutex, on the caller.
        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));
        peers.Client.CancelConnectionAttempt(peers.ServerAddress);
        Assert.Equal(ConnectionState.NotConnected, peers.Client.GetConnectionState(peers.ServerAddress));
        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));

        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.Connected), "connected");
        Assert.DoesNotContain(peers.ClientEvents, e => e.Kind == PeerEventKind.ConnectFailed);
    }

    [Fact]
    public void Messages_arrive_in_order_before_the_disconnection_a_notifying_close_produces()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        server.Send(new byte[] { 0x86, 1 }, Reliability.ReliableOrdered);
        server.Send(new byte[] { 0x86, 2 }, Reliability.ReliableOrdered);
        server.Close();
        Assert.Equal(ConnectionState.Disconnecting, server.State);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Disconnected) && peers.ClientEvents.Any(e => e.Kind == PeerEventKind.Disconnected), "both disconnected");

        var serverSide = peers.ServerEvents.Where(e => e.Kind != PeerEventKind.Connected).ToList();
        Assert.Equal([[0x86, 1], [0x86, 2], [(byte)MessageId.DisconnectionNotification]], serverSide.Select(e => e.Data));
        Assert.Equal(DisconnectReason.DisconnectionNotification, serverSide[2].DisconnectReason);
        Assert.False(serverSide[2].ClosedLocally);
        var clientSide = peers.ClientEvents.Single(e => e.Kind == PeerEventKind.Disconnected);
        Assert.True(clientSide.ClosedLocally);
        Assert.Same(server, clientSide.Connection);
        Assert.Equal(ConnectionState.Disconnected, server.State);
        Assert.True(server.Closed.IsCompleted);
        Assert.Equal(0, peers.Client.ConnectionCount);
        Assert.Equal(SendStatus.NotConnected, server.Send(new byte[] { 0x86 }, Reliability.Reliable).Status);
    }

    [Fact]
    public void A_disconnecting_connection_is_not_counted_or_listed()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        server.Close();
        // Only the client runs, so the server never acknowledges the notification and the close cannot finish.
        peers.Client.Update();
        peers.Client.Update();

        // NumberOfConnections and GetConnectionList count only CONNECTED systems (RakPeer.cpp:920-926, 1832-1850).
        Assert.Equal(ConnectionState.Disconnecting, server.State);
        Assert.Equal(0, peers.Client.ConnectionCount);
        Assert.Empty(peers.Client.Connections);
        Assert.Equal(1, peers.Server.ConnectionCount);
    }

    [Fact]
    public void A_silent_close_reports_ConnectionLost_at_once_and_abandons_outstanding_receipts_first()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();
        peers.ClientEvents.Clear();

        var sent = server.Send(new byte[] { 0x86 }, Reliability.ReliableOrderedWithAckReceipt);
        server.Close(notify: false);
        peers.Client.Update();
        ManualPeers.Drain(peers.Client, peers.ClientEvents);

        Assert.Equal([PeerEventKind.ReceiptAbandoned, PeerEventKind.Disconnected], peers.ClientEvents.Select(e => e.Kind));
        Assert.Equal(sent.Receipt, peers.ClientEvents[0].Receipt);
        Assert.Empty(peers.ClientEvents[0].Data);
        var lost = peers.ClientEvents[1];
        Assert.Equal(DisconnectReason.ConnectionLost, lost.DisconnectReason);
        Assert.True(lost.IsLocal);
        Assert.True(lost.ClosedLocally);
        Assert.Equal([(byte)MessageId.ConnectionLost], lost.Data);
    }

    [Fact]
    public async Task CloseAsync_completes_when_the_connection_ends()
    {
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, MaxIncomingConnections = 2, Bindings = [ManualPeers.Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 2, Bindings = [ManualPeers.Loopback] });
        var connection = (await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token)).EnsureConnected();

        await connection.CloseAsync(cancellationToken: Token);

        Assert.Equal(ConnectionState.Disconnected, connection.State);
        Assert.True(connection.Closed.IsCompleted);
    }
}
