using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Messages between unconnected peers, from Samples/OfflineMessagesTest/OfflineMessagesTest.cpp and
/// Samples/Tests/OfflineMessagesConvertTest.cpp (AdvertiseSystem, then a ping answered with the offline ping response),
/// Samples/Ping/Ping.cpp (a server describing itself in its offline ping response) and
/// Samples/LANServerDiscovery/LANServerDiscovery.cpp (the same ping sent to 255.255.255.255). Datagrams go through the
/// proxy, except in the broadcast discovery scenario, which pings 255.255.255.255 directly.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class OfflineMessageScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_discovery_ping_is_answered_with_the_servers_description_and_reported_to_the_server(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // Ping.cpp:44-47 sets the server's description, with its terminating zero, as the offline ping response.
        var description = "Offline ping response data. This can be anything you want.\0"u8.ToArray();
        await session.Server.SetOfflinePingResponseAsync(description, Token);

        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: false, Token));

        var pong = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, Token);
        AssertPong(pong, session.ServerStarted.PeerGuid, session.ProxyEndPoint, description);
        // RakPeer.cpp:4670-4677: the server's application sees the ping, from the pinging peer's GUID.
        var ping = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPing, "ID_UNCONNECTED_PING", Timeout, Token);
        Assert.Equal(session.ClientStarted.PeerGuid, ping.PeerGuid);
        Assert.Equal(session.AddressAtServer(session.ClientPeer), ping.Address);
        Assert.Equal(new byte[] { (byte)MessageId.UnconnectedPing }, ping.Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_discovery_ping_to_the_broadcast_address_is_answered_by_the_server_on_that_port(PeerKind client, PeerKind server)
    {
        // LANServerDiscovery.cpp pings "255.255.255.255" on the server's port. Both peers bind every interface, and
        // RakPeer::Startup sets SO_BROADCAST on each socket (RakPeer.cpp:505, RakNetSocket2_Berkley.cpp:176).
        PeerMatrix.SkipUnlessAvailable(client, server);
        SkipUnlessBroadcastAvailable();
        var anyInterface = new StartupOptions(Host: "0.0.0.0");
        await using var serverPeer = await InteropPeer.StartAsync(server, "server", anyInterface, Token);
        await using var clientPeer = await InteropPeer.StartAsync(client, "client", anyInterface, Token);
        var description = "Offline ping response data. This can be anything you want.\0"u8.ToArray();
        await serverPeer.Driver.SetOfflinePingResponseAsync(description, Token);
        var serverPort = serverPeer.Started.BoundAddress.Port;

        Assert.True(await clientPeer.Driver.PingAsync(new IPEndPoint(IPAddress.Broadcast, serverPort), onlyIfAccepting: false, Token));

        var pong = await clientPeer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, Token);
        AssertPong(pong, serverPeer.Started.PeerGuid, new IPEndPoint(pong.Address.Address, serverPort), description);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_pinged_peer_can_ping_back_at_the_address_it_saw(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // OfflineMessagesConvertTest.cpp:72-73 and :114-131: the peer that was contacted pings back and checks the
        // response, "Offline Ping Data" with its terminating zero.
        var response = "Offline Ping Data\0"u8.ToArray();
        await session.Client.SetOfflinePingResponseAsync(response, Token);
        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: false, Token));
        var ping = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPing, "ID_UNCONNECTED_PING", Timeout, Token);

        Assert.True(await session.Server.PingAsync(ping.Address, onlyIfAccepting: false, Token));

        var pong = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, Token);
        AssertPong(pong, session.ClientStarted.PeerGuid, ping.Address, response);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_ping_for_open_connections_is_answered_while_the_server_accepts_connections(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: true, Token));

        var pong = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, Token);
        AssertPong(pong, session.ServerStarted.PeerGuid, session.ProxyEndPoint, []);
        var ping = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.UnconnectedPingOpenConnections, "ID_UNCONNECTED_PING_OPEN_CONNECTIONS", Timeout, Token);
        Assert.Equal(session.ClientStarted.PeerGuid, ping.PeerGuid);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_ping_for_open_connections_is_ignored_by_a_server_that_accepts_none(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, serverOptions: new StartupOptions(MaxIncoming: 0), cancellationToken: Token);

        // RakPeer.cpp:4637-4639: ID_UNCONNECTED_PING_OPEN_CONNECTIONS is answered only if AllowIncomingConnections.
        // A plain ping sent after it is always answered; datagrams on loopback through the proxy keep their order, so
        // once its pong is in, any pong for the first ping would be too.
        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: true, Token));
        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: false, Token));

        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UnconnectedPing, "ID_UNCONNECTED_PING", Timeout, Token);
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId == (byte)MessageId.UnconnectedPong);
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.UnconnectedPingOpenConnections);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Advertise_system_delivers_its_data_to_an_unconnected_peer(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await OfflineFlows.AdvertiseSystemAsync(session, Token);
    }

    // Sending to 255.255.255.255 needs an interface with a broadcast route.
    private static void SkipUnlessBroadcastAvailable()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        bool sent;
        try
        {
            sent = socket.SendTo([0], new IPEndPoint(IPAddress.Broadcast, ((IPEndPoint)socket.LocalEndPoint!).Port)) == 1;
        }
        catch (SocketException)
        {
            sent = false;
        }
        Assert.SkipUnless(sent, "This host has no broadcast route.");
    }

    // RakPeer.cpp:4682-4700: the application sees the ID, the ping's send time cut to a 4-byte TimeMS, then the response.
    // The time is the pinging peer's own clock, so it is at most the time the pong arrived.
    private static void AssertPong(ReceivedPacket pong, ulong guid, System.Net.IPEndPoint address, byte[] response)
    {
        Assert.Equal(guid, pong.PeerGuid);
        Assert.Equal(address, pong.Address);
        Assert.Equal(1 + sizeof(uint) + response.Length, pong.Data.Length);
        Assert.Equal(Convert.ToHexStringLower(response), Convert.ToHexStringLower(pong.Data.Span[5..]));
        var sentMs = BinaryPrimitives.ReadUInt32BigEndian(pong.Data.Span[1..]);
        Assert.InRange((uint)pong.TimeMs - sentMs, 0u, 1000u);
    }
}

/// <summary>The steps and assertions of the offline message scenarios, shared with the golden vector capture.</summary>
public static class OfflineFlows
{
    /// <summary>
    /// OfflineMessagesConvertTest.cpp:70 and :86-101: the client advertises "hello world" with its terminating zero, and
    /// the server's application sees ID_ADVERTISE_SYSTEM and the data, from the client's GUID.
    /// </summary>
    public static async Task AdvertiseSystemAsync(InteropSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var data = "hello world\0"u8.ToArray();

        Assert.True(await session.Client.AdvertiseSystemAsync(session.ProxyEndPoint, data, cancellationToken));

        var advertised = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.AdvertiseSystem, "ID_ADVERTISE_SYSTEM", InteropSession.DefaultTimeout, cancellationToken);
        Assert.Equal([(byte)MessageId.AdvertiseSystem, .. data], advertised.Data.ToArray());
        Assert.Equal(session.ClientStarted.PeerGuid, advertised.PeerGuid);
        Assert.Equal(session.AddressAtServer(session.ClientPeer), advertised.Address);
    }
}
