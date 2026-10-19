using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// activeSystemList: AddToActiveSystemList appends, RemoveFromActiveSystemList moves the last entry into the hole
// (RakPeer.cpp:3940-3958), and RunUpdateCycle walks it by index (RakPeer.cpp:5832).
public class ActiveSystemListTests
{
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);
    private static readonly IPEndPoint UnverifiedAddress = new(IPAddress.Parse("10.0.0.9"), 50_009);
    private static readonly IPEndPoint FirstClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint SecondClientAddress = new(IPAddress.Parse("10.0.0.3"), 50_003);
    private const ulong UnverifiedGuid = 99;

    private readonly InMemoryLink _link = new();

    [Fact]
    public void A_removal_moves_the_last_system_into_the_hole()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 4);
        var first = Peer(FirstClientAddress, 1);
        var second = Peer(SecondClientAddress, 3);
        TakeUnverifiedSlot(server);
        ConnectAndWait(first);
        ConnectAndWait(second);
        Assert.Equal([UnverifiedGuid, 1UL, 3UL], server.ActiveSystems.Select(s => s.Guid));

        server.CloseConnection(UnverifiedAddress, false, 0, Priority.Low, _link.NowUsOf(server));

        Assert.Equal([3UL, 1UL], server.ActiveSystems.Select(s => s.Guid));
    }

    [Fact]
    public void The_system_moved_into_a_hole_during_the_update_waits_for_the_next_one()
    {
        var server = Peer(ServerAddress, 2, maxIncoming: 4);
        var first = Peer(FirstClientAddress, 1);
        var second = Peer(SecondClientAddress, 3);
        TakeUnverifiedSlot(server);
        ConnectAndWait(first);
        ConnectAndWait(second);
        _link.Run(TimeSpan.FromMilliseconds(50));

        // The second client's message goes out on its next update and reaches the server on the step after.
        second.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, 7], Reliability.Reliable, Priority.Immediate, 0, _link.NowUsOf(second));
        _link.Step();
        // In the same server update, the unverified sender at index 0 sends something other than ID_CONNECTION_REQUEST
        // and is closed during its own turn (RakPeer.cpp:5953-5974), so the second client fills index 0 and is skipped.
        server.Receive(UnverifiedAddress, NonsenseFromUnverifiedSender(), _link.NowUsOf(server));
        var before = _link.EventsOf(server).Count;
        _link.Step();

        Assert.DoesNotContain(_link.EventsOf(server).Skip(before), e => e is MessageReceived);
        _link.Step();
        Assert.Contains(_link.EventsOf(server).Skip(before), e => e is MessageReceived { Guid: 3 });
    }

    private void TakeUnverifiedSlot(PeerCore server)
    {
        server.Receive(UnverifiedAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, UnverifiedGuid)), _link.NowUsOf(server));
        Assert.Equal(ConnectionState.IsConnecting, server.GetConnectionState(UnverifiedAddress));
    }

    private byte[] NonsenseFromUnverifiedSender()
    {
        var session = new ReliabilityLayer(1492, _link.NowUs);
        session.Send([(byte)MessageId.UserPacketEnum], Reliability.Reliable, Priority.Immediate, 0, 0, _link.NowUs);
        session.Update(_link.NowUs + 10_000);
        Assert.True(session.TryDequeueDatagram(out var datagram));
        return datagram;
    }

    private PeerCore Peer(IPEndPoint address, ulong guid, int maxIncoming = 0) =>
        _link.Attach(address, new PeerCore(new PeerOptions { Guid = guid, MaxIncomingConnections = maxIncoming }));

    private void ConnectAndWait(PeerCore peer)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, peer.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => _link.EventsOf(peer).OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(1));
        _link.Run(TimeSpan.FromMilliseconds(20));
    }
}
