using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Tests;

// RakPeer's lookups, sends and states follow its remote system list: every slot with isActive set, whatever its
// connectMode, and slots used before (RakPeer.cpp:1717-1770, 2394-2473, 3139-3190, 4286-4347).
public class RemoteSystemSlotTests
{
    private const ulong RawGuid = 0x0102_0304_0506_0708;

    [Fact]
    public void An_incoming_handshake_is_Connecting_by_address_and_by_GUID()
    {
        using var peers = new ManualPeers();
        using var raw = RawSocket();
        var rawAddress = (SystemAddress)(IPEndPoint)raw.LocalEndPoint!;

        // OPEN_CONNECTION_REQUEST_2 takes a slot in UNVERIFIED_SENDER (RakPeer.cpp:5366-5417).
        SendOpenConnectionRequest2(raw, peers.ServerAddress);
        peers.RunUntil(() => peers.Server.GetConnectionState(rawAddress) != ConnectionState.NotConnected, "a slot for the raw sender");

        Assert.Equal(ConnectionState.Connecting, peers.Server.GetConnectionState(rawAddress));
        Assert.Equal(ConnectionState.Connecting, peers.Server.GetConnectionState(new RakNetGuid(RawGuid)));
        Assert.True(peers.Server.TryGetConnection(new RakNetGuid(RawGuid), out var connection));
        Assert.Equal(ConnectionState.Connecting, connection.State);
        Assert.True(connection.IsIncoming);
        Assert.Equal(rawAddress, connection.Address);
        Assert.Equal(new RakNetGuid(RawGuid), peers.Server.GetGuidFromAddress(rawAddress));
        Assert.Equal(rawAddress, peers.Server.GetAddressFromGuid(new RakNetGuid(RawGuid)));
        // Not fully connected, so not listed or counted (RakPeer.cpp:1799-1850).
        Assert.Empty(peers.Server.Connections);
        Assert.False(peers.Server.TryGetConnectionByIndex(connection.SystemIndex, out _));
    }

    [Fact]
    public void Connect_to_an_address_with_an_incoming_handshake_is_refused_synchronously()
    {
        using var peers = new ManualPeers();
        using var raw = RawSocket();
        var rawAddress = (SystemAddress)(IPEndPoint)raw.LocalEndPoint!;
        SendOpenConnectionRequest2(raw, peers.ServerAddress);
        peers.RunUntil(() => peers.Server.GetConnectionState(rawAddress) == ConnectionState.Connecting, "a slot for the raw sender");

        // SendConnectionRequest checks every active slot first (RakPeer.cpp:3275-3276).
        Assert.Equal(ConnectAttemptResult.AlreadyConnectedToEndpoint, peers.Server.Connect(rawAddress));
    }

    [Fact]
    public void A_connection_that_ended_is_Disconnected_by_address_and_NotConnected_by_GUID()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        server.Close();
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.Disconnected), "disconnected");

        // GetIndexFromSystemAddress falls back to a slot the address used before; the slot's GUID was cleared
        // (RakPeer.cpp:3139-3166, 4160).
        Assert.Equal(ConnectionState.Disconnected, peers.Client.GetConnectionState(peers.ServerAddress));
        Assert.Equal(ConnectionState.NotConnected, peers.Client.GetConnectionState(peers.Server.RakNetGuid));
        Assert.Equal(RakNetGuid.Unassigned, peers.Client.GetGuidFromAddress(peers.ServerAddress));
        Assert.Equal(SystemAddress.Unassigned, peers.Client.GetAddressFromGuid(peers.Server.RakNetGuid));
    }

    [Fact]
    public void A_message_sent_while_the_connection_is_being_requested_is_delivered()
    {
        using var peers = new ManualPeers();

        peers.Client.Connect(peers.ServerAddress);
        peers.RunUntil(() => peers.Client.GetConnectionState(peers.ServerAddress) == ConnectionState.Connecting, "a slot for the server");
        Assert.DoesNotContain(peers.ClientEvents, e => e.Kind == PeerEventKind.Connected);
        var sent = peers.Client.Send(new byte[] { 0x86, 1 }, peers.ServerAddress, Reliability.ReliableOrdered);

        // SendImmediate queues to an active slot in REQUESTED_CONNECTION (RakPeer.cpp:4306-4326).
        Assert.Equal(SendStatus.Queued, sent.Status);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Message), "the message");
        Assert.Equal([0x86, 1], peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Message).Data);
    }

    [Fact]
    public void A_broadcast_reaches_a_system_still_being_requested()
    {
        using var peers = new ManualPeers();

        peers.Client.Connect(peers.ServerAddress);
        peers.RunUntil(() => peers.Client.GetConnectionState(peers.ServerAddress) == ConnectionState.Connecting, "a slot for the server");
        Assert.DoesNotContain(peers.ClientEvents, e => e.Kind == PeerEventKind.Connected);
        peers.Client.Broadcast(new byte[] { 0x86, 2 }, Reliability.ReliableOrdered);

        // Broadcast takes every active slot with an address, whatever its connectMode (RakPeer.cpp:4335-4347).
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Message), "the broadcast");
        Assert.Equal([0x86, 2], peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Message).Data);
    }

    [Fact]
    public void A_broadcast_reaches_a_disconnecting_system_that_a_targeted_send_does_not()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();
        peers.Settle();

        server.Close();
        server.Send(new byte[] { 0x86, 1 }, Reliability.ReliableOrdered);
        peers.Client.Broadcast(new byte[] { 0x86, 2 }, Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Disconnected), "disconnected");

        // A targeted send skips DISCONNECT_ASAP (RakPeer.cpp:4320-4323); a broadcast does not check the mode.
        var messages = peers.ServerEvents.Where(e => e.Kind == PeerEventKind.Message).Select(e => e.Data).ToList();
        Assert.Equal([[0x86, 2]], messages);
    }

    [Fact]
    public void A_disconnecting_connection_is_not_found_by_index()
    {
        using var peers = new ManualPeers();
        var server = peers.Connect();

        server.Close();
        peers.Client.Update();

        // GetSystemAddressFromIndex and GetGUIDFromIndex need CONNECTED (RakPeer.cpp:1799-1825).
        Assert.Equal(ConnectionState.Disconnecting, server.State);
        Assert.False(peers.Client.TryGetConnectionByIndex(server.SystemIndex, out _));
    }

    [Fact]
    public void This_peers_own_GUID_maps_to_its_first_internal_address()
    {
        using var peers = new ManualPeers();

        // GetSystemAddressFromGuid returns GetInternalID(UNASSIGNED_SYSTEM_ADDRESS) for myGuid (RakPeer.cpp:2452-2453).
        Assert.Equal(peers.Client.InternalAddresses[0], peers.Client.GetAddressFromGuid(peers.Client.RakNetGuid));
    }

    [Fact]
    public void The_connection_list_follows_the_active_system_list_after_a_removal()
    {
        using var peers = new ManualPeers();
        using var second = RakPeer.Start(peers.Options(maxIncoming: 0));
        using var third = RakPeer.Start(peers.Options(maxIncoming: 0));
        var first = peers.Connect();
        ConnectAndStep(peers, second);
        ConnectAndStep(peers, third);
        Assert.Equal([peers.Client.RakNetGuid, second.RakNetGuid, third.RakNetGuid], peers.Server.Connections.Select(c => c.RakNetGuid));

        first.Close();
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Disconnected), "the first client gone");

        // RemoveFromActiveSystemList moves the last entry into the hole, and GetSystemList walks activeSystemList
        // (RakPeer.cpp:3945-3958, 1832-1850).
        Assert.Equal([third.RakNetGuid, second.RakNetGuid], peers.Server.Connections.Select(c => c.RakNetGuid));
    }

    private static void ConnectAndStep(ManualPeers peers, RakPeer client)
    {
        client.Connect(peers.ServerAddress);
        var connected = false;
        peers.RunUntil(() =>
        {
            client.Update();
            while (client.Events.TryRead(out var e))
                connected |= e.Kind == PeerEventKind.Connected;
            return connected && peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Connected && e.RakNetGuid == client.RakNetGuid);
        }, "connected");
    }

    private static Socket RawSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static void SendOpenConnectionRequest2(Socket raw, SystemAddress server)
    {
        var writer = new BitStreamWriter(new byte[64]);
        new OpenConnectionRequest2(server.ToIPEndPoint(), 1492, RawGuid).Write(ref writer);
        raw.SendTo(writer.Written, server.ToIPEndPoint());
    }
}
