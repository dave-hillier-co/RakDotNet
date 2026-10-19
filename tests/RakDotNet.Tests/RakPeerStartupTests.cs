using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

public class RakPeerStartupTests
{
    private static RakPeerOptions LoopbackOptions => new() { MaxConnections = 4, Bindings = [ManualPeers.Loopback] };

    [Fact]
    public void Start_binds_every_binding_and_reports_its_socket()
    {
        using var peer = RakPeer.Start(LoopbackOptions with { Bindings = [ManualPeers.Loopback, ManualPeers.Loopback] });

        Assert.True(peer.IsActive);
        Assert.Equal(2, peer.Sockets.Count);
        Assert.All(peer.Sockets, s => Assert.True(s.IsOpen));
        Assert.Equal([0, 1], peer.Sockets.Select(s => s.Index));
        Assert.Equal(IPAddress.Loopback, peer.Sockets[0].LocalAddress.ToIPEndPoint().Address);
        Assert.NotEqual(0, peer.Sockets[0].LocalAddress.Port);
        Assert.NotEqual(peer.Sockets[0].LocalAddress, peer.Sockets[1].LocalAddress);
        Assert.Equal(AddressFamily.InterNetwork, peer.Sockets[0].AddressFamily);
    }

    [Fact]
    public void A_socket_bound_to_every_interface_reports_its_address_as_127_0_0_1()
    {
        // RNS2_Berkley::GetSystemAddressIPV4 (RakNetSocket2_Berkley.cpp:89-102) replaces INADDR_ANY with 127.0.0.1, so
        // GetMyBoundAddress reports 127.0.0.1 and the bound port.
        using var peer = RakPeer.Start(LoopbackOptions with { Bindings = [SocketBinding.AnyIPv4] });

        Assert.Equal(IPAddress.Loopback, peer.Sockets[0].LocalAddress.ToIPEndPoint().Address);
        Assert.NotEqual(0, peer.Sockets[0].LocalAddress.Port);
    }

    [Fact]
    public void Socket_bindings_default_to_raknets_buffer_sizes()
    {
        // RNS2_Berkley::SetSocketOptions (RakNetSocket2_Berkley.cpp:25-53).
        Assert.Equal(256 * 1024, SocketBinding.AnyIPv4.ReceiveBufferSize);
        Assert.Equal(16 * 1024, SocketBinding.Port(1).SendBufferSize);
    }

    [Fact]
    public void Start_draws_a_guid_that_is_neither_zero_nor_unassigned_unless_one_is_given()
    {
        using var random = RakPeer.Start(LoopbackOptions);
        using var fixedGuid = RakPeer.Start(LoopbackOptions with { RakNetGuid = new RakNetGuid(1234) });

        Assert.True(random.RakNetGuid.IsAssigned);
        Assert.NotEqual(0UL, random.RakNetGuid.Value);
        Assert.Equal(new RakNetGuid(1234), fixedGuid.RakNetGuid);
        Assert.Equal(fixedGuid.RakNetGuid, fixedGuid.GetGuidFromAddress(SystemAddress.Unassigned));
    }

    [Fact]
    public void Start_reports_a_port_already_in_use_with_the_failing_binding()
    {
        using var first = RakPeer.Start(LoopbackOptions);
        var taken = new SocketBinding(first.Sockets[0].LocalAddress.ToIPEndPoint());

        var error = Assert.Throws<RakStartupException>(() => RakPeer.Start(LoopbackOptions with { Bindings = [ManualPeers.Loopback, taken] }));

        Assert.Equal(StartupError.SocketPortAlreadyInUse, error.Error);
        Assert.Equal(1, error.BindingIndex);
        Assert.Equal(SocketError.AddressAlreadyInUse, error.SocketError);
    }

    [Fact]
    public void Start_rejects_invalid_options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RakPeer.Start(LoopbackOptions with { MaxConnections = 0 }));
        Assert.Throws<ArgumentException>(() => RakPeer.Start(LoopbackOptions with { Bindings = [] }));
        // RakPeer.cpp:2271 asserts the offline ping response is under 400 bytes.
        Assert.Throws<ArgumentException>(() => RakPeer.Start(LoopbackOptions with { OfflinePingResponse = new byte[400] }));
        Assert.Throws<ArgumentException>(() => RakPeer.Start(LoopbackOptions with { IncomingPassword = new byte[256] }));
    }

    [Fact]
    public void The_incoming_connection_limit_is_clamped_to_the_connection_limit()
    {
        // RakPeer.cpp:605
        using var peer = RakPeer.Start(LoopbackOptions with { MaxIncomingConnections = 100 });

        Assert.Equal(4, peer.MaxConnections);
        Assert.Equal(4, peer.MaxIncomingConnections);
        peer.MaxIncomingConnections = 2;
        Assert.Equal(2, peer.MaxIncomingConnections);
    }

    [Fact]
    public void The_offline_ping_response_is_copied_and_limited()
    {
        using var peer = RakPeer.Start(LoopbackOptions);
        var response = "hello"u8.ToArray();

        peer.OfflinePingResponse = response;
        response[0] = 0;

        Assert.Equal("hello"u8.ToArray(), peer.OfflinePingResponse.ToArray());
        Assert.Throws<ArgumentException>(() => peer.OfflinePingResponse = new byte[400]);
    }

    [Fact]
    public void Update_throws_in_background_mode()
    {
        using var peer = RakPeer.Start(LoopbackOptions);

        Assert.Throws<InvalidOperationException>(peer.Update);
    }

    [Fact]
    public async Task Shutdown_closes_the_sockets_completes_the_stream_and_makes_commands_throw()
    {
        var peer = RakPeer.Start(LoopbackOptions);
        var socket = peer.Sockets[0];
        var guid = peer.RakNetGuid;

        await peer.ShutdownAsync(TimeSpan.Zero, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(peer.IsActive);
        Assert.False(socket.IsOpen);
        await peer.Events.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(peer.Events.TryRead(out _));
        Assert.False(await peer.Events.WaitToReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(guid, peer.RakNetGuid);
        Assert.Throws<ObjectDisposedException>(() => peer.Connect(new SystemAddress(IPAddress.Loopback, 1)));
        Assert.Throws<ObjectDisposedException>(() => peer.PingUnconnected(new SystemAddress(IPAddress.Loopback, 1)));
        Assert.Throws<ObjectDisposedException>(() => peer.Send(new byte[] { 0x86 }, new RakNetGuid(1), Reliability.Reliable));
        peer.Dispose();
        await peer.DisposeAsync();
    }

    [Fact]
    public void The_local_address_lists_follow_RakNet()
    {
        using var peer = RakPeer.Start(LoopbackOptions);

        // ipList always has MAXIMUM_NUMBER_OF_INTERNAL_IDS entries, padded with UNASSIGNED_SYSTEM_ADDRESS.
        Assert.Equal(10, peer.InternalAddresses.Count);
        Assert.All(peer.LocalAddresses, a => Assert.Equal(AddressFamily.InterNetwork, a.AddressFamily));
        // RakPeer.cpp:2636-2657: 127.0.0.1 or any address of the host.
        Assert.True(peer.IsLocalAddress(IPAddress.Loopback));
        Assert.All(peer.LocalAddresses, a => Assert.True(peer.IsLocalAddress(a)));
        Assert.False(peer.IsLocalAddress(IPAddress.Parse("192.0.2.1")));
    }
}
