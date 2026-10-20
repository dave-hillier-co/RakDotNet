using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TcpInterface;

/// <summary>TCPInterface's plugin calls (TCPInterface.cpp:242-244, 251-253, 505-535, 551-575, 585, 654-734).</summary>
public sealed class TcpInterfacePluginTests : IDisposable
{
    private readonly Plugins.TcpInterface _server = new();
    private readonly Plugins.TcpInterface _client = new();
    private readonly RecordingPlugin _serverPlugin = new();
    private readonly RecordingPlugin _clientPlugin = new();
#pragma warning disable CA1859 // The test is of the IPluginHost implementation.
    private readonly IPluginHost _clientHost;
#pragma warning restore CA1859

    public TcpInterfacePluginTests()
    {
        _server.AttachPlugin(_serverPlugin);
        _client.AttachPlugin(_clientPlugin);
        _server.Start(0, 4, bindAddress: "127.0.0.1");
        _client.Start(0, 0);
        _clientHost = _client;
    }

    private ushort ServerPort => (ushort)_server.ListenEndPoint!.Port;

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void Attaching_and_starting_are_reported_and_attaching_again_does_nothing()
    {
        _server.AttachPlugin(_serverPlugin);

        Assert.Equal(["attached", "startup"], _serverPlugin.Calls);
        Assert.Same(_server, _serverPlugin.Host);
    }

    [Fact]
    public void Connection_events_reach_plugins_when_they_are_polled()
    {
        _client.Connect("127.0.0.1", ServerPort, block: true);
        var server = new SystemAddress(IPAddress.Loopback, ServerPort);
        Assert.DoesNotContain(_clientPlugin.Calls, c => c.StartsWith("connected", StringComparison.Ordinal));

        Assert.Equal(server, _client.HasCompletedConnectionAttempt());
        var client = Poll.Address(_server.HasNewIncomingConnection, "the new connection");

        Assert.Contains($"connected {server}", _clientPlugin.Calls);
        Assert.Contains($"connected {client}", _serverPlugin.Calls);

        _client.CloseConnection(server);
        Poll.Address(_server.HasLostConnection, "the lost connection");
        Assert.Contains($"disconnected {server} ClosedByUser", _clientPlugin.Calls);
        Assert.Contains($"disconnected {client} DisconnectionNotification", _serverPlugin.Calls);
    }

    [Fact]
    public void A_failed_attempt_reaches_plugins()
    {
        var port = TcpInterfaceTests.ClosedPort();
        _client.Connect("127.0.0.1", port, block: false);

        var failed = Poll.Address(_client.HasFailedConnectionAttempt, "the failed attempt");

        Assert.Contains($"failed {failed}", _clientPlugin.Calls);
    }

    [Fact]
    public void Each_receive_updates_the_plugins_and_a_consumed_packet_is_not_returned()
    {
        var server = _client.Connect("127.0.0.1", ServerPort, block: true);
        var client = Poll.Address(_server.HasNewIncomingConnection, "the new connection");
        _serverPlugin.ConsumeFirstByte = 0xFF;

        Assert.Null(_server.Receive());
        Assert.Equal(1, _serverPlugin.Calls.Count(c => c == "update"));

        _server.PushBackPacket(new TcpPacket(client, new byte[] { 0xFF }), pushAtHead: false);
        _server.PushBackPacket(new TcpPacket(client, new byte[] { 1 }), pushAtHead: false);

        // RakNet returns null for a consumed packet rather than looking for the next (TCPInterface.cpp:519-524).
        Assert.Null(_server.Receive());
        Assert.Equal([1], _server.Receive()!.Data.ToArray());
        Assert.Equal(["receive ff", "receive 01"], _serverPlugin.Calls.Where(c => c.StartsWith("receive", StringComparison.Ordinal)));
        _ = server;
    }

    [Fact]
    public void Stop_reports_shutdown_even_when_not_started_and_detach_is_reported()
    {
        _server.Stop();
        _server.Stop();
        _server.DetachPlugin(_serverPlugin);

        Assert.Equal(["attached", "startup", "shutdown", "shutdown", "detached"], _serverPlugin.Calls);
    }

    [Fact]
    public void The_interface_is_a_plugin_host_that_sends_to_the_target_address()
    {
        var server = _client.Connect("127.0.0.1", ServerPort, block: true);
        var client = Poll.Address(_server.HasNewIncomingConnection, "the new connection");

        // PluginInterface2::SendUnified sends the bytes to the target's address; a GUID alone has none.
        Assert.Null(_clientHost.Peer);
        Assert.Equal(RakNetGuid.Unassigned, _clientHost.RakNetGuid);
        Assert.Equal(SendStatus.Queued, _clientHost.Send(new byte[] { 1, 2 }, server, Reliability.ReliableOrdered).Status);
        Assert.Equal(SendStatus.NotConnected, _clientHost.Send(new byte[] { 1, 2 }, new RakNetGuid(5), Reliability.ReliableOrdered).Status);

        Assert.Equal([1, 2], Poll.Bytes(_server, client, 2));
    }

    private sealed class RecordingPlugin : ITcpPlugin
    {
        public List<string> Calls { get; } = [];

        public Plugins.TcpInterface? Host { get; private set; }

        public byte? ConsumeFirstByte { get; set; }

        public void OnAttached(Plugins.TcpInterface tcp)
        {
            Host = tcp;
            Calls.Add("attached");
        }

        public void OnDetached() => Calls.Add("detached");

        public void OnStartup() => Calls.Add("startup");

        public void OnShutdown() => Calls.Add("shutdown");

        public void OnUpdate() => Calls.Add("update");

        public PluginResult OnReceive(TcpPacket packet)
        {
            Calls.Add($"receive {Convert.ToHexStringLower(packet.Data.Span)}");
            return packet.Data.Span[0] == ConsumeFirstByte ? PluginResult.Consume : PluginResult.Continue;
        }

        public void OnConnected(SystemAddress address) => Calls.Add($"connected {address}");

        public void OnDisconnected(SystemAddress address, TcpDisconnectReason reason) => Calls.Add($"disconnected {address} {reason}");

        public void OnConnectFailed(SystemAddress address) => Calls.Add($"failed {address}");
    }
}
