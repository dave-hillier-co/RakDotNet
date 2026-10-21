using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

public sealed class NatEmulatorTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private readonly NatEmulator _emulator = new();
    private readonly List<IDisposable> _disposables = [];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
        await _emulator.DisposeAsync();
    }

    [Fact]
    public async Task A_full_cone_mapping_passes_in_what_anyone_sends()
    {
        var router = _emulator.AddRouter(NatType.FullCone);
        var inside = await InsideAsync(router);
        var first = Outside();
        var second = Outside();

        Assert.True(inside.TrySend([1, 2, 3], (IPEndPoint)first.LocalEndPoint!));
        var (data, mapped) = await ReceiveAsync(first);
        Assert.Equal([1, 2, 3], data);
        Assert.Equal(router.FirstPort, mapped.Port);
        Assert.Equal([mapped], router.Mappings);

        second.SendTo([4], mapped);
        var (received, from) = await ReceiveAsync(inside);
        Assert.Equal([4], received);
        Assert.Equal(second.LocalEndPoint, from.ToIPEndPoint());
    }

    [Fact]
    public async Task An_address_restricted_mapping_passes_in_only_from_hosts_it_sent_to()
    {
        var router = _emulator.AddRouter(NatType.AddressRestricted);
        var inside = await InsideAsync(router);
        var server = Outside();
        var serverSecondPort = Outside();
        var stranger = Outside();
        _emulator.AddHost((IPEndPoint)server.LocalEndPoint!, (IPEndPoint)serverSecondPort.LocalEndPoint!);

        inside.TrySend([1], (IPEndPoint)server.LocalEndPoint!);
        var (_, mapped) = await ReceiveAsync(server);

        stranger.SendTo([2], mapped);
        serverSecondPort.SendTo([3], mapped);
        var (received, from) = await ReceiveAsync(inside);
        Assert.Equal([3], received);
        Assert.Equal(serverSecondPort.LocalEndPoint, from.ToIPEndPoint());
        await AssertNothingAsync(inside);
        Assert.Contains(router.Records, r => !r.Outbound && r.Remote.Equals(stranger.LocalEndPoint) && r.Outcome == "filtered");
    }

    [Fact]
    public async Task A_port_restricted_mapping_passes_in_only_from_addresses_it_sent_to()
    {
        var router = _emulator.AddRouter(NatType.PortRestricted);
        var inside = await InsideAsync(router);
        var server = Outside();
        var serverSecondPort = Outside();
        _emulator.AddHost((IPEndPoint)server.LocalEndPoint!, (IPEndPoint)serverSecondPort.LocalEndPoint!);

        inside.TrySend([1], (IPEndPoint)server.LocalEndPoint!);
        var (_, mapped) = await ReceiveAsync(server);
        serverSecondPort.SendTo([2], mapped);
        server.SendTo([3], mapped);

        Assert.Equal([3], (await ReceiveAsync(inside)).Data);
        await AssertNothingAsync(inside);
    }

    [Fact]
    public async Task A_symmetric_router_maps_each_destination_past_the_ports_punchthrough_predicts()
    {
        var router = _emulator.AddRouter(NatType.Symmetric);
        var inside = await InsideAsync(router);
        var first = Outside();
        var second = Outside();

        inside.TrySend([1], (IPEndPoint)first.LocalEndPoint!);
        inside.TrySend([2], (IPEndPoint)second.LocalEndPoint!);
        inside.TrySend([3], (IPEndPoint)first.LocalEndPoint!);
        var (_, toFirst) = await ReceiveAsync(first);
        var (_, toSecond) = await ReceiveAsync(second);
        var (_, againToFirst) = await ReceiveAsync(first);

        Assert.Equal(router.FirstPort, toFirst.Port);
        // NatPunchthroughClient tries the reported port and the next MAX_PREDICTIVE_PORT_RANGE (2) ports.
        Assert.Equal(router.FirstPort + 3, toSecond.Port);
        Assert.Equal(toFirst, againToFirst);
        // Each mapping filters as port restricted.
        second.SendTo([4], toFirst);
        first.SendTo([5], toFirst);
        Assert.Equal([5], (await ReceiveAsync(inside)).Data);
        await AssertNothingAsync(inside);
    }

    [Fact]
    public async Task A_datagram_with_a_ttl_of_two_opens_the_mapping_without_reaching_the_destination()
    {
        var router = _emulator.AddRouter(NatType.PortRestricted);
        var inside = await InsideAsync(router);
        var server = Outside();
        var peer = Outside();
        inside.TrySend([1], (IPEndPoint)server.LocalEndPoint!);
        var (_, mapped) = await ReceiveAsync(server);

        Assert.True(inside.TrySend([0, 1], (IPEndPoint)peer.LocalEndPoint!, ttl: 2));
        Assert.True(inside.TrySend([9], (IPEndPoint)peer.LocalEndPoint!, ttl: 1));
        await AssertNothingAsync(peer);

        peer.SendTo([7], mapped);
        Assert.Equal([7], (await ReceiveAsync(inside)).Data);
        Assert.Contains(router.Records, r => r.Outcome == "ttl opened mapping");
        Assert.Contains(router.Records, r => r.Outcome == "ttl expired");
    }

    [Fact]
    public async Task Nothing_reaches_an_inside_socket_except_through_its_router()
    {
        var router = _emulator.AddRouter(NatType.FullCone);
        var inside = await InsideAsync(router);
        var stranger = Outside();

        stranger.SendTo([1], inside.LocalAddress.ToIPEndPoint());

        await AssertNothingAsync(inside);
    }

    [Fact]
    public async Task A_socket_reaches_its_own_address_directly()
    {
        var router = _emulator.AddRouter(NatType.Symmetric);
        var inside = await InsideAsync(router);

        // As RakNet unblocks its receive thread.
        Assert.True(inside.TrySend([1, 2], inside.LocalAddress));

        var (data, from) = await ReceiveAsync(inside);
        Assert.Equal([1, 2], data);
        Assert.Equal(inside.LocalAddress, from);
        Assert.Empty(router.Mappings);
    }

    [Fact]
    public async Task Two_hosts_behind_one_router_reach_each_other_on_the_lan()
    {
        var router = _emulator.AddRouter(NatType.Symmetric);
        var first = await InsideAsync(router);
        var second = await InsideAsync(router);
        var server = Outside();
        // The router learns of a host when it first sends.
        second.TrySend([0], (IPEndPoint)server.LocalEndPoint!);
        await ReceiveAsync(server);

        first.TrySend([1], second.LocalAddress);

        var (data, from) = await ReceiveAsync(second);
        Assert.Equal([1], data);
        Assert.Equal(first.LocalAddress, from);
    }

    [Fact]
    public void Routers_take_separate_blocks_of_ports()
    {
        var first = _emulator.AddRouter(NatType.FullCone);
        var second = _emulator.AddRouter(NatType.FullCone);

        Assert.True(first.FirstPort + _emulator.PortsPerRouter <= second.FirstPort || second.FirstPort + _emulator.PortsPerRouter <= first.FirstPort);
        Assert.NotEqual(first.GatewayEndPoint, second.GatewayEndPoint);
    }

    private async Task<IDatagramTransport> InsideAsync(NatRouter router)
    {
        var factory = new NatGatewayTransportFactory(router.GatewayEndPoint);
        Assert.True(factory.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0)), out var transport, out _, out _));
        _disposables.Add(transport);
        // Binding sends four zero bytes to the socket itself, as RakNet's test send does (RakNetSocket2.cpp:317-326).
        var (data, from) = await ReceiveAsync(transport);
        Assert.Equal(new byte[4], data);
        Assert.Equal(transport.LocalAddress, from);
        return transport;
    }

    private Socket Outside()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _disposables.Add(socket);
        return socket;
    }

    private static async Task<(byte[] Data, IPEndPoint From)> ReceiveAsync(Socket socket)
    {
        var buffer = new byte[2048];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Wait);
        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeout.Token);
        return (buffer[..received.ReceivedBytes], (IPEndPoint)received.RemoteEndPoint);
    }

    private static async Task<(byte[] Data, SystemAddress From)> ReceiveAsync(IDatagramTransport transport)
    {
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (transport.TryReceive(buffer, out var length, out var from))
                return (buffer[..length], from);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Nothing arrived.");
    }

    private static async Task AssertNothingAsync(IDatagramTransport transport)
    {
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.False(transport.TryReceive(new byte[2048], out _, out var from), $"A datagram arrived from {from}.");
    }

    private static async Task AssertNothingAsync(Socket socket)
    {
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.Equal(0, socket.Available);
    }
}
