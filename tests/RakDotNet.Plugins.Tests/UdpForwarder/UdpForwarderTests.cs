using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;
using RakDotNet.Testing;

namespace RakDotNet.Plugins.Tests.UdpForwarder;

public sealed class UdpForwarderTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryNetwork _network;
    private readonly Plugins.UdpForwarder _forwarder;
    private readonly List<IDatagramTransport> _sockets = [];

    public UdpForwarderTests()
    {
        _network = new InMemoryNetwork(_time);
        _forwarder = new Plugins.UdpForwarder(_network, _time, PeerThreading.Manual);
    }

    [Fact]
    public void Before_startup_forwarding_is_refused_as_not_running()
    {
        var result = _forwarder.StartForwarding(Address("10.0.0.1", 1000), Address("10.0.0.2", 2000), Timeout);

        Assert.Equal(new UdpForwarding(UdpForwarderResult.NotRunning, 0, null), result);
    }

    [Fact]
    public void A_missing_or_oversized_timeout_or_an_unassigned_address_is_invalid()
    {
        _forwarder.Startup();
        var a = Address("10.0.0.1", 1000);
        var b = Address("10.0.0.2", 2000);

        // UDPForwarder.cpp:120-121, checked before whether it runs.
        Assert.Equal(UdpForwarderResult.InvalidParameters, _forwarder.StartForwarding(a, b, TimeSpan.Zero).Result);
        Assert.Equal(UdpForwarderResult.InvalidParameters, _forwarder.StartForwarding(a, b, Plugins.UdpForwarder.MaximumTimeout + TimeSpan.FromMilliseconds(1)).Result);
        Assert.Equal(UdpForwarderResult.InvalidParameters, _forwarder.StartForwarding(SystemAddress.Unassigned, b, Timeout).Result);
        Assert.Equal(UdpForwarderResult.InvalidParameters, _forwarder.StartForwarding(a, SystemAddress.Unassigned, Timeout).Result);
        Assert.Equal(UdpForwarderResult.Success, _forwarder.StartForwarding(a, b, Plugins.UdpForwarder.MaximumTimeout).Result);
    }

    [Fact]
    public void A_pair_already_forwarded_either_way_reports_it_without_a_port()
    {
        _forwarder.Startup();
        var a = Address("10.0.0.1", 1000);
        var b = Address("10.0.0.2", 2000);

        var first = _forwarder.StartForwarding(a, b, Timeout);
        Assert.Equal(UdpForwarderResult.Success, first.Result);
        Assert.NotEqual(0, first.ForwardingPort);
        Assert.NotNull(first.ForwardingSocket);

        // Only success fills in the port and socket (UDPForwarder.cpp:153-160).
        Assert.Equal(new UdpForwarding(UdpForwarderResult.ForwardingAlreadyExists, 0, null), _forwarder.StartForwarding(a, b, Timeout));
        Assert.Equal(new UdpForwarding(UdpForwarderResult.ForwardingAlreadyExists, 0, null), _forwarder.StartForwarding(b, a, Timeout));
        Assert.Equal(1, _forwarder.UsedForwardEntries);
    }

    [Fact]
    public void Entries_are_refused_only_once_more_than_the_maximum_are_in_use()
    {
        _forwarder.Startup();
        _forwarder.MaxForwardEntries = 1;

        // GetUsedForwardEntries()>maxForwardEntries (UDPForwarder.cpp:405): the maximum plus one fit.
        Assert.Equal(UdpForwarderResult.Success, _forwarder.StartForwarding(Address("10.0.0.1", 1), Address("10.0.0.2", 1), Timeout).Result);
        Assert.Equal(UdpForwarderResult.Success, _forwarder.StartForwarding(Address("10.0.0.1", 2), Address("10.0.0.2", 2), Timeout).Result);
        Assert.Equal(UdpForwarderResult.NoSockets, _forwarder.StartForwarding(Address("10.0.0.1", 3), Address("10.0.0.2", 3), Timeout).Result);
        // Even for a pair that exists, the count is checked first.
        Assert.Equal(UdpForwarderResult.NoSockets, _forwarder.StartForwarding(Address("10.0.0.1", 1), Address("10.0.0.2", 1), Timeout).Result);
        Assert.Equal(2, _forwarder.UsedForwardEntries);
    }

    [Fact]
    public void The_maximum_is_kept_within_what_RakNet_asserts()
    {
        Assert.Equal(64, _forwarder.MaxForwardEntries);
        Assert.Throws<ArgumentOutOfRangeException>(() => _forwarder.MaxForwardEntries = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => _forwarder.MaxForwardEntries = 65535 / 2);
    }

    [Fact]
    public void A_forced_host_that_is_not_an_address_cannot_be_bound()
    {
        _forwarder.Startup();

        var result = _forwarder.StartForwarding(Address("10.0.0.1", 1000), Address("10.0.0.2", 2000), Timeout, "not an address");

        Assert.Equal(UdpForwarderResult.BindFailed, result.Result);
        Assert.Equal(0, _forwarder.UsedForwardEntries);
    }

    [Fact]
    public void The_socket_is_bound_on_the_forced_host()
    {
        _forwarder.Startup();

        var result = _forwarder.StartForwarding(Address("10.0.0.1", 1000), Address("10.0.0.2", 2000), Timeout, "10.0.0.9");

        Assert.Equal(Address("10.0.0.9", result.ForwardingPort), result.ForwardingSocket!.LocalAddress);
    }

    [Fact]
    public void Datagrams_cross_in_both_directions_and_the_first_from_each_side_fixes_its_port()
    {
        _forwarder.Startup();
        var source = Bind("10.0.0.1");
        var destination = Bind("10.0.0.2");
        // The source is known by address only: its router may give it another port.
        var forwarding = _forwarder.StartForwarding(source.LocalAddress.WithPort(1), destination.LocalAddress, Timeout);
        var forwarder = new SystemAddress(IPAddress.Loopback, forwarding.ForwardingPort);

        source.TrySend([1, 2, 3], forwarder);
        _forwarder.Update();
        AssertReceived(destination, [1, 2, 3], forwarder);

        destination.TrySend([4, 5], forwarder);
        _forwarder.Update();
        // Back to the port the source actually sent from.
        AssertReceived(source, [4, 5], forwarder);

        // Another port on the source's address is not the source any more.
        var stranger = Bind("10.0.0.1");
        stranger.TrySend([6], forwarder);
        _forwarder.Update();
        Assert.Null(TryReceive(destination));
        Assert.Null(TryReceive(stranger));
    }

    [Fact]
    public void The_side_heard_first_is_addr1_whichever_it_is()
    {
        _forwarder.Startup();
        var source = Bind("10.0.0.1");
        var destination = Bind("10.0.0.1");
        // Both on one address, as on loopback: the first datagram matches the source by address and is confirmed as it.
        var forwarding = _forwarder.StartForwarding(source.LocalAddress, destination.LocalAddress, Timeout);
        var forwarder = new SystemAddress(IPAddress.Loopback, forwarding.ForwardingPort);

        destination.TrySend([1], forwarder);
        _forwarder.Update();
        // Taken for the source, so sent to the destination's unconfirmed address: itself.
        AssertReceived(destination, [1], forwarder);

        source.TrySend([2], forwarder);
        _forwarder.Update();
        AssertReceived(destination, [2], forwarder);
        destination.TrySend([3], forwarder);
        _forwarder.Update();
        AssertReceived(source, [3], forwarder);
    }

    [Fact]
    public void An_entry_that_forwards_nothing_for_its_timeout_is_removed()
    {
        _forwarder.Startup();
        var source = Bind("10.0.0.1");
        var destination = Bind("10.0.0.2");
        var forwarding = _forwarder.StartForwarding(source.LocalAddress, destination.LocalAddress, Timeout);
        var forwarder = new SystemAddress(IPAddress.Loopback, forwarding.ForwardingPort);

        _time.Advance(Timeout - TimeSpan.FromMilliseconds(1));
        source.TrySend([1], forwarder);
        _forwarder.Update();
        Assert.NotNull(TryReceive(destination));

        // The forward restarted the timeout: the entry outlives its first deadline.
        _time.Advance(Timeout);
        _forwarder.Update();
        Assert.Equal(1, _forwarder.UsedForwardEntries);

        // curTime > last + timeout (UDPForwarder.cpp:563-566).
        _time.Advance(TimeSpan.FromMilliseconds(2));
        _forwarder.Update();
        Assert.Equal(0, _forwarder.UsedForwardEntries);
        source.TrySend([2], forwarder);
        _forwarder.Update();
        Assert.Null(TryReceive(destination));
    }

    [Fact]
    public void Stopping_removes_the_pair_at_the_next_update_in_either_direction()
    {
        _forwarder.Startup();
        var a = Address("10.0.0.1", 1000);
        var b = Address("10.0.0.2", 2000);
        _forwarder.StartForwarding(a, b, Timeout);

        _forwarder.StopForwarding(b, a);
        Assert.Equal(1, _forwarder.UsedForwardEntries);
        _forwarder.Update();

        Assert.Equal(0, _forwarder.UsedForwardEntries);
        Assert.Equal(UdpForwarderResult.Success, _forwarder.StartForwarding(a, b, Timeout).Result);
    }

    [Fact]
    public void Shutdown_closes_every_entry_and_stops_forwarding()
    {
        _forwarder.Startup();
        _forwarder.StartForwarding(Address("10.0.0.1", 1000), Address("10.0.0.2", 2000), Timeout);

        _forwarder.Shutdown();

        Assert.False(_forwarder.IsRunning);
        Assert.Equal(0, _forwarder.UsedForwardEntries);
        Assert.Equal(UdpForwarderResult.NotRunning, _forwarder.StartForwarding(Address("10.0.0.1", 1000), Address("10.0.0.2", 2000), Timeout).Result);
    }

    [Fact]
    public void Update_belongs_to_the_thread_in_background_mode()
    {
        using var background = new Plugins.UdpForwarder(_network, _time);

        Assert.Throws<InvalidOperationException>(background.Update);
    }

    [Fact]
    public async Task In_background_mode_its_own_thread_forwards()
    {
        using var background = new Plugins.UdpForwarder(_network, _time);
        background.Startup();
        var source = Bind("10.0.0.1");
        var destination = Bind("10.0.0.2");
        var forwarding = background.StartForwarding(source.LocalAddress, destination.LocalAddress, Timeout);

        source.TrySend([7, 7], new SystemAddress(IPAddress.Loopback, forwarding.ForwardingPort));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        (byte[], SystemAddress)? received;
        while ((received = TryReceive(destination)) is null && DateTime.UtcNow < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.Equal([7, 7], received?.Item1);
    }

    /// <summary>Samples/UDPForwarder: a peer connects to the forwarder's port and is connected to the other peer.</summary>
    [Fact]
    public void A_peer_connects_to_another_through_the_forwarder()
    {
        using var mesh = new NatPunchthrough.NatMesh();
        using var forwarder = new Plugins.UdpForwarder(mesh.Network, mesh.Time, PeerThreading.Manual);
        var client = mesh.Add("127.0.0.1", null, 0, new ForwarderPump(forwarder));
        var server = mesh.Add("127.0.0.1", null, 1);
        forwarder.Startup();
        // The sample's six seconds: RakNet sends at least every five.
        var forwarding = forwarder.StartForwarding(client.Sockets[0].LocalAddress, server.Sockets[0].LocalAddress, Timeout, "127.0.0.1");
        Assert.Equal(UdpForwarderResult.Success, forwarding.Result);

        mesh.Connect(client, forwarding.ForwardingSocket!.LocalAddress, server);

        var incoming = mesh.Seen(server).Single(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(client.RakNetGuid, incoming.RakNetGuid);
        Assert.Equal(forwarding.ForwardingSocket.LocalAddress, incoming.Address);
        client.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1 }, server.RakNetGuid, Reliability.ReliableOrdered);
        mesh.RunUntil(() => mesh.Seen(server).Any(e => e.Id == (byte)MessageId.UserPacketEnum), "the message through the forwarder");
    }

    [Fact]
    public void An_entry_whose_socket_fails_is_removed_and_the_others_keep_forwarding()
    {
        var factory = new FailingTransportFactory(_network);
        using var forwarder = new Plugins.UdpForwarder(factory, _time, PeerThreading.Manual);
        forwarder.Startup();
        var source = Bind("10.0.0.1");
        var destination = Bind("10.0.0.2");
        forwarder.StartForwarding(Address("10.0.0.5", 1), Address("10.0.0.6", 1), Timeout);
        var working = forwarder.StartForwarding(source.LocalAddress, destination.LocalAddress, Timeout);
        var forwardingAddress = new SystemAddress(IPAddress.Loopback, working.ForwardingPort);

        factory.Bound[0].Fail = true;
        source.TrySend([1, 2, 3], forwardingAddress);
        forwarder.Update();

        Assert.Equal(1, forwarder.UsedForwardEntries);
        Assert.True(factory.Bound[0].IsDisposed);
        AssertReceived(destination, [1, 2, 3], forwardingAddress);
    }

    public void Dispose()
    {
        _forwarder.Dispose();
        foreach (var socket in _sockets)
            socket.Dispose();
    }

    private static SystemAddress Address(string ip, int port) => new(IPAddress.Parse(ip), port);

    private IDatagramTransport Bind(string ip)
    {
        Assert.True(_network.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Parse(ip), 0)), out var socket, out _, out _));
        _sockets.Add(socket);
        return socket;
    }

    private static void AssertReceived(IDatagramTransport socket, byte[] data, SystemAddress from)
    {
        var received = TryReceive(socket);
        Assert.NotNull(received);
        Assert.Equal(data, received.Value.Data);
        Assert.Equal(from, received.Value.From);
    }

    private static (byte[] Data, SystemAddress From)? TryReceive(IDatagramTransport socket)
    {
        var buffer = new byte[2048];
        return socket.TryReceive(buffer, out var length, out var from) ? (buffer[..length], from) : null;
    }
}

/// <summary>Runs a manual-mode <see cref="Plugins.UdpForwarder"/> on the dispatch step of the peer it is attached to.</summary>
internal sealed class ForwarderPump(Plugins.UdpForwarder forwarder) : IRakPlugin
{
    void IRakPlugin.OnUpdate() => forwarder.Update();
}

/// <summary>Binds through an <see cref="InMemoryNetwork"/>, with sockets that can be made to fail as a real socket does.</summary>
internal sealed class FailingTransportFactory(InMemoryNetwork network) : IDatagramTransportFactory
{
    public List<FailingTransport> Bound { get; } = [];

    public bool TryBind(SocketBinding binding, [NotNullWhen(true)] out IDatagramTransport? transport, out StartupError startupError, out SocketError socketError)
    {
        transport = null;
        if (!network.TryBind(binding, out var inner, out startupError, out socketError))
            return false;
        var failing = new FailingTransport(inner);
        Bound.Add(failing);
        transport = failing;
        return true;
    }
}

/// <summary>Once <see cref="Fail"/> is set, receiving throws as UdpDatagramTransport rethrows a fatal socket error.</summary>
internal sealed class FailingTransport(IDatagramTransport inner) : IDatagramTransport
{
    public bool Fail { get; set; }

    public bool IsDisposed { get; private set; }

    public SystemAddress LocalAddress => inner.LocalAddress;

    public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote) =>
        Fail ? throw new SocketException((int)SocketError.NetworkDown) : inner.TryReceive(buffer, out length, out remote);

    public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0) => inner.TrySend(datagram, remote, ttl);

    public void SetReadySignal(Action signal) => inner.SetReadySignal(signal);

    public void Dispose()
    {
        IsDisposed = true;
        inner.Dispose();
    }
}
