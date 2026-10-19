using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
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
