using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using RakDotNet.Testing;

namespace RakDotNet.Tests;

// Offline sends go straight to a raw endpoint on an InMemoryNetwork, so the test sees the exact datagram. Expected
// bytes are derived from RakPeer.cpp: SendOutOfBand (2997-3044), AdvertiseSystem (2683-2689) and SendTTL (2731-2753).
public sealed class LowLevelTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryNetwork _network;
    private readonly IDatagramTransport _raw;
    private readonly RakPeer _peer;

    public LowLevelTests()
    {
        _network = new InMemoryNetwork(_time);
        Assert.True(_network.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0)), out var raw, out _, out _));
        _raw = raw;
        _peer = Start(_network);
    }

    [Fact]
    public void SendOutOfBand_sends_the_header_then_the_payload_on_the_next_cycle()
    {
        var observer = new SentDatagrams();
        _peer.LowLevel.Observers.Attach(observer);

        Assert.True(_peer.LowLevel.SendOutOfBand(_raw.LocalAddress, [1, 2, 3]));
        Assert.Empty(Received());
        Update();

        var expected = Header(_peer.RakNetGuid).Concat(new byte[] { 1, 2, 3 }).ToArray();
        Assert.Equal([expected], Received());
        Assert.Contains(observer.Sent, d => d.Data.SequenceEqual(expected) && d.Remote == _raw.LocalAddress);
    }

    [Fact]
    public async Task SendOutOfBandAsync_resolves_the_host()
    {
        Assert.True(await _peer.LowLevel.SendOutOfBandAsync("127.0.0.1", _raw.LocalAddress.Port, new byte[] { 9 }, cancellationToken: TestContext.Current.CancellationToken));
        Update();

        Assert.Equal([Header(_peer.RakNetGuid).Append((byte)9).ToArray()], Received());
    }

    [Fact]
    public void SendOutOfBand_returns_false_for_an_unassigned_address()
    {
        Assert.False(_peer.LowLevel.SendOutOfBand(SystemAddress.Unassigned, [1]));
    }

    [Fact]
    public void SendOutOfBand_rejects_a_payload_the_receiver_would_drop()
    {
        // RakPeer.cpp:4708-4710: the receiver takes payloads under MAX_OFFLINE_DATA_LENGTH.
        Assert.Throws<ArgumentException>(() => _peer.LowLevel.SendOutOfBand(_raw.LocalAddress, new byte[RakNetConstants.MaxOfflineDataLength]));
        Assert.Throws<ArgumentOutOfRangeException>(() => _peer.LowLevel.SendOutOfBand(_raw.LocalAddress, [1], socketIndex: 1));
    }

    [Fact]
    public void AdvertiseSystem_is_an_out_of_band_ID_ADVERTISE_SYSTEM()
    {
        Assert.True(_peer.AdvertiseSystem(_raw.LocalAddress, [7, 8]));
        Update();

        var expected = Header(_peer.RakNetGuid).Concat(new byte[] { (byte)MessageId.AdvertiseSystem, 7, 8 }).ToArray();
        Assert.Equal([expected], Received());
    }

    [Fact]
    public async Task AdvertiseSystemAsync_resolves_the_host()
    {
        Assert.True(await _peer.AdvertiseSystemAsync("127.0.0.1", _raw.LocalAddress.Port, cancellationToken: TestContext.Current.CancellationToken));
        Update();

        Assert.Equal([Header(_peer.RakNetGuid).Append((byte)MessageId.AdvertiseSystem).ToArray()], Received());
    }

    [Fact]
    public void SendTtl_sends_two_fake_bytes()
    {
        Assert.True(_peer.LowLevel.SendTtl(_raw.LocalAddress, 3));
        Update();

        Assert.Equal([new byte[] { 0, 1 }], Received());
    }

    [Fact]
    public void Offline_sends_after_shutdown_throw()
    {
        _peer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _peer.LowLevel.SendOutOfBand(_raw.LocalAddress, [1]));
        Assert.Throws<ObjectDisposedException>(() => _peer.AdvertiseSystem(_raw.LocalAddress));
        Assert.Throws<ObjectDisposedException>(() => _peer.LowLevel.SendTtl(_raw.LocalAddress, 3));
    }

    [Fact]
    public void WriteOutOfBandHeader_writes_into_a_BitStream()
    {
        using var stream = new BitStream();
        _peer.LowLevel.WriteOutOfBandHeader(stream);

        Assert.Equal(Header(_peer.RakNetGuid), stream.Written.ToArray());
    }

    [Fact]
    public void SetInternalAddress_changes_the_list_a_remote_receives()
    {
        var internalAddress = new SystemAddress(IPAddress.Parse("10.1.2.3"), 4567);
        using var server = Start(_network, maxIncoming: 1);
        _peer.SetInternalAddress(internalAddress, 1);
        Assert.Equal(internalAddress, _peer.InternalAddresses[1]);

        Assert.Equal(ConnectAttemptResult.Started, _peer.Connect(server.Sockets[0].LocalAddress));
        RakConnection? onServer = null;
        for (var i = 0; i < 200 && onServer is null; i++)
        {
            Update();
            server.Update();
            while (server.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Connected)
                    onServer = e.Connection;
            }
        }

        Assert.NotNull(onServer);
        Assert.Equal(internalAddress, onServer.RemoteInternalAddresses[1]);
    }

    [Fact]
    public void SetInternalAddress_checks_the_index()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _peer.SetInternalAddress(new SystemAddress(IPAddress.Loopback, 1), RakNetConstants.MaximumNumberOfInternalIds));
        Assert.Throws<ArgumentOutOfRangeException>(() => _peer.SetInternalAddress(new SystemAddress(IPAddress.Loopback, 1), -1));
    }

    [Fact]
    public void The_network_simulator_drops_connected_datagrams_but_not_offline_ones()
    {
        using var lossy = Start(_network, simulator: new NetworkSimulatorOptions(1f, TimeSpan.Zero, TimeSpan.Zero));
        using var server = Start(_network, maxIncoming: 1);
        Assert.True(lossy.LowLevel.IsNetworkSimulatorActive);

        Assert.True(lossy.PingUnconnected(server.Sockets[0].LocalAddress));
        Assert.Equal(ConnectAttemptResult.Started, lossy.Connect(server.Sockets[0].LocalAddress, new ConnectOptions { AttemptCount = 1, AttemptInterval = TimeSpan.FromMilliseconds(100) }));
        var seen = new List<PeerEventKind>();
        for (var i = 0; i < 300; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(10));
            lossy.Update();
            server.Update();
            while (lossy.Events.TryRead(out var e))
                seen.Add(e.Kind);
            while (server.Events.TryRead(out _))
            {
            }
        }

        Assert.Contains(PeerEventKind.UnconnectedPong, seen);
        Assert.DoesNotContain(PeerEventKind.Connected, seen);
    }

    [Fact]
    public void The_network_simulator_delays_connected_datagrams()
    {
        using var server = Start(_network, maxIncoming: 1);
        Assert.Equal(ConnectAttemptResult.Started, _peer.Connect(server.Sockets[0].LocalAddress));
        RakConnection? toServer = null;
        for (var i = 0; i < 200 && toServer is null; i++)
        {
            Update();
            server.Update();
            while (_peer.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Connected)
                    toServer = e.Connection;
            }
            while (server.Events.TryRead(out _))
            {
            }
        }
        Assert.NotNull(toServer);

        _peer.LowLevel.NetworkSimulator = new NetworkSimulatorOptions(0f, TimeSpan.FromMilliseconds(100), TimeSpan.Zero);
        Update();
        Assert.True(_peer.LowLevel.IsNetworkSimulatorActive);
        toServer.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1 }, Reliability.ReliableOrdered);
        var arrivedAfter = TimeSpan.Zero;
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromSeconds(1) && arrivedAfter == TimeSpan.Zero; elapsed += TimeSpan.FromMilliseconds(10))
        {
            Update();
            server.Update();
            while (server.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Message)
                    arrivedAfter = elapsed;
            }
        }

        Assert.InRange(arrivedAfter, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(150));
        _peer.LowLevel.NetworkSimulator = null;
        Update();
        Assert.False(_peer.LowLevel.IsNetworkSimulatorActive);
    }

    [Fact]
    public void A_simulator_with_no_impairment_is_not_active()
    {
        _peer.LowLevel.NetworkSimulator = new NetworkSimulatorOptions(0f, TimeSpan.Zero, TimeSpan.Zero);

        Assert.False(_peer.LowLevel.IsNetworkSimulatorActive);
    }

    public void Dispose()
    {
        _peer.Dispose();
        _raw.Dispose();
    }

    private RakPeer Start(InMemoryNetwork network, int maxIncoming = 0, NetworkSimulatorOptions? simulator = null) => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 2,
        MaxIncomingConnections = maxIncoming,
        Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
        Transport = network,
        Threading = PeerThreading.Manual,
        TimeProvider = _time,
        NetworkSimulator = simulator,
    });

    private void Update()
    {
        _time.Advance(TimeSpan.FromMilliseconds(10));
        _peer.Update();
    }

    private List<byte[]> Received()
    {
        var received = new List<byte[]>();
        var buffer = new byte[2048];
        while (_raw.TryReceive(buffer, out var length, out var from))
        {
            Assert.Equal(_peer.Sockets[0].LocalAddress, from);
            received.Add(buffer[..length]);
        }
        return received;
    }

    private static byte[] Header(RakNetGuid guid)
    {
        var buffer = new byte[OutOfBandHeader.Length];
        var writer = new BitStreamWriter(buffer);
        new OutOfBandHeader(guid).Write(ref writer);
        return buffer;
    }

    private sealed class SentDatagrams : INetworkObserver
    {
        public List<(byte[] Data, SystemAddress Remote)> Sent { get; } = [];

        public void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote) => Sent.Add((datagram.ToArray(), remote));
    }
}
