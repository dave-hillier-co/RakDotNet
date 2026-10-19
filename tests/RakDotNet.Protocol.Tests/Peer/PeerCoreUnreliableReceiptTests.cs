using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Peer;

// Sociable: a client and a server PeerCore over an in-memory link. Every send's receipt has to finish, so an unreliable
// receipt that waits behind a congested window must still end with an acked, lost or discarded event at the peer.
public class PeerCoreUnreliableReceiptTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public PeerCoreUnreliableReceiptTests()
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        _server = _link.Attach(ServerAddress, new PeerCore(new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8 }));
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    [Fact]
    public void An_unreliable_receipt_queued_behind_a_full_window_for_over_a_second_still_finishes()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => ClientEvents.OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(1));
        _link.Run(TimeSpan.FromMilliseconds(50));

        // Nothing comes back from the server, so the client's window stays full and its send queue backs up.
        _link.Drop = (from, _, _) => from.Equals(ServerAddress);
        var payload = new byte[1_000];
        payload[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 100; i++)
            _client.Send(ServerAddress, payload, Reliability.Reliable, Priority.High, 0, _link.NowUs);
        var receipt = _client.Send(ServerAddress, payload, Reliability.UnreliableWithAckReceipt, Priority.High, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(1_500));

        _link.Drop = null;
        _link.RunUntil(
            () => ClientEvents.OfType<SendReceiptAcked>().Any(e => e.Receipt == receipt)
                || ClientEvents.OfType<SendReceiptLost>().Any(e => e.Receipt == receipt)
                || ClientEvents.OfType<SendReceiptDiscarded>().Any(e => e.Receipt == receipt),
            TimeSpan.FromSeconds(5)
        );
    }
}
