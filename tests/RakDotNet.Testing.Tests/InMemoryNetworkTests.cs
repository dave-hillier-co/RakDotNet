using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Testing.Tests;

public class InMemoryNetworkTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Two_peers_connect_exchange_messages_and_disconnect_with_no_sockets()
    {
        using var mesh = new ManualMesh(new InMemoryNetwork(_time), _time);
        var server = mesh.Add(maxIncoming: 4);
        var client = mesh.Add(maxIncoming: 0);

        var toServer = mesh.Connect(client, server);
        toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1, 2, 3 }, Reliability.ReliableOrdered);
        mesh.RunUntil(() => mesh.Seen(server).Any(e => e.Kind == PeerEventKind.Message), "message");
        toServer.Close();
        mesh.RunUntil(() => mesh.Seen(server).Any(e => e.Kind == PeerEventKind.Disconnected), "disconnect");

        Assert.Equal(
            [PeerEventKind.Connected, PeerEventKind.Message, PeerEventKind.Disconnected],
            mesh.Seen(server).Select(e => e.Kind));
        Assert.Equal([(byte)MessageId.UserPacketEnum, 1, 2, 3], mesh.Seen(server).Single(e => e.Kind == PeerEventKind.Message).Data);
    }

    [Fact]
    public void A_peer_sees_the_other_at_the_address_it_bound()
    {
        using var mesh = new ManualMesh(new InMemoryNetwork(_time), _time);
        var server = mesh.Add(maxIncoming: 4);
        var client = mesh.Add(maxIncoming: 0);

        var toServer = mesh.Connect(client, server);

        Assert.Equal(server.Sockets[0].LocalAddress, toServer.Address);
        Assert.Equal(client.Sockets[0].LocalAddress, mesh.Seen(server).Single(e => e.Kind == PeerEventKind.Connected).Address);
    }

    [Fact]
    public void Port_zero_gets_distinct_ports_and_a_taken_port_fails()
    {
        var network = new InMemoryNetwork(_time);
        Assert.True(network.TryBind(new SocketBinding(new IPEndPoint(Loopback, 0)), out var first, out _, out _));
        Assert.True(network.TryBind(new SocketBinding(new IPEndPoint(Loopback, 0)), out var second, out _, out _));
        Assert.NotEqual(0, first.LocalAddress.Port);
        Assert.NotEqual(first.LocalAddress.Port, second.LocalAddress.Port);

        Assert.False(network.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Any, first.LocalAddress.Port)), out var taken, out var error, out var socketError));
        Assert.Null(taken);
        Assert.Equal(StartupError.SocketPortAlreadyInUse, error);
        Assert.Equal(SocketError.AddressAlreadyInUse, socketError);
    }

    [Fact]
    public void A_peer_whose_port_is_taken_fails_to_start()
    {
        var network = new InMemoryNetwork(_time);
        using var first = RakPeer.Start(Options(network, new IPEndPoint(Loopback, 5000)));

        var thrown = Assert.Throws<RakStartupException>(() => RakPeer.Start(Options(network, new IPEndPoint(Loopback, 5000))));
        Assert.Equal(StartupError.SocketPortAlreadyInUse, thrown.Error);
    }

    [Fact]
    public void Disposing_a_transport_frees_its_port()
    {
        var network = new InMemoryNetwork(_time);
        var peer = RakPeer.Start(Options(network, new IPEndPoint(Loopback, 5000)));
        peer.Dispose();

        using var again = RakPeer.Start(Options(network, new IPEndPoint(Loopback, 5000)));
        Assert.Equal(5000, again.Sockets[0].LocalAddress.Port);
    }

    [Fact]
    public void IPv6_is_not_supported()
    {
        var network = new InMemoryNetwork(_time);

        Assert.False(network.TryBind(new SocketBinding(new IPEndPoint(IPAddress.IPv6Loopback, 0)), out _, out var error, out _));
        Assert.Equal(StartupError.SocketFamilyNotSupported, error);
    }

    [Fact]
    public void A_datagram_to_an_unbound_address_is_dropped_and_counted()
    {
        var network = new InMemoryNetwork(_time);
        var a = Bind(network);

        Assert.True(a.TrySend([1], new SystemAddress(Loopback, 9)));

        Assert.Equal(1, network.DatagramsSent);
        Assert.Equal(1, network.DatagramsDropped);
    }

    [Fact]
    public void A_binding_on_every_interface_receives_datagrams_sent_to_loopback()
    {
        var network = new InMemoryNetwork(_time);
        Assert.True(network.TryBind(SocketBinding.AnyIPv4, out var any, out _, out _));
        var a = Bind(network);

        a.TrySend([7], new SystemAddress(Loopback, any.LocalAddress.Port));

        Assert.Equal([new Received([7], a.LocalAddress)], Drain(any));
    }

    [Fact]
    public void A_datagram_from_a_binding_on_every_interface_comes_from_loopback()
    {
        var network = new InMemoryNetwork(_time);
        Assert.True(network.TryBind(SocketBinding.AnyIPv4, out var any, out _, out _));
        var b = Bind(network);

        any.TrySend([7], b.LocalAddress);

        Assert.Equal([new Received([7], new SystemAddress(Loopback, any.LocalAddress.Port))], Drain(b));
    }

    [Fact]
    public void The_ready_signal_fires_when_a_datagram_arrives()
    {
        var network = new InMemoryNetwork(_time);
        var a = Bind(network);
        var b = Bind(network);
        var signals = 0;
        b.SetReadySignal(() => signals++);

        a.TrySend([1], b.LocalAddress);

        Assert.Equal(1, signals);
    }

    [Fact]
    public void A_receive_buffer_too_small_for_the_datagram_truncates_it_as_UDP_does()
    {
        var network = new InMemoryNetwork(_time);
        var a = Bind(network);
        var b = Bind(network);
        a.TrySend([1, 2, 3, 4], b.LocalAddress);

        var buffer = new byte[2];
        Assert.True(b.TryReceive(buffer, out var length, out _));
        Assert.Equal(2, length);
        Assert.Equal([1, 2], buffer);
        Assert.False(b.TryReceive(buffer, out _, out _));
    }

    [Fact]
    public void Delay_holds_datagrams_until_the_time_provider_reaches_them()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { MinDelay = TimeSpan.FromMilliseconds(50) });
        var a = Bind(network);
        var b = Bind(network);

        a.TrySend([1], b.LocalAddress);
        _time.Advance(TimeSpan.FromMilliseconds(49));
        Assert.Empty(Drain(b));
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([1], Drain(b).Single().Data);
    }

    [Fact]
    public void Delayed_datagrams_arrive_in_the_order_their_delays_end()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { MinDelay = TimeSpan.FromMilliseconds(10) });
        var a = Bind(network);
        var b = Bind(network);

        a.TrySend([1], b.LocalAddress);
        _time.Advance(TimeSpan.FromMilliseconds(5));
        a.TrySend([2], b.LocalAddress);
        a.TrySend([3], b.LocalAddress);
        _time.Advance(TimeSpan.FromMilliseconds(20));

        Assert.Equal([1, 2, 3], Drain(b).Select(r => r.Data[0]));
    }

    [Fact]
    public void Total_loss_delivers_nothing()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { PacketLoss = 1 });
        var a = Bind(network);
        var b = Bind(network);

        for (var i = 0; i < 10; i++)
            a.TrySend([(byte)i], b.LocalAddress);

        Assert.Empty(Drain(b));
        Assert.Equal(10, network.DatagramsSent);
        Assert.Equal(10, network.DatagramsDropped);
    }

    [Fact]
    public void Loss_is_reproducible_from_the_seed()
    {
        static byte[] Run(int seed)
        {
            var time = new FakeTimeProvider();
            var network = new InMemoryNetwork(time, new InMemoryNetworkOptions { Seed = seed, PacketLoss = 0.5 });
            var a = Bind(network);
            var b = Bind(network);
            for (var i = 0; i < 64; i++)
                a.TrySend([(byte)i], b.LocalAddress);
            return [.. Drain(b).Select(r => r.Data[0])];
        }

        var first = Run(seed: 7);
        Assert.Equal(first, Run(seed: 7));
        Assert.NotEqual(first, Run(seed: 8));
        Assert.InRange(first.Length, 1, 63);
    }

    [Fact]
    public void Duplication_delivers_a_datagram_twice()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { Duplication = 1 });
        var a = Bind(network);
        var b = Bind(network);

        a.TrySend([1], b.LocalAddress);
        a.TrySend([2], b.LocalAddress);

        Assert.Equal([1, 1, 2, 2], Drain(b).Select(r => r.Data[0]));
    }

    [Fact]
    public void Reordering_holds_a_datagram_back_behind_the_next_one()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { Reordering = 1 });
        var a = Bind(network);
        var b = Bind(network);

        for (byte i = 1; i <= 4; i++)
            a.TrySend([i], b.LocalAddress);

        Assert.Equal([2, 1, 4, 3], Drain(b).Select(r => r.Data[0]));
    }

    [Fact]
    public void Peers_connect_through_a_slow_network_driven_by_the_fake_clock()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { MinDelay = TimeSpan.FromMilliseconds(30), DelayVariance = TimeSpan.FromMilliseconds(20), Seed = 3 });
        using var mesh = new ManualMesh(network, _time);
        var server = mesh.Add(maxIncoming: 4);
        var client = mesh.Add(maxIncoming: 0);

        var startedAt = _time.GetUtcNow();
        var toServer = mesh.Connect(client, server);

        Assert.True(toServer.IsConnected);
        // Two handshake round trips at least 60 ms each.
        Assert.True(_time.GetUtcNow() - startedAt >= TimeSpan.FromMilliseconds(120));
    }

    [Fact]
    public void Reliable_ordered_messages_survive_duplication_and_reordering()
    {
        var network = new InMemoryNetwork(_time, new InMemoryNetworkOptions { Duplication = 0.2, Reordering = 0.2, Seed = 11 });
        using var mesh = new ManualMesh(network, _time);
        var server = mesh.Add(maxIncoming: 4);
        var client = mesh.Add(maxIncoming: 0);
        var toServer = mesh.Connect(client, server);

        for (byte i = 0; i < 50; i++)
            toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, i }, Reliability.ReliableOrdered);
        mesh.RunUntil(() => mesh.Seen(server).Count(e => e.Kind == PeerEventKind.Message) >= 50, "50 messages");
        mesh.Settle();

        Assert.Equal(
            Enumerable.Range(0, 50).Select(i => (byte)i),
            mesh.Seen(server).Where(e => e.Kind == PeerEventKind.Message).Select(e => e.Data[1]));
    }

    [Fact]
    public async Task Background_peers_are_woken_by_the_network()
    {
        var network = new InMemoryNetwork(TimeProvider.System);
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 4, MaxIncomingConnections = 4, Transport = network, Bindings = [new SocketBinding(new IPEndPoint(Loopback, 0))] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1, Transport = network, Bindings = [new SocketBinding(new IPEndPoint(Loopback, 0))] });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var result = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: timeout.Token);

        Assert.True(result.IsConnected);
    }

    private RakPeerOptions Options(InMemoryNetwork network, IPEndPoint endPoint) => new()
    {
        MaxConnections = 4,
        Transport = network,
        Bindings = [new SocketBinding(endPoint)],
        Threading = PeerThreading.Manual,
        TimeProvider = _time,
    };

    private static IDatagramTransport Bind(InMemoryNetwork network)
    {
        Assert.True(network.TryBind(new SocketBinding(new IPEndPoint(Loopback, 0)), out var transport, out _, out _));
        return transport;
    }

    private static List<Received> Drain(IDatagramTransport transport)
    {
        var received = new List<Received>();
        var buffer = new byte[1500];
        while (transport.TryReceive(buffer, out var length, out var from))
            received.Add(new Received(buffer[..length], from));
        return received;
    }

    private sealed record Received(byte[] Data, SystemAddress From)
    {
        public bool Equals(Received? other) => other is not null && Data.AsSpan().SequenceEqual(other.Data) && From == other.From;

        public override int GetHashCode() => From.GetHashCode();
    }
}
