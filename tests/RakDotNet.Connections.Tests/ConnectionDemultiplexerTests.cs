using System.Net;
using System.Threading.Channels;
using RakDotNet.Protocol.Wire;
using RakDotNet.Testing;

namespace RakDotNet.Connections.Tests;

// Background peers on an InMemoryNetwork driven by the system clock: the demultiplexer is an async pump.
public sealed class ConnectionDemultiplexerTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly InMemoryNetwork _network = new(TimeProvider.System);
    private readonly CancellationTokenSource _timeout = new(Patience);
    private readonly List<RakPeer> _peers = [];

    private CancellationToken Token => _timeout.Token;

    [Fact]
    public async Task Each_connection_gets_its_messages_then_its_disconnect_then_completion()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server);
        var pump = demux.RunAsync(Token);
        var client = Start(maxIncoming: 0);
        var toServer = await Connect(client, server);

        var connection = await demux.Connections.ReadAsync(Token);
        for (byte i = 0; i < 5; i++)
            toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
        toServer.Close();

        var events = await ReadAll(demux.EventsOf(connection));
        Assert.Equal(
            [PeerEventKind.Message, PeerEventKind.Message, PeerEventKind.Message, PeerEventKind.Message, PeerEventKind.Message, PeerEventKind.Disconnected],
            events.Select(e => e.Kind));
        Assert.Equal([0, 1, 2, 3, 4], events.Take(5).Select(e => e.Data[1]));
        Assert.True(connection.IsIncoming);
        Assert.False(pump.IsCompleted);
    }

    [Fact]
    public async Task Order_holds_within_each_connection_while_connections_interleave()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server);
        _ = demux.RunAsync(Token);
        var first = await Connect(Start(maxIncoming: 0), server);
        var second = await Connect(Start(maxIncoming: 0), server);
        var connections = new Dictionary<RakNetGuid, RakConnection>();
        while (connections.Count < 2)
        {
            var c = await demux.Connections.ReadAsync(Token);
            connections[c.RakNetGuid] = c;
        }

        for (byte i = 0; i < 20; i++)
        {
            first.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
            second.Send(new byte[] { (byte)MessageId.UserPacketEnum + 1, i }, Reliability.ReliableOrdered);
        }
        first.Close();
        second.Close();

        var fromFirst = await ReadAll(demux.EventsOf(connections[first.Peer.RakNetGuid]));
        var fromSecond = await ReadAll(demux.EventsOf(connections[second.Peer.RakNetGuid]));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), fromFirst.Where(e => e.Kind == PeerEventKind.Message).Select(e => e.Data[1]));
        Assert.All(fromFirst.Where(e => e.Kind == PeerEventKind.Message), e => Assert.Equal((byte)MessageId.UserPacketEnum, e.Data[0]));
        Assert.Equal(Enumerable.Range(0, 20).Select(i => (byte)i), fromSecond.Where(e => e.Kind == PeerEventKind.Message).Select(e => e.Data[1]));
        Assert.All(fromSecond.Where(e => e.Kind == PeerEventKind.Message), e => Assert.Equal((byte)MessageId.UserPacketEnum + 1, e.Data[0]));
    }

    [Fact]
    public async Task Outgoing_connections_and_receipts_are_demultiplexed_too()
    {
        var server = Start(maxIncoming: 4);
        var client = Start(maxIncoming: 0);
        await using var demux = new ConnectionDemultiplexer(client);
        _ = demux.RunAsync(Token);

        Assert.Equal(ConnectAttemptResult.Started, client.Connect(server.Sockets[0].LocalAddress));
        var connection = await demux.Connections.ReadAsync(Token);
        var sent = connection.Send(new byte[] { (byte)MessageId.UserPacketEnum }, Reliability.ReliableWithAckReceipt);

        var reader = demux.EventsOf(connection);
        using var receipt = await reader.ReadAsync(Token);
        Assert.False(connection.IsIncoming);
        Assert.Equal(PeerEventKind.ReceiptAcked, receipt.Event.Kind);
        Assert.Equal(sent.Receipt, receipt.Event.Receipt);
    }

    [Fact]
    public async Task Events_without_a_connection_go_to_the_offline_queue()
    {
        var server = Start(maxIncoming: 4);
        var client = Start(maxIncoming: 0);
        await using var demux = new ConnectionDemultiplexer(client);
        _ = demux.RunAsync(Token);

        Assert.True(client.PingUnconnected(server.Sockets[0].LocalAddress));
        using (var pong = await demux.Offline.ReadAsync(Token))
            Assert.Equal(PeerEventKind.UnconnectedPong, pong.Event.Kind);

        var nobody = new SystemAddress(IPAddress.Loopback, 9);
        Assert.Equal(ConnectAttemptResult.Started, client.Connect(nobody, new ConnectOptions { AttemptCount = 1, AttemptInterval = TimeSpan.FromMilliseconds(10) }));
        using var failed = await demux.Offline.ReadAsync(Token);
        Assert.Equal(PeerEventKind.ConnectFailed, failed.Event.Kind);
        Assert.Equal(nobody, failed.Event.Address);
    }

    [Fact]
    public async Task The_wait_policy_delivers_everything_to_a_slow_reader()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server, new DemultiplexerOptions { PerConnectionCapacity = 1 });
        _ = demux.RunAsync(Token);
        var toServer = await Connect(Start(maxIncoming: 0), server);
        var connection = await demux.Connections.ReadAsync(Token);

        for (byte i = 0; i < 30; i++)
            toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
        toServer.Close();

        var events = await ReadAll(demux.EventsOf(connection));
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), events.Where(e => e.Kind == PeerEventKind.Message).Select(e => e.Data[1]));
        Assert.Equal(PeerEventKind.Disconnected, events[^1].Kind);
    }

    [Fact]
    public async Task The_close_policy_closes_a_connection_whose_queue_is_full()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server, new DemultiplexerOptions { PerConnectionCapacity = 2, Overflow = DemultiplexerOverflow.CloseConnection });
        _ = demux.RunAsync(Token);
        var toServer = await Connect(Start(maxIncoming: 0), server);
        var connection = await demux.Connections.ReadAsync(Token);

        for (byte i = 0; i < 10; i++)
            toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
        await connection.Closed.WaitAsync(Token);

        var events = await ReadAll(demux.EventsOf(connection));
        Assert.Equal([0, 1], events.Select(e => e.Data[1]));
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task A_full_connection_under_the_close_policy_does_not_stop_the_others()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server, new DemultiplexerOptions { PerConnectionCapacity = 2, Overflow = DemultiplexerOverflow.CloseConnection });
        _ = demux.RunAsync(Token);
        var noisy = await Connect(Start(maxIncoming: 0), server);
        var quiet = await Connect(Start(maxIncoming: 0), server);
        RakConnection? quietOnServer = null;
        for (var i = 0; i < 2; i++)
        {
            var c = await demux.Connections.ReadAsync(Token);
            if (c.RakNetGuid == quiet.Peer.RakNetGuid)
                quietOnServer = c;
        }

        for (byte i = 0; i < 10; i++)
            noisy.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
        quiet.Send(new byte[] { (byte)MessageId.UserPacketEnum, 42 }, Reliability.ReliableOrdered);

        using var message = await demux.EventsOf(quietOnServer!).ReadAsync(Token);
        Assert.Equal(42, message.Data.Span[1]);
    }

    [Fact]
    public async Task Shutting_the_peer_down_ends_the_pump_and_completes_every_reader()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server);
        var pump = demux.RunAsync(Token);
        await Connect(Start(maxIncoming: 0), server);
        var connection = await demux.Connections.ReadAsync(Token);

        await server.ShutdownAsync(TimeSpan.Zero, cancellationToken: Token);
        await pump.WaitAsync(Token);

        await demux.Connections.Completion.WaitAsync(Token);
        await demux.Offline.Completion.WaitAsync(Token);
        await demux.EventsOf(connection).Completion.WaitAsync(Token);
    }

    [Fact]
    public async Task Cancelling_the_pump_completes_every_reader()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server);
        using var cancel = new CancellationTokenSource();
        var pump = demux.RunAsync(cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pump.WaitAsync(Token));
        await demux.Connections.Completion.WaitAsync(Token);
        await demux.Offline.Completion.WaitAsync(Token);
    }

    [Fact]
    public async Task The_pump_runs_once()
    {
        var server = Start(maxIncoming: 4);
        await using var demux = new ConnectionDemultiplexer(server);
        _ = demux.RunAsync(Token);

        Assert.Throws<InvalidOperationException>(() => { _ = demux.RunAsync(Token); });
    }

    [Fact]
    public async Task Disposing_stops_the_pump_and_releases_what_was_not_read()
    {
        var server = Start(maxIncoming: 4);
        var demux = new ConnectionDemultiplexer(server);
        var pump = demux.RunAsync(Token);
        var toServer = await Connect(Start(maxIncoming: 0), server);
        var connection = await demux.Connections.ReadAsync(Token);
        toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum }, Reliability.ReliableOrdered);
        var reader = demux.EventsOf(connection);
        await reader.WaitToReadAsync(Token);

        await demux.DisposeAsync();

        Assert.True(pump.IsCompleted);
        Assert.False(reader.TryRead(out _));
        await reader.Completion.WaitAsync(Token);
        Assert.Throws<ObjectDisposedException>(() => { _ = demux.RunAsync(Token); });
    }

    [Fact]
    public async Task Plugins_run_on_the_pump()
    {
        var plugin = new CountingPlugin();
        var server = Start(maxIncoming: 4, plugin);
        await using var demux = new ConnectionDemultiplexer(server);
        _ = demux.RunAsync(Token);
        await Connect(Start(maxIncoming: 0), server);
        var connection = await demux.Connections.ReadAsync(Token);

        Assert.Equal(1, plugin.Connected);
        Assert.Same(connection, plugin.Connection);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var peer in _peers)
            await peer.ShutdownAsync(TimeSpan.Zero);
        _timeout.Dispose();
    }

    private RakPeer Start(int maxIncoming, params IRakPlugin[] plugins)
    {
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 4,
            MaxIncomingConnections = maxIncoming,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Transport = _network,
            Plugins = plugins,
        });
        _peers.Add(peer);
        return peer;
    }

    private async Task<RakConnection> Connect(RakPeer client, RakPeer server)
    {
        var result = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: Token);
        return result.EnsureConnected();
    }

    private async Task<List<Seen>> ReadAll(ChannelReader<OwnedPeerEvent> reader)
    {
        var seen = new List<Seen>();
        await foreach (var owned in reader.ReadAllAsync(Token))
        {
            using (owned)
                seen.Add(new Seen(owned.Event.Kind, owned.Data.ToArray()));
        }
        return seen;
    }

    private sealed record Seen(PeerEventKind Kind, byte[] Data);

    private sealed class CountingPlugin : IRakPlugin
    {
        public int Connected { get; private set; }

        public RakConnection? Connection { get; private set; }

        public void OnConnected(RakConnection connection, bool isIncoming)
        {
            Connected++;
            Connection = connection;
        }
    }
}
