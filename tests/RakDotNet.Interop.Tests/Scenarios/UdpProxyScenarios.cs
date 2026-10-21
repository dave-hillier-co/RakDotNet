using System.Net;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// The UDP proxy fallback of Samples/NATCompleteClient (UDPProxyClientFramework) and NATCompleteServer
/// (UDPProxyCoordinatorFramework and UDPProxyServerFramework). The server runs the coordinator and a proxy server that
/// logs into it on the same peer, as NATCompleteServer does, with its public address set to 127.0.0.1 as the sample's
/// note for a coordinator on the same LAN asks. Two clients behind routers that punchthrough cannot get through ask for
/// forwarding and connect through the proxy. The sender is of the client's kind, the recipient and server of the
/// server's.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class UdpProxyScenarios
{
    private const string Password = "a";

    // NATCompleteClient asks for UDP_FORWARDER_MAXIMUM_TIMEOUT.
    private static readonly TimeSpan ForwardingTimeout = TimeSpan.FromMilliseconds(60000 * 10);
    private static readonly TimeSpan PunchthroughTimeout = TimeSpan.FromSeconds(20);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task When_punchthrough_fails_the_sender_connects_to_the_recipient_through_the_proxy(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await session.Server.Nat.AttachNatPunchthroughServerAsync(Token);
        await StartProxyAsync(session);
        var (sender, senderAddress) = await AddClientAsync(session, client, NatType.Symmetric);
        var (recipient, recipientAddress) = await AddClientAsync(session, server, NatType.Symmetric);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);
        await recipient.Nat.AttachNatPunchthroughClientAsync(Token);

        // Two symmetric routers: punchthrough fails (NatTypeDetectionCommon.cpp:21-37).
        Assert.True(await sender.Nat.OpenNatAsync(recipient.Started.PeerGuid, session.ServerAddress, Token));
        var punched = await sender.Inbox.WaitForPacketAsync(
            p => p.MessageId is (byte)MessageId.NatPunchthroughFailed or (byte)MessageId.NatPunchthroughSucceeded, "the punchthrough result", PunchthroughTimeout, Token);
        Assert.Equal((byte)MessageId.NatPunchthroughFailed, punched.MessageId);

        // UDPProxyClientFramework: forwarding to the recipient's GUID through the coordinator.
        Assert.True(await sender.Relay().RequestForwardingAsync(session.ServerAddress, recipient.Started.PeerGuid, ForwardingTimeout, cancellationToken: Token));
        var success = await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnForwardingSuccess", "OnForwardingSuccess", Timeout, Token);
        var notification = await recipient.WaitForUdpProxyCallbackAsync(c => c.Name == "OnForwardingNotification", "OnForwardingNotification", Timeout, Token);

        // The server's public address and forwarding port; the source is the sender as the coordinator sees it.
        Assert.Equal("127.0.0.1", success.ProxyIp);
        Assert.NotEqual(0, success.ProxyPort);
        Assert.Equal(session.ServerAddress, success.Coordinator);
        Assert.Equal(senderAddress, success.Source);
        Assert.Equal(recipientAddress, success.Target);
        Assert.Equal(recipient.Started.PeerGuid, success.TargetGuid);
        Assert.Equal(success with { Name = "OnForwardingNotification" }, notification);

        // "We can now communicate with the other system, including connecting."
        var proxy = new IPEndPoint(IPAddress.Parse(success.ProxyIp!), success.ProxyPort);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await sender.Driver.ConnectAsync(new Harness.ConnectOptions(proxy), Token));
        var accepted = await sender.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == recipient.Started.PeerGuid,
            "ID_CONNECTION_REQUEST_ACCEPTED from the recipient", Timeout, Token);
        var incoming = await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == sender.Started.PeerGuid,
            "ID_NEW_INCOMING_CONNECTION from the sender", Timeout, Token);
        Assert.Equal(proxy, accepted.Address);
        Assert.Equal(proxy.Port, incoming.Address.Port);
        await sender.Driver.SendAsync(new SendOptions(recipient.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum, 0x50, 0x58 }), Token);
        var message = await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum, "the sender's message", Timeout, Token);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 0x50, 0x58], message.Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_proxy_server_logs_in_only_with_the_coordinators_password(PeerKind client, PeerKind server)
    {
        // The coordinator is of the server's kind and the proxy server, connected to it, of the client's.
        await using var session = await NatSession.StartAsync(server, Token);
        var coordinator = session.Server.Relay();
        await coordinator.AttachUdpProxyCoordinatorAsync("", Token);
        var proxyServer = await session.AddClientAsync(client, null, Token);
        await session.ConnectAsync(proxyServer, Token);
        await proxyServer.Relay().AttachUdpProxyServerAsync("127.0.0.1", cancellationToken: Token);

        // UDPProxyCoordinator.cpp:296-333, the password returned in each reply.
        Assert.True(await proxyServer.Relay().LoginToCoordinatorAsync(Password, session.ServerAddress, Token));
        await proxyServer.WaitForUdpProxyCallbackAsync(c => c.Name == "OnNoPasswordSet" && c.Password == Password, "OnNoPasswordSet", Timeout, Token);
        await coordinator.AttachUdpProxyCoordinatorAsync(Password, Token);
        Assert.True(await proxyServer.Relay().LoginToCoordinatorAsync("b", session.ServerAddress, Token));
        await proxyServer.WaitForUdpProxyCallbackAsync(c => c.Name == "OnWrongPassword" && c.Password == "b", "OnWrongPassword", Timeout, Token);
        Assert.True(await proxyServer.Relay().LoginToCoordinatorAsync(Password, session.ServerAddress, Token));
        await proxyServer.WaitForUdpProxyCallbackAsync(c => c.Name == "OnLoginSuccess" && c.Password == Password, "OnLoginSuccess", Timeout, Token);

        // Logged in: another login is not sent.
        Assert.False(await proxyServer.Relay().LoginToCoordinatorAsync(Password, session.ServerAddress, Token));
        Assert.Equal(["OnNoPasswordSet", "OnWrongPassword", "OnLoginSuccess"], (await proxyServer.Relay().GetUdpProxyCallbacksAsync(Token)).Select(c => c.Name));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_request_is_refused_without_servers_or_recipient_and_reported_in_progress_once_made(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await session.Server.Relay().AttachUdpProxyCoordinatorAsync(Password, Token);
        var (sender, senderAddress) = await AddClientAsync(session, client, NatType.Symmetric);
        var (recipient, recipientAddress) = await AddClientAsync(session, server, NatType.PortRestricted);

        // No server has logged in.
        await sender.Relay().RequestForwardingAsync(session.ServerAddress, recipient.Started.PeerGuid, ForwardingTimeout, cancellationToken: Token);
        var none = await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnNoServersOnline", "OnNoServersOnline", Timeout, Token);
        Assert.Equal(new UdpProxyCallback("OnNoServersOnline", Coordinator: session.ServerAddress, Source: senderAddress, Target: recipientAddress,
            TargetGuid: recipient.Started.PeerGuid), none);

        await StartProxyAsync(session);
        // A GUID the coordinator does not know: GetSystemAddressFromGuid gives UNASSIGNED_SYSTEM_ADDRESS.
        await sender.Relay().RequestForwardingAsync(session.ServerAddress, 0x0102030405060708, ForwardingTimeout, cancellationToken: Token);
        var unknown = await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnRecipientNotConnected", "OnRecipientNotConnected", Timeout, Token);
        Assert.Equal(new UdpProxyCallback("OnRecipientNotConnected", Coordinator: session.ServerAddress, Source: senderAddress, TargetGuid: 0x0102030405060708), unknown);

        await sender.Relay().RequestForwardingAsync(session.ServerAddress, recipient.Started.PeerGuid, ForwardingTimeout, cancellationToken: Token);
        await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnForwardingSuccess", "OnForwardingSuccess", Timeout, Token);

        // The request is kept for the forwarding timeout after success, so another for the pair, or from the recipient
        // for the reverse pair, is in progress, with no address and port 0 (UDPProxyCoordinator.cpp:213-230).
        await sender.Relay().RequestForwardingAsync(session.ServerAddress, recipient.Started.PeerGuid, ForwardingTimeout, cancellationToken: Token);
        var inProgress = await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnForwardingInProgress", "OnForwardingInProgress", Timeout, Token);
        Assert.Equal(new UdpProxyCallback("OnForwardingInProgress", "", 0, session.ServerAddress, senderAddress, recipientAddress, recipient.Started.PeerGuid), inProgress);
        await recipient.Relay().RequestForwardingAsync(session.ServerAddress, sender.Started.PeerGuid, ForwardingTimeout, cancellationToken: Token);
        await recipient.WaitForUdpProxyCallbackAsync(c => c.Name == "OnForwardingInProgress", "OnForwardingInProgress at the recipient", Timeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task When_the_only_server_has_no_entries_left_all_servers_are_busy(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await StartProxyAsync(session, maxForwardEntries: 1);
        var (sender, _) = await AddClientAsync(session, client, NatType.Symmetric);

        // Targets named by address need not be connected; the forwarder takes one more than its maximum.
        for (var i = 1; i <= 3; i++)
            await sender.Relay().RequestForwardingToAddressAsync(session.ServerAddress, new IPEndPoint(IPAddress.Loopback, 1000 + i), ForwardingTimeout, cancellationToken: Token);
        await sender.WaitForUdpProxyCallbackAsync(c => c.Name == "OnAllServersBusy", "OnAllServersBusy", Timeout, Token);

        var calls = await sender.Relay().GetUdpProxyCallbacksAsync(Token);
        Assert.Equal(["OnForwardingSuccess", "OnForwardingSuccess", "OnAllServersBusy"], calls.Select(c => c.Name));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, 1003), calls[2].Target);
        Assert.NotEqual(calls[0].ProxyPort, calls[1].ProxyPort);
    }

    // NATCompleteServer: the coordinator, and a proxy server on the same peer logged into it at its own address.
    private static async Task StartProxyAsync(NatSession session, int? maxForwardEntries = null)
    {
        var server = session.Server;
        await server.Relay().AttachUdpProxyCoordinatorAsync(Password, Token);
        await server.Relay().AttachUdpProxyServerAsync("127.0.0.1", maxForwardEntries, Token);
        Assert.True(await server.Relay().LoginToCoordinatorAsync(Password, cancellationToken: Token));
        await server.WaitForUdpProxyCallbackAsync(c => c.Name == "OnLoginSuccess", "OnLoginSuccess", Timeout, Token);
    }

    private static async Task<(NatPeer Peer, IPEndPoint AtServer)> AddClientAsync(NatSession session, PeerKind kind, NatType router)
    {
        var peer = await session.AddClientAsync(kind, router, Token);
        await peer.Relay().AttachUdpProxyClientAsync(Token);
        var (_, address) = await session.ConnectAsync(peer, Token);
        return (peer, address);
    }
}
