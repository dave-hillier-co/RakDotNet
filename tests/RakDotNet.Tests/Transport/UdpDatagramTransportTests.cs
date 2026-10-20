using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using RakDotNet.Transport;

namespace RakDotNet.Tests.Transport;

public sealed class UdpDatagramTransportTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly UdpDatagramTransport _transport;
    private readonly Socket _sender = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly SemaphoreSlim _ready = new(0);

    public UdpDatagramTransportTests()
    {
        Assert.True(UdpDatagramTransport.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0)), out var transport, out _, out _));
        _transport = transport!;
        // The bind's own test send (RakNetSocket2.cpp:317-326), taken before the ready signal is set so it is not counted.
        Assert.Equal(new byte[4], ReceiveOne(_transport).Data);
        _transport.SetReadySignal(Signal);
        _sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    [Fact]
    public void A_datagram_waiting_in_the_slot_is_not_overwritten_by_arming_again()
    {
        var buffer = new byte[2048];
        // Drained: the transport arms its receive.
        Assert.False(_transport.TryReceive(buffer, out _, out _));
        Send(1);
        Assert.True(_ready.Wait(Timeout, TestContext.Current.CancellationToken), "The armed receive did not complete.");

        // The consumer missed the slot completing and arms again, with the next datagram already waiting.
        Send(2);
        _transport.Arm();

        Assert.Equal([1, 2], ReceiveAll(buffer, count: 2));
    }

    [Fact]
    public void Binding_sends_four_zero_bytes_to_its_own_address_as_a_test()
    {
        // RNS2_Berkley::BindShared (RakNetSocket2.cpp:317-326) sends 4 zero bytes to its bound address and fails the
        // bind if that send fails; the datagram arrives on the socket itself.
        using var transport = Bind(new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0)));

        var (data, from) = ReceiveOne(transport);

        Assert.Equal(new byte[4], data);
        Assert.Equal(transport.LocalAddress, from);
    }

    [Fact]
    public void A_bound_socket_may_send_to_the_broadcast_address()
    {
        // RakPeer.cpp:505 binds every socket with setBroadcast, applied as SO_BROADCAST (RakNetSocket2_Berkley.cpp:61-64,
        // 176), so Ping and AdvertiseSystem to 255.255.255.255 reach the LAN.
        Broadcast.SkipUnlessAvailable();
        using var wildcard = Bind(SocketBinding.AnyIPv4);

        Assert.True(wildcard.TrySend([1], new SystemAddress(IPAddress.Broadcast, _transport.LocalAddress.Port)));
    }

    [Fact]
    public void A_dont_fragment_send_larger_than_the_route_allows_fails_with_message_size()
    {
        // RakPeer.cpp:5779-5797 sets IP_DONTFRAGMENT around each MTU probe (RakNetSocket2_Berkley.cpp:71-80), so a probe
        // too large for the path is refused rather than fragmented. 192.0.2.1 (TEST-NET-1) is routed through the default
        // interface, whose MTU is below 1600.
        var route = new SystemAddress(IPAddress.Parse("192.0.2.1"), 9);
        var datagram = new byte[1600 - RakNetConstants.UdpHeaderSize];
        using var wildcard = Bind(SocketBinding.AnyIPv4);
        Assert.SkipUnless(wildcard.TrySend([1], route), "No route to TEST-NET-1.");

        Assert.True(wildcard.TrySend(datagram, route), "Without don't-fragment the datagram is fragmented.");
        Assert.False(wildcard.TrySendDoNotFragment(datagram, route, out var error));
        Assert.Equal(SocketError.MessageSize, error);
        Assert.True(wildcard.TrySendDoNotFragment(datagram.AsSpan(0, 1000), route, out error));
        Assert.Equal(SocketError.Success, error);
        Assert.True(wildcard.TrySend(datagram, route), "Don't-fragment is cleared after the send.");
    }

    [Fact]
    public void A_dont_fragment_send_leaves_linux_path_mtu_discovery_as_it_found_it()
    {
        // Socket.DontFragment on Linux is IP_MTU_DISCOVER: true is IP_PMTUDISC_DO and false IP_PMTUDISC_DONT, while a new
        // socket starts at IP_PMTUDISC_WANT. Native RakNet never touches the option there (IP_DONTFRAGMENT is not
        // defined, RakNetSocket2_Berkley.cpp:71-80), so later sends must go out as they did before the probe.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "IP_MTU_DISCOVER is Linux only.");
        var before = _transport.PathMtuDiscovery;

        Assert.True(_transport.TrySendDoNotFragment([1, 2, 3], new SystemAddress(IPAddress.Loopback, ((IPEndPoint)_sender.LocalEndPoint!).Port), out _));

        Assert.Equal(before, _transport.PathMtuDiscovery);
    }

    [Fact]
    public void Socket_buffers_default_to_raknets_256_KiB_receive_and_16_KiB_send()
    {
        // RNS2_Berkley::SetSocketOptions (RakNetSocket2_Berkley.cpp:25-53) always sets SO_RCVBUF 262144 and SO_SNDBUF
        // 16384. The sizes are compared with a socket given the same options, since Linux reports them doubled.
        using var reference = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 256 * 1024, SendBufferSize = 16 * 1024 };

        Assert.Equal(reference.ReceiveBufferSize, _transport.ReceiveBufferSize);
        Assert.Equal(reference.SendBufferSize, _transport.SendBufferSize);
    }

    [Fact]
    public void Socket_buffer_sizes_given_in_the_binding_are_used_and_null_keeps_the_os_default()
    {
        using var reference = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ReceiveBufferSize = 100_000 };
        using var unset = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var transport = Bind(new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0)) { ReceiveBufferSize = 100_000, SendBufferSize = null });

        Assert.Equal(reference.ReceiveBufferSize, transport.ReceiveBufferSize);
        Assert.Equal(unset.SendBufferSize, transport.SendBufferSize);
    }

    public void Dispose()
    {
        _transport.Dispose();
        _sender.Dispose();
        _ready.Dispose();
    }

    // Disposing the transport completes its outstanding receive, which signals once more.
    private void Signal()
    {
        try
        {
            _ready.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static UdpDatagramTransport Bind(SocketBinding binding)
    {
        Assert.True(UdpDatagramTransport.TryBind(binding, out var transport, out var error, out var socketError), $"{error} {socketError}");
        return transport!;
    }

    private static (byte[] Data, SystemAddress From) ReceiveOne(UdpDatagramTransport transport)
    {
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (transport.TryReceive(buffer, out var length, out var from))
                return (buffer[..length], from);
            Thread.Sleep(1);
        }
        Assert.Fail("Nothing was received.");
        return default;
    }

    private void Send(byte value) => _sender.SendTo([value], _transport.LocalAddress.ToIPEndPoint());

    private List<byte> ReceiveAll(byte[] buffer, int count)
    {
        var received = new List<byte>();
        var deadline = DateTime.UtcNow + Timeout;
        while (received.Count < count && DateTime.UtcNow < deadline)
        {
            if (_transport.TryReceive(buffer, out var length, out var from))
            {
                Assert.Equal((SystemAddress)(IPEndPoint)_sender.LocalEndPoint!, from);
                Assert.Equal(1, length);
                received.Add(buffer[0]);
            }
            else
            {
                _ready.Wait(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }
        }
        return received;
    }
}
