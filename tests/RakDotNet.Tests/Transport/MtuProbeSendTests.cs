using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Tests.Transport;

// RakPeer::RunUpdateCycle's MTU probe send (RakPeer.cpp:5779-5821): don't-fragment around each
// ID_OPEN_CONNECTION_REQUEST_1, a probe too large for the path moves on to the next MTU, and a probe slow to send drops
// to the smallest MTU.
public sealed class MtuProbeSendTests
{
    [Fact]
    public void Only_mtu_probes_are_sent_with_dont_fragment()
    {
        using var path = new PathTransport();
        using var peers = new ManualPeers(client: o => o with { Bindings = [path.Binding] });

        peers.Connect();

        Assert.NotEmpty(path.DoNotFragmentSends);
        Assert.All(path.DoNotFragmentSends, d => Assert.Equal((byte)MessageId.OpenConnectionRequest1, d[0]));
        Assert.DoesNotContain(path.PlainSends, d => d[0] == (byte)MessageId.OpenConnectionRequest1);
    }

    [Fact]
    public void A_probe_too_large_for_the_path_moves_on_to_1200_at_once()
    {
        // RakPeer.cpp:5791-5797: on WSAEMSGSIZE the rest of the 1492 attempts are skipped.
        using var path = new PathTransport { MaxDoNotFragmentLength = 1200 - RakNetConstants.UdpHeaderSize };
        using var peers = new ManualPeers(client: o => o with { Bindings = [path.Binding] });

        var connection = peers.Connect();

        Assert.Equal(1200, connection.Mtu);
        Assert.Equal([1492, 1200], path.DoNotFragmentSends.Select(d => d.Length + RakNetConstants.UdpHeaderSize));
    }

    [Fact]
    public void A_probe_that_takes_over_100_ms_to_send_drops_to_576()
    {
        // RakPeer.cpp:5798-5808: the send is timed, and one taking more than 100 ms moves to the smallest MTU at once.
        // The slow probe is lost on the way, so the attempt continues from the next probe.
        using var path = new PathTransport();
        using var peers = new ManualPeers(client: o => o with { Bindings = [path.Binding] });
        path.Sending = () =>
        {
            if (path.DoNotFragmentSends.Count > 1)
                return true;
            peers.Time.Advance(TimeSpan.FromMilliseconds(101));
            return false;
        };

        var connection = peers.Connect();

        Assert.Equal(576, connection.Mtu);
        Assert.Equal([1492, 576], path.DoNotFragmentSends.Select(d => d.Length + RakNetConstants.UdpHeaderSize));
    }

    [Fact]
    public void Probes_bypass_the_network_simulator()
    {
        // RakNet applies the simulator in ReliabilityLayer only (ReliabilityLayer.cpp:2257-2296), so the probes sent by
        // RakPeer.cpp:5779-5821 keep don't-fragment and the too-large report while the simulator delays connected traffic.
        using var path = new PathTransport { MaxDoNotFragmentLength = 1200 - RakNetConstants.UdpHeaderSize };
        using var peers = new ManualPeers(client: o => o with
        {
            Bindings = [path.Binding],
            NetworkSimulator = new NetworkSimulatorOptions(0f, TimeSpan.FromMilliseconds(50), TimeSpan.Zero),
        });

        var connection = peers.Connect();

        Assert.Equal(1200, connection.Mtu);
        Assert.Equal([1492, 1200], path.DoNotFragmentSends.Select(d => d.Length + RakNetConstants.UdpHeaderSize));
    }

    /// <summary>A loopback UDP transport standing in for a path whose MTU is <see cref="MaxDoNotFragmentLength"/>.</summary>
    private sealed class PathTransport : IDatagramTransport
    {
        private readonly IDatagramTransport _inner;

        public PathTransport()
        {
            Assert.True(UdpTransportFactory.Instance.TryBind(ManualPeers.Loopback, out var inner, out _, out _));
            _inner = inner;
            Binding = ManualPeers.Loopback with { Transport = this };
        }

        public SocketBinding Binding { get; }

        public int MaxDoNotFragmentLength { get; init; } = int.MaxValue;

        /// <summary>Runs inside each don't-fragment send, after it is recorded; false loses the datagram.</summary>
        public Func<bool>? Sending { get; set; }

        public List<byte[]> DoNotFragmentSends { get; } = [];

        public List<byte[]> PlainSends { get; } = [];

        public SystemAddress LocalAddress => _inner.LocalAddress;

        public bool TryReceive(Span<byte> buffer, out int length, out SystemAddress remote) => _inner.TryReceive(buffer, out length, out remote);

        public bool TrySend(ReadOnlySpan<byte> datagram, in SystemAddress remote, int ttl = 0)
        {
            PlainSends.Add(datagram.ToArray());
            return _inner.TrySend(datagram, remote, ttl);
        }

        public bool TrySendDoNotFragment(ReadOnlySpan<byte> datagram, in SystemAddress remote, out SocketError socketError)
        {
            DoNotFragmentSends.Add(datagram.ToArray());
            if (Sending is { } sending && !sending())
            {
                socketError = SocketError.Success;
                return true;
            }
            if (datagram.Length > MaxDoNotFragmentLength)
            {
                socketError = SocketError.MessageSize;
                return false;
            }
            return _inner.TrySendDoNotFragment(datagram, remote, out socketError);
        }

        public void SetReadySignal(Action signal) => _inner.SetReadySignal(signal);

        public void Dispose() => _inner.Dispose();
    }
}
