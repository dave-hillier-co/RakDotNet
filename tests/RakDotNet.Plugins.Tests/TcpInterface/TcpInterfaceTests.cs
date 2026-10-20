using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TcpInterface;

/// <summary>
/// TCPInterface over loopback, as RakNet's TCP samples use it: a server started with incoming connections, clients
/// started without, and the polling calls for connection events and data.
/// </summary>
public sealed class TcpInterfaceTests : IDisposable
{
    private readonly Plugins.TcpInterface _server = new();
    private readonly Plugins.TcpInterface _client = new();
    private readonly Plugins.TcpInterface _secondClient = new();

    public TcpInterfaceTests()
    {
        Assert.True(_server.Start(0, maxIncomingConnections: 4, bindAddress: "127.0.0.1"));
        Assert.True(_client.Start(0, 0));
        Assert.True(_secondClient.Start(0, 0));
    }

    private SystemAddress ServerAddress => new(IPAddress.Loopback, _server.ListenEndPoint!.Port);

    public void Dispose()
    {
        _client.Dispose();
        _secondClient.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void A_connection_attempt_completes_on_the_client_and_arrives_as_a_new_connection_on_the_server()
    {
        // A non-blocking Connect returns UNASSIGNED_SYSTEM_ADDRESS at once (TCPInterface.cpp:391-417).
        Assert.Equal(SystemAddress.Unassigned, _client.Connect("127.0.0.1", ServerAddress.Port, block: false));

        Assert.Equal(ServerAddress, Poll.Address(_client.HasCompletedConnectionAttempt, "the completed attempt"));
        var incoming = Poll.Address(_server.HasNewIncomingConnection, "the new connection");
        Assert.True(incoming.IsLoopback);
        Assert.Equal([incoming], _server.GetConnectionList());
        Assert.Equal(1, _server.ConnectionCount);
        Assert.Equal([ServerAddress], _client.GetConnectionList());
    }

    [Fact]
    public void A_blocking_connect_returns_the_address_and_still_reports_the_completed_attempt()
    {
        Assert.Equal(ServerAddress, _client.Connect("127.0.0.1", ServerAddress.Port, block: true));

        Assert.Equal(ServerAddress, _client.HasCompletedConnectionAttempt());
    }

    [Fact]
    public void Bytes_sent_either_way_arrive_in_order()
    {
        var (clientAtServer, serverAtClient) = Connect(_client);

        _client.Send([1, 2, 3], serverAtClient, broadcast: false);
        _client.Send([4, 5], serverAtClient, broadcast: false);
        Assert.Equal([1, 2, 3, 4, 5], Poll.Bytes(_server, clientAtServer, 5));

        _server.Send([9, 8, 7], clientAtServer, broadcast: false);
        Assert.Equal([9, 8, 7], Poll.Bytes(_client, serverAtClient, 3));
    }

    [Fact]
    public void A_send_list_sends_its_parts_joined()
    {
        var (clientAtServer, serverAtClient) = Connect(_client);

        Assert.True(_client.SendList([new byte[] { 1, 2 }, Array.Empty<byte>(), new byte[] { 3 }], serverAtClient, broadcast: false));

        Assert.Equal([1, 2, 3], Poll.Bytes(_server, clientAtServer, 3));
    }

    [Fact]
    public void A_send_list_with_no_bytes_or_no_target_is_refused()
    {
        var (_, serverAtClient) = Connect(_client);

        // TCPInterface.cpp:451-465.
        Assert.False(_client.SendList([Array.Empty<byte>()], serverAtClient, broadcast: false));
        Assert.False(_client.SendList([new byte[] { 1 }], SystemAddress.Unassigned, broadcast: false));
        Assert.True(_client.SendList([new byte[] { 1 }], SystemAddress.Unassigned, broadcast: true));
        using var stopped = new Plugins.TcpInterface();
        Assert.False(stopped.SendList([new byte[] { 1 }], serverAtClient, broadcast: false));
    }

    [Fact]
    public void A_large_send_is_buffered_and_arrives_whole()
    {
        var (clientAtServer, serverAtClient) = Connect(_client);
        var data = new byte[3_000_000];
        new Random(7).NextBytes(data);

        _client.Send(data, serverAtClient, broadcast: false);

        Assert.Equal(data, Poll.Bytes(_server, clientAtServer, data.Length));
        Poll.True(() => _client.GetOutgoingDataBufferSize(serverAtClient) == 0, "the outgoing buffer to drain");
    }

    [Fact]
    public void A_broadcast_reaches_every_connection_but_the_one_excluded()
    {
        var (firstAtServer, firstServer) = Connect(_client);
        var (secondAtServer, secondServer) = Connect(_secondClient);

        _server.Send([42], firstAtServer, broadcast: true);

        Assert.Equal([42], Poll.Bytes(_secondClient, secondServer, 1));
        Poll.Never(() => _client.ReceiveHasPackets(), "data at the excluded client");
        Assert.NotEqual(firstAtServer, secondAtServer);
        _ = firstServer;
    }

    [Fact]
    public void Closing_a_connection_is_a_lost_connection_on_the_other_side_only()
    {
        var (clientAtServer, serverAtClient) = Connect(_client);

        _client.CloseConnection(serverAtClient);

        Assert.Equal(clientAtServer, Poll.Address(_server.HasLostConnection, "the lost connection"));
        Assert.Empty(_server.GetConnectionList());
        Assert.Empty(_client.GetConnectionList());
        Poll.Never(() => _client.HasLostConnection() != SystemAddress.Unassigned, "a lost connection at the closing side");
    }

    [Fact]
    public void A_connection_attempt_to_a_closed_port_fails()
    {
        var closedPort = ClosedPort();

        _client.Connect("127.0.0.1", closedPort, block: false);

        Assert.Equal(new SystemAddress(IPAddress.Loopback, closedPort), Poll.Address(_client.HasFailedConnectionAttempt, "the failed attempt"));
        Assert.Equal(0, _client.ConnectionCount);
    }

    [Fact]
    public void A_blocking_connection_attempt_to_a_closed_port_fails_and_returns_unassigned()
    {
        var closedPort = ClosedPort();

        Assert.Equal(SystemAddress.Unassigned, _client.Connect("127.0.0.1", closedPort, block: true));
        Assert.Equal(new SystemAddress(IPAddress.Loopback, closedPort), _client.HasFailedConnectionAttempt());
    }

    [Fact]
    public void A_connection_attempt_to_an_empty_host_name_fails()
    {
        // SocketConnect's gethostbyname("") finds nothing (TCPInterface.cpp:798-801); .NET's resolver would give
        // this machine's own addresses.
        Assert.Equal(SystemAddress.Unassigned, _client.Connect("", ServerAddress.Port, block: true));
        Assert.Equal(0, _client.ConnectionCount);
        Poll.Never(() => _server.HasNewIncomingConnection() != SystemAddress.Unassigned, "a connection");
    }

    [Fact]
    public void A_connection_beyond_the_maximum_is_accepted_but_never_serviced()
    {
        // TCPInterface.cpp:1066-1109: with every slot taken the accepted socket is kept but never read, and no event is
        // queued for it; the check meant to close the listener compares with -1 and never fires.
        using var full = new Plugins.TcpInterface();
        full.Start(0, maxIncomingConnections: 1, bindAddress: "127.0.0.1");
        var port = (ushort)full.ListenEndPoint!.Port;
        _client.Connect("127.0.0.1", port, block: true);
        var first = Poll.Address(full.HasNewIncomingConnection, "the first connection");

        var extra = _secondClient.Connect("127.0.0.1", port, block: true);
        _secondClient.Send([1], extra, broadcast: false);

        Poll.Never(() => full.HasNewIncomingConnection() != SystemAddress.Unassigned || full.ReceiveHasPackets(), "the second connection");
        Assert.Equal([first], full.GetConnectionList());
    }

    [Fact]
    public void Connect_before_start_returns_unassigned()
    {
        using var stopped = new Plugins.TcpInterface();

        Assert.Equal(SystemAddress.Unassigned, stopped.Connect("127.0.0.1", ServerAddress.Port, block: true));
    }

    [Fact]
    public void Start_twice_fails_and_stop_then_start_works_again()
    {
        Assert.False(_server.Start(0, 4));
        var (clientAtServer, _) = Connect(_client);
        _client.Send([1], ServerAddress, broadcast: false);
        Poll.True(_server.ReceiveHasPackets, "data");

        _server.Stop();

        Assert.False(_server.WasStarted);
        Assert.Null(_server.Receive());
        Assert.Equal(SystemAddress.Unassigned, _server.HasLostConnection());
        Assert.Empty(_server.GetConnectionList());
        Assert.True(_server.Start(0, 4, bindAddress: "127.0.0.1"));
        Assert.True(_server.WasStarted);
        _ = clientAtServer;
    }

    [Fact]
    public void Packets_pushed_back_at_the_head_come_before_received_ones_and_those_at_the_tail_after()
    {
        var (clientAtServer, serverAtClient) = Connect(_client);
        _client.Send([1], serverAtClient, broadcast: false);
        Poll.True(_server.ReceiveHasPackets, "data");

        _server.PushBackPacket(new TcpPacket(clientAtServer, new byte[] { 3 }), pushAtHead: false);
        _server.PushBackPacket(new TcpPacket(clientAtServer, new byte[] { 0 }), pushAtHead: true);

        Assert.Equal([0], _server.Receive()!.Data.ToArray());
        Assert.Equal([1], _server.Receive()!.Data.ToArray());
        Assert.Equal([3], _server.Receive()!.Data.ToArray());
        Assert.Null(_server.Receive());
    }

    private (SystemAddress ClientAtServer, SystemAddress ServerAtClient) Connect(Plugins.TcpInterface client)
    {
        var serverAtClient = client.Connect("127.0.0.1", ServerAddress.Port, block: true);
        Assert.Equal(ServerAddress, serverAtClient);
        Assert.Equal(ServerAddress, client.HasCompletedConnectionAttempt());
        var clientAtServer = Poll.Address(_server.HasNewIncomingConnection, "the new connection");
        return (clientAtServer, serverAtClient);
    }

    internal static ushort ClosedPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
