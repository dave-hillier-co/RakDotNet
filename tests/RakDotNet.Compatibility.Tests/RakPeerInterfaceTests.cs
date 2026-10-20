using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Compatibility.Tests;

// The facade is RakNet-shaped, so these tests read like RakNet's samples: Startup, Connect, then a Receive loop. The
// facade has no transport option, so the peers run on loopback UDP and the system clock.
public sealed class RakPeerInterfaceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly List<RakPeerInterface> _instances = [];

    [Fact]
    public void Startup_reports_RakNets_results()
    {
        var peer = Instance();
        Assert.Equal(StartupResult.InvalidMaxConnections, peer.Startup(0, [Loopback()]));
        Assert.Equal(StartupResult.InvalidSocketDescriptors, peer.Startup(1, []));
        // RakPeer.cpp:411-422 checks the descriptors before maxConnections.
        Assert.Equal(StartupResult.InvalidSocketDescriptors, peer.Startup(0, []));
        Assert.False(peer.IsActive());

        Assert.Equal(StartupResult.RakNetStarted, peer.Startup(1, [Loopback()]));
        Assert.True(peer.IsActive());
        Assert.NotNull(peer.Peer);
        Assert.Equal(StartupResult.RakNetAlreadyStarted, peer.Startup(1, [Loopback()]));
    }

    [Fact]
    public void Startup_on_a_taken_port_reports_it()
    {
        using var taken = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        taken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = (ushort)((IPEndPoint)taken.LocalEndPoint!).Port;

        Assert.Equal(StartupResult.SocketPortAlreadyInUse, Instance().Startup(1, [new SocketDescriptor(port, "127.0.0.1")]));
    }

    [Fact]
    public void A_peer_can_start_again_after_shutdown_as_in_RakNet()
    {
        var peer = Started(1);
        var first = peer.GetMyGUID();
        peer.Shutdown(0);
        Assert.False(peer.IsActive());

        Assert.Equal(StartupResult.RakNetStarted, peer.Startup(1, [Loopback()]));
        Assert.True(peer.IsActive());
        Assert.NotEqual(first, peer.GetMyGUID());
    }

    [Fact]
    public void A_client_connects_sends_and_closes_through_Receive()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", ServerPort(server)));
        using (var accepted = ReceiveUntil(client, MessageId.ConnectionRequestAccepted, server))
            Assert.Equal(server.GetMyGUID(), accepted.RakNetGuid);
        using (var incoming = ReceiveUntil(server, MessageId.NewIncomingConnection, client))
            Assert.Equal(client.GetMyGUID(), incoming.RakNetGuid);

        var serverGuid = server.GetMyGUID();
        Assert.Equal(1, client.NumberOfConnections());
        Assert.Equal(ConnectionState.Connected, client.GetConnectionState(serverGuid));
        var serverAddress = client.GetSystemAddressFromGuid(serverGuid);
        Assert.Equal(serverGuid, client.GetGuidFromSystemAddress(serverAddress));
        Assert.Equal(0, client.GetIndexFromSystemAddress(serverAddress));
        Assert.Equal(serverAddress, client.GetSystemAddressFromIndex(0));
        Assert.Equal(serverGuid, client.GetGUIDFromIndex(0));
        Span<SystemAddress> list = new SystemAddress[4];
        Assert.Equal(1, client.GetConnectionList(list));
        Assert.Equal(serverAddress, list[0]);

        var receipt = client.Send([(byte)MessageId.UserPacketEnum, 1, 2], Priority.High, Reliability.ReliableOrdered, 0, serverGuid, broadcast: false);
        Assert.NotEqual(0u, receipt);
        using (var message = ReceiveUntil(server, MessageId.UserPacketEnum, client))
        {
            Assert.Equal([(byte)MessageId.UserPacketEnum, 1, 2], message.Data.ToArray());
            Assert.Equal(3, message.Length);
            Assert.Equal(24, message.BitSize);
            Assert.False(message.WasGeneratedLocally);
            Assert.Equal(client.GetMyGUID(), message.RakNetGuid);
        }

        client.CloseConnection(serverGuid, sendDisconnectionNotification: true);
        using var gone = ReceiveUntil(server, MessageId.DisconnectionNotification, client);
        Assert.Equal(client.GetMyGUID(), gone.RakNetGuid);
    }

    [Fact]
    public void A_BitStream_send_keeps_its_bits()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);

        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.UserPacketEnum);
        stream.WriteUInt32(0xDEADBEEF);
        Assert.NotEqual(0u, client.Send(stream, Priority.High, Reliability.ReliableOrdered, 0, server.GetMyGUID(), broadcast: false));

        using var message = ReceiveUntil(server, MessageId.UserPacketEnum, client);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 0xDE, 0xAD, 0xBE, 0xEF], message.Data.ToArray());
    }

    [Fact]
    public void SendList_concatenates_the_parts()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);

        var parts = Sequence([(byte)MessageId.UserPacketEnum, 1], [2, 3]);
        Assert.NotEqual(0u, client.SendList(parts, Priority.High, Reliability.ReliableOrdered, 0, server.GetMyGUID(), broadcast: false));

        using var message = ReceiveUntil(server, MessageId.UserPacketEnum, client);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 1, 2, 3], message.Data.ToArray());
    }

    [Fact]
    public void A_broadcast_skips_the_excepted_system()
    {
        var server = Started(4, maxIncoming: 4);
        var first = Started(1);
        var second = Started(1);
        Connect(first, server);
        Connect(second, server);

        Assert.NotEqual(0u, server.Send([(byte)MessageId.UserPacketEnum, 9], Priority.High, Reliability.ReliableOrdered, 0, first.GetMyGUID(), broadcast: true));

        using var atSecond = ReceiveUntil(second, MessageId.UserPacketEnum, server);
        Assert.Equal(9, atSecond.Data.Span[1]);
        Assert.DoesNotContain(Drain(first, server), p => p.Data.Span[0] == (byte)MessageId.UserPacketEnum);
    }

    [Fact]
    public void Send_returns_0_for_bad_input_as_RakNet_does()
    {
        var peer = Started(1);

        Assert.Equal(0u, peer.Send([], Priority.High, Reliability.Reliable, 0, peer.GetMyGUID(), broadcast: false));
        Assert.Equal(0u, peer.Send([1], Priority.High, Reliability.Reliable, 0, default, broadcast: false));
        Assert.Equal(0u, peer.Send([1], Priority.High, Reliability.Reliable, 32, peer.GetMyGUID(), broadcast: false));
        Assert.Equal(0u, Instance().Send([1], Priority.High, Reliability.Reliable, 0, new RakNetGuid(1), broadcast: false));
    }

    [Fact]
    public void A_send_to_an_unconnected_system_still_uses_a_receipt()
    {
        var peer = Started(1);
        var next = peer.GetNextSendReceipt();

        Assert.Equal(next, peer.Send([(byte)MessageId.UserPacketEnum], Priority.High, Reliability.Reliable, 0, new RakNetGuid(42), broadcast: false));
        Assert.Equal(next + 1, peer.GetNextSendReceipt());
        Assert.Equal(next + 1, peer.IncrementNextSendReceipt());
        Assert.Equal(next + 2, peer.GetNextSendReceipt());
    }

    [Fact]
    public void Connect_reports_RakNets_attempt_results()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        var port = ServerPort(server);

        Assert.Equal(ConnectionAttemptResult.InvalidParameter, Instance().Connect("127.0.0.1", port));
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, client.Connect("", port));
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, client.Connect("127.0.0.1", port, connectionSocketIndex: 1));
        Assert.Equal(ConnectionAttemptResult.CannotResolveDomainName, client.Connect("no-such-host.invalid", port));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", port));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress, client.Connect("127.0.0.1", port));
        ReceiveUntil(client, MessageId.ConnectionRequestAccepted, server).Dispose();
        Assert.Equal(ConnectionAttemptResult.AlreadyConnectedToEndpoint, client.Connect("127.0.0.1", port));
    }

    [Fact]
    public void A_refused_connection_arrives_as_RakNets_failure_packet()
    {
        var server = Started(4, maxIncoming: 0);
        var client = Started(1);

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", ServerPort(server)));

        ReceiveUntil(client, MessageId.NoFreeIncomingConnections, server).Dispose();
    }

    [Fact]
    public void An_unconnected_ping_gets_the_offline_ping_response()
    {
        var server = Instance();
        server.SetOfflinePingResponse("hello"u8);
        Assert.Equal(StartupResult.RakNetStarted, server.Startup(1, [Loopback()]));
        var client = Started(1);
        Assert.Equal("hello"u8.ToArray(), server.GetOfflinePingResponse().ToArray());

        Assert.True(client.Ping("127.0.0.1", ServerPort(server), onlyReplyOnAcceptingConnections: false));

        using var pong = ReceiveUntil(client, MessageId.UnconnectedPong, server);
        Assert.Equal("hello"u8.ToArray(), pong.Data.Span[5..].ToArray());
        Assert.Equal(server.GetMyGUID(), pong.RakNetGuid);
    }

    [Fact]
    public void SendLoopback_and_PushBackPacket_come_back_through_Receive()
    {
        var peer = Started(1);

        peer.SendLoopback([(byte)MessageId.UserPacketEnum, 5]);
        var looped = ReceiveUntil(peer, MessageId.UserPacketEnum);
        // RakPeer.cpp:165, 183: AllocPacket clears wasGeneratedLocally, and SendLoopback leaves it clear.
        Assert.False(looped.WasGeneratedLocally);
        Assert.Equal(peer.GetMyGUID(), looped.RakNetGuid);

        peer.PushBackPacket(looped, pushAtHead: true);
        Assert.Throws<ObjectDisposedException>(() => looped.Data);
        using var again = ReceiveUntil(peer, MessageId.UserPacketEnum);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 5], again.Data.ToArray());
    }

    [Fact]
    public void A_deallocated_packet_throws_and_deallocating_twice_is_harmless()
    {
        var peer = Started(1);
        peer.SendLoopback([(byte)MessageId.UserPacketEnum]);
        var packet = ReceiveUntil(peer, MessageId.UserPacketEnum);

        peer.DeallocatePacket(packet);
        peer.DeallocatePacket(packet);
        peer.DeallocatePacket(null);

        Assert.Throws<ObjectDisposedException>(() => packet.Data);
        Assert.Throws<ObjectDisposedException>(() => packet.Length);
        Assert.Throws<ObjectDisposedException>(() => packet.SystemAddress);
    }

    [Fact]
    public void Settings_made_before_Startup_apply_when_it_starts()
    {
        var plugin = new StartupPlugin();
        var peer = Instance();
        peer.SetMaximumIncomingConnections(3);
        peer.AttachPlugin(plugin);
        Assert.Equal(3, peer.GetMaximumIncomingConnections());

        Assert.Equal(StartupResult.RakNetStarted, peer.Startup(5, [Loopback()]));

        Assert.Equal(3, peer.GetMaximumIncomingConnections());
        Assert.Equal(5, peer.GetMaximumNumberOfPeers());
        Assert.True(plugin.StartedUp);
        peer.SetMaximumIncomingConnections(2);
        Assert.Equal(2, peer.Peer!.MaxIncomingConnections);
    }

    [Fact]
    public void Addresses_and_identity_queries_answer_as_RakNet()
    {
        var peer = Started(1);
        var bound = peer.GetMyBoundAddress();

        Assert.Equal(IPAddress.Loopback, bound.ToIPEndPoint().Address);
        Assert.NotEqual(0, bound.Port);
        Assert.Equal(peer.GetMyGUID(), peer.GetGuidFromSystemAddress(SystemAddress.Unassigned));
        Assert.Equal(peer.Peer!.InternalAddresses[0], peer.GetInternalID());
        // RakPeer.cpp:2580 returns defaultMTUSize, which RakPeer.cpp:206 sets to the smallest MTU tried, 576.
        Assert.Equal(576, peer.GetMTUSize(SystemAddress.Unassigned));
        Assert.Equal(-1, peer.GetIndexFromSystemAddress(new SystemAddress(IPAddress.Loopback, 9)));
        Assert.Equal(SystemAddress.Unassigned, peer.GetSystemAddressFromIndex(3));
        Assert.True(peer.IsLocalIP("127.0.0.1"));
        Assert.False(peer.IsLocalIP("not an address"));
        Assert.Equal(10_000u, peer.GetTimeoutTime(SystemAddress.Unassigned));
        Assert.Equal(-1, peer.GetLastPing(new RakNetGuid(5)));
        Assert.Equal(0u, peer.GetReceiveBufferSize());
        Assert.Equal((uint)peer.Peer.LocalAddresses.Count, peer.GetNumberOfAddresses());
    }

    [Fact]
    public void Per_connection_queries_answer_for_a_connection()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        var serverAddress = client.GetSystemAddressFromGuid(server.GetMyGUID());

        Assert.True(client.GetMTUSize(serverAddress) > 0);
        Assert.NotNull(client.GetStatistics(serverAddress));
        Assert.NotNull(client.GetStatistics(SystemAddress.Unassigned));
        Assert.Null(client.GetStatistics(new SystemAddress(IPAddress.Loopback, 9)));
        Assert.True(client.GetStatistics(0, out _));
        var addresses = new List<SystemAddress>();
        var guids = new List<RakNetGuid>();
        var statistics = new List<RakNetStatistics>();
        client.GetStatisticsList(addresses, guids, statistics);
        Assert.Equal([serverAddress], addresses);
        Assert.Equal([server.GetMyGUID()], guids);
        Assert.Single(statistics);
        addresses.Clear();
        guids.Clear();
        client.GetSystemList(addresses, guids);
        Assert.Equal([serverAddress], addresses);
        Assert.NotEqual(SystemAddress.Unassigned, client.GetExternalID(serverAddress));
    }

    [Fact]
    public void GetTimeoutTime_returns_the_default_even_for_a_connection_as_RakNet_does()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        var serverAddress = client.GetSystemAddressFromGuid(server.GetMyGUID());

        client.SetTimeoutTime(5_000, serverAddress);

        // RakPeer.cpp:2551-2564 reads the connection's timeout, discards it and returns defaultTimeoutTime.
        Assert.Equal(10_000u, client.GetTimeoutTime(serverAddress));
        Assert.True(client.Peer!.TryGetConnection(serverAddress, out var connection));
        Assert.Equal(TimeSpan.FromSeconds(5), connection.Timeout);
        client.SetTimeoutTime(7_000, SystemAddress.Unassigned);
        Assert.Equal(7_000u, client.GetTimeoutTime(serverAddress));
    }

    [Fact]
    public void IsLocalIP_compares_strings_as_RakNet_does()
    {
        var peer = Started(1);
        var stopped = Instance();

        // RakPeer.cpp:2636-2657: "127.0.0.1" and "localhost" by strcmp, then each GetLocalIP string.
        Assert.True(peer.IsLocalIP("localhost"));
        Assert.True(stopped.IsLocalIP("localhost"));
        Assert.True(stopped.IsLocalIP("127.0.0.1"));
        Assert.False(peer.IsLocalIP("127.1"));
        Assert.False(peer.IsLocalIP(null));
        Assert.False(peer.IsLocalIP(""));
        if (peer.GetNumberOfAddresses() > 0)
            Assert.True(peer.IsLocalIP(peer.GetLocalIP(0)));
    }

    [Fact]
    public void Pings_of_a_system_still_connecting_are_65535_as_in_RakNet()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        var serverAddress = new SystemAddress(IPAddress.Loopback, ServerPort(server));
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", serverAddress.Port));

        // Each peer runs only when stepped, so the client stops in IS_CONNECTING: it has a slot from
        // ID_OPEN_CONNECTION_REPLY_2 and the server has not answered its connection request.
        var clock = Stopwatch.StartNew();
        while (client.GetConnectionState(serverAddress) != ConnectionState.Connecting)
        {
            Assert.True(clock.Elapsed < Patience, "The client never reached IS_CONNECTING.");
            Assert.True(client.RunUpdateCycle());
            if (client.GetConnectionState(serverAddress) == ConnectionState.Connecting)
                break;
            Assert.True(server.RunUpdateCycle());
            Thread.Sleep(1);
        }

        // AssignSystemAddressToRemoteSystemList sets every ping to 65535 (RakPeer.cpp:3740-3748), and GetLastPing and
        // GetLowestPing return them raw (RakPeer.cpp:2178-2208); GetAveragePing finds no sample (RakPeer.cpp:2157-2169).
        Assert.Equal(65535, client.GetLastPing(serverAddress));
        Assert.Equal(65535, client.GetLowestPing(serverAddress));
        Assert.Equal(-1, client.GetAveragePing(serverAddress));
        Assert.Equal(-1, client.GetLastPing(new SystemAddress(IPAddress.Loopback, 9)));
    }

    [Fact]
    public void A_disconnected_system_keeps_its_slot_values_as_in_RakNet()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        var serverGuid = server.GetMyGUID();
        var serverAddress = client.GetSystemAddressFromGuid(serverGuid);
        var external = client.GetExternalID(serverAddress);
        Assert.NotEqual(SystemAddress.Unassigned, external);
        Assert.True(client.Peer!.TryGetConnection(serverGuid, out var connection));

        server.CloseConnection(client.GetMyGUID(), sendDisconnectionNotification: true);
        ReceiveUntil(client, MessageId.DisconnectionNotification, server).Dispose();
        Assert.Equal(ConnectionState.NotConnected, client.Peer.GetConnectionState(serverGuid));
        // The values the slot held when it went inactive.
        var lastPing = (int)connection.LastPing!.Value.TotalMilliseconds;
        var lowestPing = (int)connection.LowestPing!.Value.TotalMilliseconds;

        // The slot goes inactive but keeps its values until it is reassigned: GetExternalID falls back to an inactive
        // slot's myExternalSystemAddress (RakPeer.cpp:2351-2376), and the ping queries look up slots with onlyActive
        // false (RakPeer.cpp:2178-2208, 3381-3443). Closing the slot clears its guid (RakPeer.cpp:4159-4161), so a
        // GUID query finds nothing (RakPeer.cpp:3428-3440).
        Assert.Equal(external, client.GetExternalID(serverAddress));
        Assert.Equal(lastPing, client.GetLastPing(serverAddress));
        Assert.Equal(lowestPing, client.GetLowestPing(serverAddress));
        Assert.Equal(-1, client.GetLastPing(serverGuid));
        Assert.Equal(-1, client.GetLowestPing(serverGuid));
        Assert.Equal(-1, client.GetAveragePing(serverGuid));
    }

    [Fact]
    public void After_Shutdown_slot_queries_find_nothing_as_in_RakNet()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        var serverGuid = server.GetMyGUID();
        var serverAddress = client.GetSystemAddressFromGuid(serverGuid);
        Assert.NotEqual(SystemAddress.Unassigned, client.GetExternalID(serverAddress));

        client.Shutdown(100);

        // Shutdown sets maximumNumberOfPeers to 0 (RakPeer.cpp:1150-1167), so every slot loop is empty.
        Assert.Equal(SystemAddress.Unassigned, client.GetExternalID(serverAddress));
        Assert.Equal(-1, client.GetLastPing(serverAddress));
        Assert.Equal(-1, client.GetLowestPing(serverAddress));
        Assert.Equal(-1, client.GetAveragePing(serverAddress));
    }

    [Fact]
    public void ConnectWithSocket_connects_through_the_given_socket()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Instance();
        Assert.Equal(StartupResult.RakNetStarted, client.Startup(1, [Loopback(), Loopback()]));
        var sockets = new List<RakSocket>();
        client.GetSockets(sockets);
        var second = sockets[1];

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.ConnectWithSocket("127.0.0.1", ServerPort(server), default, second));
        ReceiveUntil(client, MessageId.ConnectionRequestAccepted, server).Dispose();
        using var incoming = ReceiveUntil(server, MessageId.NewIncomingConnection, client);

        Assert.Equal(second.LocalAddress.Port, incoming.SystemAddress.Port);
        Assert.Same(second, client.GetSocket(client.GetSystemAddressFromGuid(server.GetMyGUID())));
    }

    [Fact]
    public void ConnectWithSocket_reports_RakNets_invalid_parameter()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        var socket = client.GetSocket(SystemAddress.Unassigned)!;

        // RakPeer.cpp:1024-1025: no host, not started or no socket.
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, client.ConnectWithSocket(null!, ServerPort(server), default, socket));
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, client.ConnectWithSocket("127.0.0.1", ServerPort(server), default, null!));
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, Instance().ConnectWithSocket("127.0.0.1", ServerPort(server), default, socket));
        // Decision 11.14: another peer's socket is not borrowed.
        Assert.Equal(ConnectionAttemptResult.InvalidParameter, client.ConnectWithSocket("127.0.0.1", ServerPort(server), default, server.GetSocket(SystemAddress.Unassigned)!));
    }

    [Fact]
    public void GetSocket_and_GetSockets_answer_as_RakNet()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Instance();
        Assert.Null(client.GetSocket(SystemAddress.Unassigned));
        var sockets = new List<RakSocket> { null! };
        client.GetSockets(sockets);
        Assert.Empty(sockets);

        Assert.Equal(StartupResult.RakNetStarted, client.Startup(1, [Loopback(), Loopback()]));
        client.GetSockets(sockets);
        Assert.Equal(2, sockets.Count);
        Assert.Equal(client.GetMyBoundAddress(0), sockets[0].LocalAddress);
        Assert.Equal(client.GetMyBoundAddress(1), sockets[1].LocalAddress);
        // RakPeer.cpp:5658-5663: an undefined target answers with socketList, of which GetSocket returns the first.
        Assert.Same(sockets[0], client.GetSocket(SystemAddress.Unassigned));
        Assert.Null(client.GetSocket(new SystemAddress(IPAddress.Loopback, 9)));

        Connect(client, server);
        Assert.Same(sockets[0], client.GetSocket(client.GetSystemAddressFromGuid(server.GetMyGUID())));

        client.ReleaseSockets(sockets);
        Assert.Empty(sockets);
    }

    [Fact]
    public void AllocatePacket_builds_a_packet_for_PushBackPacket()
    {
        var peer = Started(1);
        var source = new SystemAddress(IPAddress.Parse("10.0.0.1"), 1234);

        var packet = peer.AllocatePacket(3);
        // RakPeer.cpp:159-169: length and bitSize from dataSize, guid unassigned, not generated locally.
        Assert.Equal(3, packet.Length);
        Assert.Equal(24, packet.BitSize);
        Assert.Equal(RakNetGuid.Unassigned, packet.RakNetGuid);
        Assert.False(packet.WasGeneratedLocally);
        packet.Data.Span[0] = (byte)MessageId.UserPacketEnum;
        packet.Data.Span[1] = 7;
        packet.Data.Span[2] = 8;
        packet.SystemAddress = source;
        packet.RakNetGuid = new RakNetGuid(77);

        peer.PushBackPacket(packet, pushAtHead: false);
        Assert.Throws<ObjectDisposedException>(() => packet.Data);

        using var received = ReceiveUntil(peer, MessageId.UserPacketEnum);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 7, 8], received.Data.ToArray());
        Assert.Equal(source, received.SystemAddress);
        Assert.Equal(new RakNetGuid(77), received.RakNetGuid);
    }

    [Fact]
    public void A_received_packet_pushed_back_carries_its_edits()
    {
        var peer = Started(1);
        var source = new SystemAddress(IPAddress.Parse("10.0.0.2"), 4321);
        peer.SendLoopback([(byte)MessageId.UserPacketEnum, 1]);
        var looped = ReceiveUntil(peer, MessageId.UserPacketEnum);

        looped.Data.Span[1] = 2;
        looped.SystemAddress = source;
        peer.PushBackPacket(looped, pushAtHead: true);

        using var again = ReceiveUntil(peer, MessageId.UserPacketEnum);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 2], again.Data.ToArray());
        Assert.Equal(source, again.SystemAddress);
    }

    [Fact]
    public void A_locally_generated_packet_pushed_back_with_a_new_address_stays_local()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        var source = new SystemAddress(IPAddress.Parse("10.0.0.3"), 999);
        client.CloseConnection(server.GetMyGUID(), sendDisconnectionNotification: false);
        var lost = ReceiveUntil(client, MessageId.ConnectionLost, server);
        Assert.True(lost.WasGeneratedLocally);

        lost.SystemAddress = source;
        client.PushBackPacket(lost, pushAtHead: true);

        // PushBackPacket queues the same Packet (RakPeer.cpp:2826-2843), so wasGeneratedLocally survives.
        using var again = ReceiveUntil(client, MessageId.ConnectionLost);
        Assert.Equal(source, again.SystemAddress);
        Assert.True(again.WasGeneratedLocally);
    }

    [Fact]
    public void A_received_packet_pushed_back_with_a_new_GUID_keeps_its_bit_size()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Connect(client, server);
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.UserPacketEnum);
        stream.WriteBits(0b101, 3);
        Assert.NotEqual(0u, client.Send(stream, Priority.High, Reliability.ReliableOrdered, 0, server.GetMyGUID(), broadcast: false));
        var message = ReceiveUntil(server, MessageId.UserPacketEnum, client);
        Assert.Equal(11, message.BitSize);

        message.RakNetGuid = new RakNetGuid(78);
        server.PushBackPacket(message, pushAtHead: true);

        using var again = ReceiveUntil(server, MessageId.UserPacketEnum);
        Assert.Equal(new RakNetGuid(78), again.RakNetGuid);
        Assert.Equal(11, again.BitSize);
    }

    [Fact]
    public void SetUserUpdateThread_runs_the_callback_every_update_cycle()
    {
        var peer = Instance();
        var calls = new List<(RakPeerInterface Peer, object? Data)>();
        var data = new object();
        peer.SetUserUpdateThread((p, d) => calls.Add((p, d)), data);
        Assert.False(peer.RunUpdateCycle());
        Assert.Equal(StartupResult.RakNetStarted, peer.Startup(1, [Loopback()]));

        Assert.True(peer.RunUpdateCycle());
        Assert.True(peer.RunUpdateCycle());
        Assert.Equal(2, calls.Count);
        Assert.All(calls, call => Assert.Same(peer, call.Peer));
        Assert.All(calls, call => Assert.Same(data, call.Data));

        peer.Receive();
        Assert.Equal(3, calls.Count);

        peer.SetUserUpdateThread(null, null);
        peer.RunUpdateCycle();
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void RunUpdateCycle_drives_the_peer_without_Receive()
    {
        var server = Started(4, maxIncoming: 4);
        var client = Started(1);
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", ServerPort(server)));

        var clock = Stopwatch.StartNew();
        while (server.NumberOfConnections() == 0)
        {
            Assert.True(clock.Elapsed < Patience, "The server never accepted the connection.");
            Assert.True(client.RunUpdateCycle());
            Assert.True(server.RunUpdateCycle());
            Thread.Sleep(1);
        }

        using var accepted = ReceiveUntil(client, MessageId.ConnectionRequestAccepted);
        Assert.Equal(server.GetMyGUID(), accepted.RakNetGuid);
    }

    [Fact]
    public void WriteOutOfBandHeader_writes_this_peers_GUID()
    {
        var peer = Started(1);
        using var stream = new BitStream();

        peer.WriteOutOfBandHeader(stream);

        var reader = stream.CreateReader();
        Assert.True(Protocol.Wire.Messages.OutOfBandHeader.TryRead(ref reader, out var header));
        Assert.Equal(peer.GetMyGUID(), header.Sender);
    }

    [Fact]
    public void Out_of_band_and_advertise_sends_report_whether_they_were_sent()
    {
        var peer = Started(1);
        var other = Started(1);

        Assert.True(peer.SendOutOfBand("127.0.0.1", ServerPort(other), [1, 2]));
        Assert.True(peer.AdvertiseSystem("127.0.0.1", ServerPort(other), [3]));
        Assert.False(peer.SendOutOfBand("no-such-host.invalid", 1, [1]));
        Assert.False(Instance().SendOutOfBand("127.0.0.1", 1, [1]));
    }

    [Fact]
    public void The_network_simulator_is_reachable_by_its_RakNet_name()
    {
        var peer = Started(1);

        peer.ApplyNetworkSimulator(0.5f, 10, 5);
        Assert.True(peer.IsNetworkSimulatorActive());
        peer.ApplyNetworkSimulator(0f, 0, 0);
        Assert.False(peer.IsNetworkSimulatorActive());
    }

    [Fact]
    public void Get64BitUniqueRandomNumber_varies()
    {
        Assert.NotEqual(RakPeerInterface.Get64BitUniqueRandomNumber(), RakPeerInterface.Get64BitUniqueRandomNumber());
    }

    [Fact]
    public void DestroyInstance_shuts_the_peer_down()
    {
        var peer = RakPeerInterface.GetInstance();
        Assert.Equal(StartupResult.RakNetStarted, peer.Startup(1, [Loopback()]));

        RakPeerInterface.DestroyInstance(peer);

        Assert.False(peer.IsActive());
        Assert.Null(peer.Receive());
    }

    public void Dispose()
    {
        foreach (var instance in _instances)
            instance.Dispose();
    }

    private RakPeerInterface Instance()
    {
        var instance = RakPeerInterface.GetInstance();
        _instances.Add(instance);
        return instance;
    }

    private RakPeerInterface Started(int maxConnections, ushort maxIncoming = 0)
    {
        var instance = Instance();
        Assert.Equal(StartupResult.RakNetStarted, instance.Startup(maxConnections, [Loopback()]));
        instance.SetMaximumIncomingConnections(maxIncoming);
        return instance;
    }

    private static SocketDescriptor Loopback() => new(0, "127.0.0.1");

    private static ushort ServerPort(RakPeerInterface server) => server.GetMyBoundAddress().Port;

    private static void Connect(RakPeerInterface client, RakPeerInterface server)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, client.Connect("127.0.0.1", ServerPort(server)));
        ReceiveUntil(client, MessageId.ConnectionRequestAccepted, server).Dispose();
        ReceiveUntil(server, MessageId.NewIncomingConnection, client).Dispose();
    }

    /// <summary>Calls Receive on <paramref name="peer"/>, keeping <paramref name="others"/> running, until a packet with <paramref name="id"/>.</summary>
    private static Packet ReceiveUntil(RakPeerInterface peer, MessageId id, params RakPeerInterface[] others)
    {
        var clock = Stopwatch.StartNew();
        var seen = new List<MessageId>();
        while (clock.Elapsed < Patience)
        {
            foreach (var other in others)
                Drain(other);
            var packet = peer.Receive();
            if (packet is null)
            {
                Thread.Sleep(1);
                continue;
            }
            if (packet.Data.Span[0] == (byte)id)
                return packet;
            seen.Add((MessageId)packet.Data.Span[0]);
            peer.DeallocatePacket(packet);
        }
        Assert.Fail($"No {id} within {Patience}; saw [{string.Join(", ", seen)}].");
        return null;
    }

    /// <summary>Receives on <paramref name="peer"/> for a while, keeping <paramref name="others"/> running.</summary>
    private static List<Packet> Drain(RakPeerInterface peer, params RakPeerInterface[] others)
    {
        var packets = new List<Packet>();
        var clock = Stopwatch.StartNew();
        do
        {
            foreach (var other in others)
                Drain(other);
            while (peer.Receive() is { } packet)
                packets.Add(packet);
            Thread.Sleep(1);
        }
        while (others.Length > 0 && clock.Elapsed < TimeSpan.FromMilliseconds(200));
        return packets;
    }

    private static void Drain(RakPeerInterface peer)
    {
        while (peer.Receive() is { } packet)
            peer.DeallocatePacket(packet);
    }

    private static ReadOnlySequence<byte> Sequence(byte[] first, byte[] second)
    {
        var head = new Segment(first);
        var tail = head.Append(second);
        return new ReadOnlySequence<byte>(head, 0, tail, second.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(byte[] memory) => Memory = memory;

        public Segment Append(byte[] memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }

    private sealed class StartupPlugin : IRakPlugin
    {
        public bool StartedUp { get; private set; }

        public void OnStartup() => StartedUp = true;
    }
}
