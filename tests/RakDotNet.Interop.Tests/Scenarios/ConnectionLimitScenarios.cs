using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Connection limits and churn, from Samples/Tests/MaximumConnectTest.cpp (more peers connect than the limit allows; no
/// peer may end up over it), ManyClientsOneServerBlockingTest.cpp, ManyClientsOneServerNonBlockingTest.cpp and
/// ManyClientsOneServerDeallocateBlockingTest.cpp (many clients connect, disconnect and connect again),
/// PeerConnectDisconnectTest.cpp and PeerConnectDisconnectWithCancelPendingTest.cpp (the same between peers, with
/// Connect's results checked and pending attempts cancelled).
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ConnectionLimitScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Clients_beyond_the_incoming_limit_are_refused_until_a_slot_frees(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, serverOptions: new StartupOptions(MaxIncoming: 2), cancellationToken: Token);
        var second = await session.AddClientAsync(client, Token);
        var third = await session.AddClientAsync(client, Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);
        await session.ConnectAsync(second, Token);

        await ConnectionLimitFlows.ConnectBeyondTheLimitAsync(session, third, Token);

        // A slot frees when a connected client leaves, and the refused client gets in.
        await session.Client.CloseAsync(serverGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.DisconnectionNotification && p.PeerGuid == session.ClientStarted.PeerGuid, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        await ScenarioFlows.WaitForStateAsync(session.Server, session.ClientStarted.PeerGuid, "IS_NOT_CONNECTED", Token);
        await session.ConnectAsync(third, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Many_clients_connect_disconnect_and_connect_again(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        List<InteropPeer> clients = [session.ClientPeer];
        for (var i = 1; i < 4; i++)
        {
            clients.Add(await session.AddClientAsync(client, Token));
        }

        // ManyClientsOneServerBlockingTest.cpp:131-170: connect everyone, disconnect everyone with notification, and
        // connect again once each client's connection is gone (ConnectionStateMatchesOptions), several times over.
        for (var round = 0; round < 3; round++)
        {
            await ConnectAllAsync(session, clients);
            foreach (var peer in clients)
            {
                await peer.Driver.CloseAsync(session.ServerStarted.PeerGuid, notify: true, Token);
            }
            foreach (var peer in clients)
            {
                await session.ServerInbox.WaitForPacketAsync(
                    p => p.MessageId == (byte)MessageId.DisconnectionNotification && p.PeerGuid == peer.Started.PeerGuid,
                    $"ID_DISCONNECTION_NOTIFICATION from {peer.Label}",
                    Timeout,
                    Token);
                await ScenarioFlows.WaitForStateAsync(peer.Driver, session.ServerStarted.PeerGuid, "IS_NOT_CONNECTED", Token);
                await ScenarioFlows.WaitForStateAsync(session.Server, peer.Started.PeerGuid, "IS_NOT_CONNECTED", Token);
            }
        }

        // "Then allow them all to connect for one last time" (ManyClientsOneServerBlockingTest.cpp:91).
        await ConnectAllAsync(session, clients);
        foreach (var peer in clients)
        {
            Assert.Equal("IS_CONNECTED", await session.Server.GetConnectionStateAsync(peer.Started.PeerGuid, Token));
        }
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Connect_reports_an_attempt_already_in_progress_and_a_connection_that_already_exists(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // Nothing reaches the server, so the first attempt stays pending.
        session.Proxy.Faults = new FaultOptions(LossProbability: 1.0);

        // PeerConnectDisconnectWithCancelPendingTest.cpp:117-136 and RakPeer.cpp:3275-3314 (SendConnectionRequest): a second Connect to an
        // address with an attempt pending is refused, and one to an address already connected is too.
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        Assert.Equal("CONNECTION_ATTEMPT_ALREADY_IN_PROGRESS", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));

        session.Proxy.Faults = FaultOptions.None;
        var accepted = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED from a retried attempt", Timeout, Token);
        Assert.Equal("ALREADY_CONNECTED_TO_ENDPOINT", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        Assert.Equal("IS_CONNECTED", await session.Client.GetConnectionStateAsync(accepted.PeerGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_cancelled_attempt_is_dropped_silently_and_can_be_started_again(PeerKind client, PeerKind server)
    {
        PeerMatrix.SkipUntilImplemented(client, PeerMatrix.Milestone4.CancelConnectionAttempt);
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        session.Proxy.Faults = new FaultOptions(LossProbability: 1.0);
        // Two attempts 100 ms apart fail within half a second unless cancelled.
        var shortAttempt = new Harness.ConnectOptions(session.ProxyEndPoint, Attempts: 2, AttemptIntervalMs: 100);

        // PeerConnectDisconnectWithCancelPendingTest.cpp:105-115: CancelConnectionAttempt before connecting again.
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(shortAttempt, Token));
        await session.Client.CancelConnectionAttemptAsync(session.ProxyEndPoint, Token);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));

        session.Proxy.Faults = FaultOptions.None;
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", Timeout, Token);
        // RakPeer.cpp:1686-1707 removes the cancelled attempt without a word, so the only attempt that ended is the
        // one that connected, long after the cancelled one would have failed.
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId == (byte)MessageId.ConnectionAttemptFailed);
    }

    // ManyClientsOneServerNonBlockingTest.cpp: every client's Connect goes out before any result is awaited.
    private static async Task ConnectAllAsync(InteropSession session, List<InteropPeer> clients)
    {
        foreach (var peer in clients)
        {
            Assert.Equal("CONNECTION_ATTEMPT_STARTED", await peer.Driver.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        }
        foreach (var peer in clients)
        {
            await peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", Timeout, Token);
            await session.ServerInbox.WaitForPacketAsync(
                p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == peer.Started.PeerGuid,
                $"ID_NEW_INCOMING_CONNECTION from {peer.Label}",
                Timeout,
                Token);
        }
    }
}

/// <summary>The steps and assertions of the connection limit scenarios, shared with the golden vector capture.</summary>
public static class ConnectionLimitFlows
{
    /// <summary>
    /// With the server's incoming connections full, <paramref name="extra"/> connects and is refused:
    /// RakPeer.cpp:5341-5357 answers ID_OPEN_CONNECTION_REQUEST_2 with ID_NO_FREE_INCOMING_CONNECTIONS, and the client
    /// reports it as the attempt's result (RakPeer.cpp:5075-5126), from the server's GUID.
    /// </summary>
    public static async Task ConnectBeyondTheLimitAsync(InteropSession session, InteropPeer extra, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(extra);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await extra.Driver.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), cancellationToken));

        var refused = await extra.Inbox.WaitForPacketAsync(
            p => p.MessageId is (byte)MessageId.NoFreeIncomingConnections or (byte)MessageId.ConnectionRequestAccepted,
            "ID_NO_FREE_INCOMING_CONNECTIONS",
            InteropSession.DefaultTimeout,
            cancellationToken);
        Assert.Equal("ID_NO_FREE_INCOMING_CONNECTIONS", refused.MessageName);
        Assert.Equal(session.ServerStarted.PeerGuid, refused.PeerGuid);
        Assert.Equal(session.ProxyEndPoint, refused.Address);
        Assert.Equal("IS_NOT_CONNECTED", await extra.Driver.GetConnectionStateAsync(session.ServerStarted.PeerGuid, cancellationToken));
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == extra.Started.PeerGuid);
    }
}
