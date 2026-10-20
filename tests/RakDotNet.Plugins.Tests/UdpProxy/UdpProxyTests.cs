using RakDotNet.Plugins.Tests.NatPunchthrough;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.UdpProxy;

/// <summary>
/// UDPProxyClient, UDPProxyServer and UDPProxyCoordinator, from the proxy fallback of Samples/NATCompleteClient
/// (UDPProxyClientFramework) and NATCompleteServer (UDPProxyCoordinatorFramework, UDPProxyServerFramework): two clients
/// behind symmetric routers, which punchthrough cannot get through, connect through a proxy server the coordinator
/// finds. Proxy servers are on 127.0.0.1, because the in-memory network sends from a socket bound to every address as
/// 127.0.0.1.
/// </summary>
public sealed class UdpProxyTests : IDisposable
{
    private const string CoordinatorAddress = "203.0.113.1";
    private const string Password = "a";
    private static readonly TimeSpan ForwardingTimeout = TimeSpan.FromSeconds(10);

    private readonly NatMesh _mesh = new();
    private readonly UdpProxyCoordinator _coordinatorPlugin = new();
    private readonly RakPeer _coordinator;

    public UdpProxyTests()
    {
        _coordinator = _mesh.Add(CoordinatorAddress, plugins: _coordinatorPlugin);
    }

    public void Dispose() => _mesh.Dispose();

    private SystemAddress Coordinator => _coordinator.Sockets[0].LocalAddress;

    [Fact]
    public void A_server_cannot_log_in_until_the_coordinator_has_a_password()
    {
        var (server, plugin, results) = AddServer();

        Assert.True(plugin.LoginToCoordinator(Password, Coordinator));
        _mesh.RunUntil(() => results.Calls.Count > 0, "the login result");

        Assert.Equal(["OnNoPasswordSet(a)"], results.Calls);
        Assert.Empty(_coordinatorPlugin.Servers);
        // The login is over, so it may be tried again.
        Assert.True(plugin.LoginToCoordinator(Password, Coordinator));
        _ = server;
    }

    [Fact]
    public void A_server_with_the_wrong_password_is_refused_and_one_with_the_right_one_is_listed()
    {
        _coordinatorPlugin.RemoteLoginPassword = Password;
        var (server, plugin, results) = AddServer();

        plugin.LoginToCoordinator("b", Coordinator);
        _mesh.RunUntil(() => results.Calls.Count == 1, "the first login result");
        plugin.LoginToCoordinator(Password, Coordinator);
        _mesh.RunUntil(() => results.Calls.Count == 2, "the second login result");

        Assert.Equal(["OnWrongPassword(b)", "OnLoginSuccess(a)"], results.Calls);
        Assert.Equal([_mesh.Seen(_coordinator).Single(e => e.Kind == PeerEventKind.Connected).Address], _coordinatorPlugin.Servers);
        Assert.Equal(server.RakNetGuid, _mesh.Seen(_coordinator).Single(e => e.Kind == PeerEventKind.Connected).RakNetGuid);
        // Logged in: a second login is not sent.
        Assert.False(plugin.LoginToCoordinator(Password, Coordinator));
    }

    [Fact]
    public void A_server_that_forgot_its_login_is_told_it_is_already_logged_in()
    {
        _coordinatorPlugin.RemoteLoginPassword = Password;
        var (server, plugin, results) = AddServer();
        plugin.LoginToCoordinator(Password, Coordinator);
        _mesh.RunUntil(() => results.Calls.Count == 1, "the login result");

        // Detaching runs OnRakPeerShutdown, which forgets the coordinators.
        server.Plugins.Detach(plugin);
        _mesh.Settle();
        server.Plugins.Attach(plugin);
        _mesh.Settle();
        Assert.True(plugin.LoginToCoordinator(Password, Coordinator));
        _mesh.RunUntil(() => results.Calls.Count == 2, "the second login result");

        Assert.Equal(["OnLoginSuccess(a)", "OnAlreadyLoggedIn(a)"], results.Calls);
        Assert.Single(_coordinatorPlugin.Servers);
    }

    [Fact]
    public void A_request_is_refused_without_a_connection_to_the_coordinator_or_a_result_handler()
    {
        var plugin = new UdpProxyClient();
        var client = _mesh.Add("10.0.0.2", plugins: plugin);
        var target = new RakNetGuid(5);

        Assert.False(plugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, target, ForwardingTimeout));
        _mesh.Connect(client, _coordinator);
        Assert.False(plugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, target, ForwardingTimeout));
        plugin.ResultHandler = new ClientResults();
        Assert.True(plugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, target, ForwardingTimeout));
    }

    [Fact]
    public void With_no_server_logged_in_the_client_is_told_none_are_online()
    {
        var (sender, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, _) = AddClient("10.0.1.2", "198.51.100.2");

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Calls.Count > 0, "the result");

        var external = ExternalAddress(sender);
        Assert.Equal([$"OnNoServersOnline({Coordinator}, {external}, {ExternalAddress(recipient)}, {recipient.RakNetGuid})"], senderResults.Calls);
    }

    [Fact]
    public void A_target_not_connected_to_the_coordinator_is_reported()
    {
        LogInServer();
        var (_, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var stranger = new RakNetGuid(0x0102030405060708);

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, stranger, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Calls.Count > 0, "the result");

        // GetSystemAddressFromGuid of an unknown GUID is UNASSIGNED_SYSTEM_ADDRESS.
        Assert.Single(senderResults.Calls);
        Assert.StartsWith("OnRecipientNotConnected(", senderResults.Calls[0]);
        Assert.EndsWith($", {SystemAddress.Unassigned}, {stranger})", senderResults.Calls[0]);
    }

    [Fact]
    public void Clients_behind_symmetric_routers_connect_through_the_proxy_server()
    {
        var (serverPeer, _) = LogInServer();
        var (sender, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, recipientResults) = AddClient("10.0.1.2", "198.51.100.2");

        Assert.True(senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout));
        _mesh.RunUntil(() => senderResults.Successes.Count > 0 && recipientResults.Notifications.Count > 0, "success and notification");

        var success = senderResults.Successes.Single();
        var notification = recipientResults.Notifications.Single();
        // The server's address as the coordinator sees it, and its forwarding port.
        Assert.Equal("127.0.0.1", success.ProxyIPAddress);
        Assert.Equal(serverPeer.Sockets[0].LocalAddress.ToString(writePort: false), success.ProxyIPAddress);
        Assert.Equal(success with { Callback = "OnForwardingNotification" }, notification);
        Assert.Equal(ExternalAddress(sender), success.SourceAddress);
        Assert.Equal(ExternalAddress(recipient), success.TargetAddress);
        Assert.Equal(recipient.RakNetGuid, success.TargetGuid);
        Assert.Equal(Coordinator, success.ProxyCoordinator);

        // NATCompleteClient: connect to the proxy, which the target receives.
        var proxy = SystemAddress.Parse($"{success.ProxyIPAddress}:{success.ProxyPort}", null);
        _mesh.Connect(sender, proxy, recipient);
        var incoming = _mesh.Seen(recipient).Last(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(sender.RakNetGuid, incoming.RakNetGuid);
        Assert.Equal(proxy.Port, incoming.Address.Port);
        sender.Send(new byte[] { (byte)MessageId.UserPacketEnum, 0x50 }, recipient.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(recipient).Any(e => e.Id == (byte)MessageId.UserPacketEnum), "the message through the proxy");
    }

    [Fact]
    public void A_second_request_for_the_pair_or_its_reverse_is_in_progress()
    {
        LogInServer();
        var (_, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, recipientPlugin, recipientResults) = AddClient("10.0.1.2", "198.51.100.2");
        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Successes.Count > 0, "success");

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        recipientPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, _mesh.Seen(_coordinator).First(e => e.Kind == PeerEventKind.Connected).RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.InProgress.Count > 0, "in progress");

        // UDPProxyCoordinator.cpp:213-230: no server address and port 0.
        var inProgress = senderResults.InProgress.Single();
        Assert.Equal("", inProgress.ProxyIPAddress);
        Assert.Equal(0, inProgress.ProxyPort);
        _ = recipientResults;
    }

    [Fact]
    public void A_request_is_forgotten_once_the_forwarding_timeout_has_passed_after_success()
    {
        LogInServer();
        var (_, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, _) = AddClient("10.0.1.2", "198.51.100.2");
        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Successes.Count > 0, "success");

        _mesh.Run(ForwardingTimeout + TimeSpan.FromMilliseconds(50));
        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Calls.Count > 1, "the second result");

        // The forwarder dropped its entry too, so the server forwards again.
        Assert.StartsWith("OnForwardingSuccess(", senderResults.Calls[1]);
    }

    [Fact]
    public void When_the_only_server_is_out_of_entries_all_servers_are_busy()
    {
        var (_, server) = LogInServer();
        server.MaxForwardEntries = 1;
        var (_, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");

        // Targets named by address need no connection. One more than the maximum fits.
        for (var i = 1; i <= 3; i++)
            senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, SystemAddress.Parse($"192.0.2.{i}:1000", null), ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Calls.Count == 3, "three results");

        Assert.StartsWith("OnForwardingSuccess(", senderResults.Calls[0]);
        Assert.StartsWith("OnForwardingSuccess(", senderResults.Calls[1]);
        Assert.StartsWith("OnAllServersBusy(", senderResults.Calls[2]);
    }

    [Fact]
    public void With_two_servers_both_clients_ping_them_and_a_server_is_tried_after_three_seconds()
    {
        LogInServer();
        LogInServer();
        var (sender, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, recipientResults) = AddClient("10.0.1.2", "198.51.100.2");
        var start = _mesh.Time.GetUtcNow();

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.RunUntil(() => senderResults.Successes.Count > 0 && recipientResults.Notifications.Count > 0, "success and notification");

        // The ping request reaches the application, and both clients' pongs are consumed.
        static bool IsPingRequest(SeenEvent e) => e.Id == (byte)MessageId.UdpProxyGeneral && e.Data[1] == (byte)UdpProxyMessage.PingServersFromCoordinatorToClient;
        Assert.Single(_mesh.Seen(sender), IsPingRequest);
        Assert.Single(_mesh.Seen(recipient), IsPingRequest);
        Assert.DoesNotContain(_mesh.Seen(sender).Concat(_mesh.Seen(recipient)), e => e.Id == (byte)MessageId.UnconnectedPong);
        // The target's pings are stored with the source's, so only DEFAULT_UNRESPONSIVE_PING_TIME_COORDINATOR ends the wait.
        Assert.True(_mesh.Time.GetUtcNow() - start > TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void A_server_that_leaves_passes_its_requests_to_the_next_and_with_none_left_all_are_busy()
    {
        var (serverPeer, _) = LogInServer();
        var (_, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, _) = AddClient("10.0.1.2", "198.51.100.2");

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        // The server leaves before it answers; the request tries the next server, and there is none.
        serverPeer.Close(_coordinator.RakNetGuid, notify: true);
        _mesh.RunUntil(() => senderResults.Calls.Count > 0, "a result");

        Assert.StartsWith("OnAllServersBusy(", Assert.Single(senderResults.Calls));
        Assert.Empty(_coordinatorPlugin.Servers);
    }

    [Fact]
    public void A_request_whose_requester_leaves_is_forgotten()
    {
        // With two servers the request waits 3 s for pings, while the requester leaves.
        LogInServer();
        LogInServer();
        var (sender, senderPlugin, senderResults) = AddClient("10.0.0.2", "198.51.100.1");
        var (recipient, _, recipientResults) = AddClient("10.0.1.2", "198.51.100.2");

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, recipient.RakNetGuid, ForwardingTimeout);
        _mesh.Run(TimeSpan.FromMilliseconds(500));
        sender.Close(_coordinator.RakNetGuid, notify: true);
        _mesh.Run(TimeSpan.FromSeconds(4));

        Assert.Empty(senderResults.Calls);
        Assert.Empty(recipientResults.Calls);
    }

    [Fact]
    public void The_client_request_is_written_as_RakNet_writes_it()
    {
        var (_, senderPlugin, _) = AddClient("10.0.0.2", "198.51.100.1");
        var seen = new List<byte[]>();
        // Ahead of the coordinator, which consumes the request.
        _coordinator.Plugins.Detach(_coordinatorPlugin);
        _mesh.Step();
        _coordinator.Plugins.Attach(new Spy(seen));
        _coordinator.Plugins.Attach(_coordinatorPlugin);
        _mesh.Settle();

        senderPlugin.RequestForwarding(Coordinator, SystemAddress.Unassigned, new RakNetGuid(0x1122334455667788), TimeSpan.FromMilliseconds(0x01020304));
        _mesh.RunUntil(() => seen.Count > 0, "the request");

        // UDPProxyClient.cpp:76-90: the IDs, an unassigned source (version 4, ~0.0.0.0, port 0), false for a GUID, the
        // GUID, the timeout and false for no selection data, in 170 bits.
        Assert.Equal(new byte[]
        {
            (byte)MessageId.UdpProxyGeneral, (byte)UdpProxyMessage.ForwardingRequestFromClientToCoordinator,
            4, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0,
            0x08, 0x91, 0x19, 0xA2, 0x2A, 0xB3, 0x3B, 0xC4, 0x00, 0x81, 0x01, 0x82, 0x00,
        }, seen[0]);
    }

    [Fact]
    public void The_proxy_server_forwards_while_attached_to_a_running_peer()
    {
        var plugin = new UdpProxyServer(PeerThreading.Manual) { MaxForwardEntries = 3 };
        Assert.Null(plugin.Forwarder);
        var server = _mesh.Add("127.0.0.1", plugins: plugin);
        var forwarder = plugin.Forwarder!;
        Assert.True(forwarder.IsRunning);
        Assert.Equal(3, forwarder.MaxForwardEntries);

        server.Dispose();

        Assert.Null(plugin.Forwarder);
        Assert.False(forwarder.IsRunning);
    }

    private (RakPeer Peer, UdpProxyServer Plugin) LogInServer()
    {
        _coordinatorPlugin.RemoteLoginPassword = Password;
        var (server, plugin, results) = AddServer();
        plugin.LoginToCoordinator(Password, Coordinator);
        _mesh.RunUntil(() => results.Calls.Count > 0, "the login result");
        Assert.Equal(["OnLoginSuccess(a)"], results.Calls);
        return (server, plugin);
    }

    private (RakPeer Peer, UdpProxyServer Plugin, ServerResults Results) AddServer()
    {
        var results = new ServerResults();
        var plugin = new UdpProxyServer(PeerThreading.Manual) { ResultHandler = results };
        var server = _mesh.Add("127.0.0.1", plugins: plugin);
        _mesh.Connect(server, _coordinator);
        _mesh.Settle();
        return (server, plugin, results);
    }

    private (RakPeer Peer, UdpProxyClient Plugin, ClientResults Results) AddClient(string inside, string routerAddress)
    {
        var results = new ClientResults();
        var plugin = new UdpProxyClient { ResultHandler = results };
        var client = _mesh.Add(inside, _mesh.Nat(routerAddress, NatBehaviour.Symmetric), plugins: plugin);
        _mesh.Connect(client, _coordinator);
        _mesh.Settle();
        return (client, plugin, results);
    }

    // The client's address as the coordinator sees it: its router's mapping for the coordinator.
    private SystemAddress ExternalAddress(RakPeer client) => _coordinator.GetAddressFromGuid(client.RakNetGuid);

    internal sealed record Forwarding(string Callback, string ProxyIPAddress, ushort ProxyPort, SystemAddress ProxyCoordinator, SystemAddress SourceAddress,
                                      SystemAddress TargetAddress, RakNetGuid TargetGuid);

    internal sealed class ClientResults : IUdpProxyClientResultHandler
    {
        public List<string> Calls { get; } = [];

        public List<Forwarding> Successes { get; } = [];

        public List<Forwarding> Notifications { get; } = [];

        public List<Forwarding> InProgress { get; } = [];

        public void OnForwardingSuccess(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                        SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Record(Successes, new Forwarding("OnForwardingSuccess", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid));

        public void OnForwardingNotification(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                             SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Record(Notifications, new Forwarding("OnForwardingNotification", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid));

        public void OnForwardingInProgress(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                           SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Record(InProgress, new Forwarding("OnForwardingInProgress", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid));

        public void OnNoServersOnline(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                      UdpProxyClient proxyClient) =>
            Calls.Add($"OnNoServersOnline({proxyCoordinator}, {sourceAddress}, {targetAddress}, {targetGuid})");

        public void OnRecipientNotConnected(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                            UdpProxyClient proxyClient) =>
            Calls.Add($"OnRecipientNotConnected({proxyCoordinator}, {sourceAddress}, {targetAddress}, {targetGuid})");

        public void OnAllServersBusy(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                     UdpProxyClient proxyClient) =>
            Calls.Add($"OnAllServersBusy({proxyCoordinator}, {sourceAddress}, {targetAddress}, {targetGuid})");

        private void Record(List<Forwarding> list, Forwarding forwarding)
        {
            list.Add(forwarding);
            Calls.Add($"{forwarding.Callback}({forwarding.ProxyIPAddress}:{forwarding.ProxyPort})");
        }
    }

    internal sealed class ServerResults : IUdpProxyServerResultHandler
    {
        public List<string> Calls { get; } = [];

        public void OnLoginSuccess(string usedPassword, UdpProxyServer proxyServer) => Calls.Add($"OnLoginSuccess({usedPassword})");

        public void OnAlreadyLoggedIn(string usedPassword, UdpProxyServer proxyServer) => Calls.Add($"OnAlreadyLoggedIn({usedPassword})");

        public void OnNoPasswordSet(string usedPassword, UdpProxyServer proxyServer) => Calls.Add($"OnNoPasswordSet({usedPassword})");

        public void OnWrongPassword(string usedPassword, UdpProxyServer proxyServer) => Calls.Add($"OnWrongPassword({usedPassword})");
    }

    /// <summary>Records every UDPProxy message ahead of the coordinator.</summary>
    private sealed class Spy(List<byte[]> seen) : IRakPlugin
    {
        public PluginResult OnEvent(PeerEvent e)
        {
            if (e.Data.Length > 1 && e.Data[0] == (byte)MessageId.UdpProxyGeneral && e.Data[1] == (byte)UdpProxyMessage.ForwardingRequestFromClientToCoordinator)
                seen.Add(e.ToArray());
            return PluginResult.Continue;
        }
    }
}
