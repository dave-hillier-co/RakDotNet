using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Transport;

namespace RakDotNet.Tests.Transport;

// Sociable tests: two transports on loopback sockets, with the real PeerCore and the system clock.
public sealed class PeerTransportTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private PeerTransport _server = null!;
    private PeerTransport _client = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _server = await PeerTransport.StartAsync(new PeerTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0), MaxIncomingConnections = 8 }, Token);
        _client = await PeerTransport.StartAsync(new PeerTransportOptions { LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public void Startup_binds_an_ephemeral_port_and_chooses_a_guid()
    {
        Assert.Equal(IPAddress.Loopback, _server.BoundEndPoint.Address);
        Assert.NotEqual(0, _server.BoundEndPoint.Port);
        Assert.NotEqual(_server.Guid, _client.Guid);
        Assert.NotEqual(Protocol.Wire.Messages.RakNetConstants.UnassignedGuid, _server.Guid);
    }

    [Fact]
    public async Task An_unconnected_ping_is_answered_over_the_socket()
    {
        _server.Execute((core, _) => core.SetOfflinePingResponse("hello"u8));

        Assert.True(_client.Execute((core, nowUs) => core.Ping(_server.BoundEndPoint, onlyIfAccepting: false, nowUs)));

        var pong = await NextAsync<UnconnectedPongReceived>(_client);
        Assert.Equal(_server.BoundEndPoint, pong.Remote);
        Assert.Equal(_server.Guid, pong.Guid);
        Assert.Equal("hello"u8.ToArray(), pong.ResponseData.ToArray());
        var ping = await NextAsync<UnconnectedPingReceived>(_server);
        Assert.Equal(_client.Guid, ping.Guid);
    }

    [Fact]
    public async Task Two_transports_connect_exchange_a_message_and_disconnect_in_order()
    {
        var result = _client.Execute((core, nowUs) => core.Connect(_server.BoundEndPoint, default, 12, 500, 0, nowUs));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, result);

        var accepted = await NextAsync<OutgoingConnectionCompleted>(_client);
        Assert.Equal(_server.Guid, accepted.Guid);
        var incoming = await NextAsync<IncomingConnectionCompleted>(_server);
        Assert.Equal(_client.Guid, incoming.Guid);

        byte[] message = [(byte)MessageId.UserPacketEnum, 1, 2, 3];
        var receipt = _client.Execute((core, nowUs) => core.Send(_server.Guid, message, Reliability.ReliableOrderedWithAckReceipt, Priority.High, 0, nowUs));
        // Closing before the receipt arrives could lose it, as in RakPeer: when the ACK that completes the receipt also
        // leaves nothing outgoing, the failure check closes the connection before the receipt is read
        // (RakPeer.cpp:5874-5939).
        Assert.Equal(receipt, (await NextAsync<SendReceiptAcked>(_client)).Receipt);
        _client.Execute((core, nowUs) => core.Send(_server.Guid, message, Reliability.ReliableOrdered, Priority.High, 0, nowUs));
        _client.Execute((core, nowUs) => core.CloseConnection(_server.Guid, sendDisconnectionNotification: true, 0, Priority.Low, nowUs));

        // The ordered event stream: the messages come before the disconnection.
        await NextAsync<MessageReceived>(_server);
        Assert.Equal(message, (await NextAsync<MessageReceived>(_server)).Packet.ToArray());
        Assert.Equal(_client.Guid, (await NextAsync<Disconnected>(_server)).Guid);
        await NextAsync<Disconnected>(_client);
    }

    [Fact]
    public async Task Shutting_down_notifies_connected_systems_then_ends_the_event_stream()
    {
        _client.Execute((core, nowUs) => core.Connect(_server.BoundEndPoint, default, 12, 500, 0, nowUs));
        await NextAsync<OutgoingConnectionCompleted>(_client);
        await NextAsync<IncomingConnectionCompleted>(_server);

        await _client.ShutdownAsync(TimeSpan.FromMilliseconds(500), Token);

        Assert.Equal(_client.Guid, (await NextAsync<Disconnected>(_server)).Guid);
        await ReadToEndAsync(_client);
    }

    [Fact]
    public async Task Commands_after_shutdown_throw()
    {
        await _client.ShutdownAsync(TimeSpan.Zero, Token);

        Assert.Throws<ObjectDisposedException>(() => _client.Execute((core, _) => core.ActiveSystemCount));
    }

    private static async Task ReadToEndAsync(PeerTransport transport)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Timeout);
        await foreach (var _ in transport.Events.ReadAllAsync(deadline.Token))
        {
        }
    }

    // Skips events of other types, as a scenario waiting for one packet would.
    private static async Task<T> NextAsync<T>(PeerTransport transport)
        where T : Protocol.Peer.PeerEvent
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Timeout);
        await foreach (var peerEvent in transport.Events.ReadAllAsync(deadline.Token))
        {
            if (peerEvent is T wanted)
                return wanted;
        }
        throw new InvalidOperationException($"The event stream ended before a {typeof(T).Name}.");
    }
}
