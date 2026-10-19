using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// The ways a server turns a connection attempt down, and how the client reports each (RakPeer.cpp:4540-4580,
// 4737-4870, 4871-5126, 5198-5418, 3595-3625).
public class ConnectionRejectionTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint SecondClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_002);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);
    private static readonly byte[] ServerKey = Enumerable.Range(0, SecureOpenConnectionReply1.PublicKeyLength).Select(i => (byte)i).ToArray();

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public ConnectionRejectionTests()
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        _server = _link.Attach(
            ServerAddress,
            new PeerCore(new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8, LocalAddresses = LocalAddressList.Build([ServerAddress.Address], 19_132) })
        );
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    // Banning.

    [Fact]
    public void A_banned_client_is_told_it_is_banned()
    {
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);

        var failed = ConnectAndFail();

        Assert.Equal(MessageId.ConnectionBanned, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Equal(ServerAddress, failed.Remote);
        // RakPeer.cpp:5119-5124: the packet is the rejection's ID alone.
        Assert.Equal([(byte)MessageId.ConnectionBanned], failed.Packet.ToArray());
        Assert.Equal(ConnectionState.IsNotConnected, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void Every_datagram_from_a_banned_address_is_answered_with_connection_banned_and_dropped()
    {
        // RakPeer.cpp:4550-4580: the ban check comes before anything else, so even an unconnected ping is refused.
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);

        _server.Receive(ClientAddress, Encode(new UnconnectedPing(false, 1, ClientGuid)), _link.NowUs);

        var reply = Assert.Single(Datagrams(_server));
        Assert.Equal(ClientAddress, reply.Remote);
        Assert.Equal(ServerGuid, Decode<ConnectionBanned>(reply.Data).ServerGuid);
        Assert.Empty(Events(_server));
    }

    [Fact]
    public void A_connected_system_that_is_banned_has_its_messages_refused()
    {
        Connect();
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);

        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(50));

        Assert.DoesNotContain(_link.EventsOf(_server), e => e is MessageReceived);
        Assert.Contains(_link.Delivered, d => d.From.Equals(ServerAddress) && d.Data[0] == (byte)MessageId.ConnectionBanned);
    }

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.0.0.*", true)]
    [InlineData("10.*", true)]
    [InlineData("10.0.0.2", false)]
    [InlineData("10.0.0.10", false)]
    [InlineData("10.0.0.1*", false)]
    [InlineData("11.*", false)]
    public void Ban_patterns_match_as_RakPeer_compares_them(string pattern, bool banned)
    {
        // IsBanned, RakPeer.cpp:1981-2049: characters compare until either string ends; a '*' where they differ bans.
        _server.AddToBanList(pattern, 0, _link.NowUs);

        Assert.Equal(banned, _server.IsBanned("10.0.0.1", _link.NowUs));
    }

    [Fact]
    public void A_temporary_ban_ends_once_its_time_has_passed()
    {
        // RakPeer.cpp:2001: a ban expires when its timeout is earlier than now.
        _server.AddToBanList("10.0.0.1", 1_000, _link.NowUs);

        Assert.True(_server.IsBanned("10.0.0.1", _link.NowUs + 1_000_000));
        Assert.False(_server.IsBanned("10.0.0.1", _link.NowUs + 1_001_000));
    }

    [Fact]
    public void Banning_an_address_again_replaces_its_duration()
    {
        // RakPeer.cpp:1874-1885.
        _server.AddToBanList("10.0.0.1", 1_000, _link.NowUs);
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);

        Assert.True(_server.IsBanned("10.0.0.1", _link.NowUs + 60_000_000));
    }

    [Fact]
    public void Removing_or_clearing_bans_lets_the_address_connect()
    {
        _server.AddToBanList("10.0.0.1", 0, _link.NowUs);
        _server.AddToBanList("10.0.0.*", 0, _link.NowUs);

        _server.RemoveFromBanList("10.0.0.1");
        Assert.True(_server.IsBanned("10.0.0.1", _link.NowUs));
        _server.ClearBanList();
        Assert.False(_server.IsBanned("10.0.0.1", _link.NowUs));

        Connect();
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234:5678:9abc::1")]
    public void Empty_addresses_and_ones_longer_than_15_characters_are_never_banned(string address)
    {
        // RakPeer.cpp:1866-1867 and 1988-1989.
        _server.AddToBanList(address, 0, _link.NowUs);
        _server.AddToBanList("*", 0, _link.NowUs);

        Assert.False(_server.IsBanned(address, _link.NowUs));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    public void An_ipv6_address_of_15_characters_or_fewer_can_be_banned(string address)
    {
        // RakPeer.cpp:1866 and 1985 check only the length.
        _server.AddToBanList(address, 0, _link.NowUs);

        Assert.True(_server.IsBanned(address, _link.NowUs));
    }

    [Fact]
    public void An_unverified_sender_that_sends_anything_but_a_connection_request_is_banned_for_the_timeout()
    {
        // RakPeer.cpp:5955-5976: closed, then banned for the reliability layer's timeout.
        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid)), _link.NowUs);
        var layer = new RakDotNet.Protocol.Session.ReliabilityLayer(1492, _link.NowUs);
        layer.Send([(byte)MessageId.UserPacketEnum], Reliability.Reliable, Priority.High, 0, 0, _link.NowUs);
        layer.Update(_link.NowUs + 1_000);
        Assert.True(layer.TryDequeueDatagram(out var datagram));

        _server.Receive(ClientAddress, datagram, _link.NowUs + 2_000);
        _server.Update(_link.NowUs + 3_000);

        Assert.True(_server.IsBanned("10.0.0.1", _link.NowUs + 3_000 + 9_000_000));
        Assert.False(_server.IsBanned("10.0.0.1", _link.NowUs + 3_000 + 10_001_000));
    }

    // Connection frequency.

    [Fact]
    public void With_the_frequency_limit_a_second_connection_from_the_same_ip_within_100_ms_is_refused()
    {
        _server.LimitIpConnectionFrequency = true;
        var second = _link.Attach(SecondClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid + 1 }));
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.Step();
        second.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _link.RunUntil(() => _link.EventsOf(second).Count > 0 && ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(_link.EventsOf(second)));
        Assert.Equal(MessageId.IpRecentlyConnected, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Equal([(byte)MessageId.IpRecentlyConnected], failed.Packet.ToArray());
        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(ClientEvents));
    }

    [Fact]
    public void With_the_frequency_limit_a_second_connection_from_the_same_ip_after_100_ms_is_accepted()
    {
        _server.LimitIpConnectionFrequency = true;
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(100));
        var second = _link.Attach(SecondClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid + 1 }));

        second.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(second).Count > 0, TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(second)));
    }

    [Theory]
    [InlineData(99, MessageId.IpRecentlyConnected)]
    [InlineData(100, MessageId.OpenConnectionReply2)]
    public void The_frequency_limit_refuses_a_second_connection_only_within_100_ms(int afterMs, MessageId answer)
    {
        // RakPeer.cpp:3612: refused while timeMS - connectionTime < 100.
        _server.LimitIpConnectionFrequency = true;
        _server.Receive(ClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid)), _link.NowUs);
        Assert.Equal((byte)MessageId.OpenConnectionReply2, Assert.Single(Datagrams(_server)).Data[0]);

        _server.Receive(SecondClientAddress, Encode(new OpenConnectionRequest2(ServerAddress, 1492, ClientGuid + 1)), _link.NowUs + afterMs * 1000L);

        Assert.Equal((byte)answer, Assert.Single(Datagrams(_server)).Data[0]);
    }

    [Fact]
    public void The_frequency_limit_does_not_apply_to_the_address_the_first_remote_system_saw()
    {
        // RakPeer.cpp:4024-4025: IsLoopbackAddress(systemAddress, false) also matches firstExternalID, which the hub's
        // own first connection records (RakPeer.cpp:6035-6042).
        var hubAddress = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 5);
        var hub = _link.Attach(hubAddress, new PeerCore(new PeerOptions { Guid = 5, MaxIncomingConnections = 8, LimitIpConnectionFrequency = true }));
        hub.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(hub).OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(1));
        var first = _link.Attach(new IPEndPoint(hubAddress.Address, 6), new PeerCore(new PeerOptions { Guid = 6 }));
        var second = _link.Attach(new IPEndPoint(hubAddress.Address, 7), new PeerCore(new PeerOptions { Guid = 7 }));

        first.Connect(hubAddress, default, 12, 500, 0, _link.NowUs);
        _link.Step();
        second.Connect(hubAddress, default, 12, 500, 0, _link.NowUs);
        _link.RunUntil(() => _link.EventsOf(first).Count > 0 && _link.EventsOf(second).Count > 0, TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(first)));
        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(second)));
    }

    [Fact]
    public void Without_the_frequency_limit_connections_from_the_same_ip_are_accepted_together()
    {
        // limitConnectionFrequencyFromTheSameIP is false by default (RakPeer.cpp:312).
        var second = _link.Attach(SecondClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid + 1 }));
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _link.Step();
        second.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _link.RunUntil(() => _link.EventsOf(second).Count > 0 && ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(second)));
    }

    [Fact]
    public void The_frequency_limit_does_not_apply_to_the_hosts_own_addresses()
    {
        // RakPeer.cpp:3606: IsLoopbackAddress(systemAddress, false) compares against ipList without ports.
        var local = new PeerCore(
            new PeerOptions { Guid = 7, MaxIncomingConnections = 8, LimitIpConnectionFrequency = true, LocalAddresses = LocalAddressList.Build([ClientAddress.Address], 1) }
        );
        var localAddress = new IPEndPoint(IPAddress.Parse("10.0.0.7"), 7);
        _link.Attach(localAddress, local);
        var second = _link.Attach(SecondClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid + 1 }));
        _client.Connect(localAddress, default, 12, 500, 0, _link.NowUs);
        _link.Step();
        second.Connect(localAddress, default, 12, 500, 0, _link.NowUs);

        _link.RunUntil(() => _link.EventsOf(second).Count > 0 && ClientEvents.Count > 0, TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(_link.EventsOf(second)));
    }

    [Fact]
    public void A_client_with_the_frequency_limit_silently_drops_a_second_attempt_to_the_same_ip_within_100_ms()
    {
        // RakPeer.cpp:4941-4954 and 5054-5066: when assigning the slot fails the limit, the request is removed and
        // nothing is reported.
        var otherServerAddress = new IPEndPoint(ServerAddress.Address, 19_133);
        _link.Attach(otherServerAddress, new PeerCore(new PeerOptions { Guid = ServerGuid + 1, MaxIncomingConnections = 8 }));
        _client.LimitIpConnectionFrequency = true;
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);
        _client.Connect(otherServerAddress, default, 12, 500, 0, _link.NowUs);

        _link.Run(TimeSpan.FromSeconds(1));

        Assert.IsType<OutgoingConnectionCompleted>(Assert.Single(ClientEvents));
        Assert.Equal(ConnectionState.IsNotConnected, _client.GetConnectionState(otherServerAddress));
    }

    // Security, which the offline handshake reports before any cryptography runs.

    [Fact]
    public void A_client_expecting_security_from_a_server_without_it_reports_our_system_requires_security()
    {
        // RakPeer.cpp:4824-4838: the request stays queued, so each reply reports it again until the attempts run out.
        _client.Connect(ServerAddress, default, 3, 100, 0, _link.NowUs, new ClientSecurity { ServerPublicKey = ServerKey });

        _link.RunUntil(() => ClientEvents.Any(e => e.Id == MessageId.ConnectionAttemptFailed), TimeSpan.FromSeconds(2));

        var reports = ClientEvents.Take(ClientEvents.Count - 1).Select(e => Assert.IsType<ConnectionAttemptFailed>(e)).ToList();
        Assert.Equal(4, reports.Count);
        Assert.All(reports, r => Assert.Equal(MessageId.OurSystemRequiresSecurity, r.Reason));
        Assert.All(reports, r => Assert.Equal(ServerGuid, r.Guid));
        Assert.All(reports, r => Assert.Equal([(byte)MessageId.OurSystemRequiresSecurity], r.Packet.ToArray()));
        Assert.DoesNotContain(_link.Sent, d => d.Data[0] == (byte)MessageId.OpenConnectionRequest2);
    }

    [Fact]
    public void A_client_accepting_any_key_also_requires_security()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, ClientSecurity.AcceptAnyPublicKey);
        _client.Receive(ServerAddress, Encode(new OpenConnectionReply1(ServerGuid, 1492)), _link.NowUs);

        Assert.Equal(MessageId.OurSystemRequiresSecurity, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(Events(_client))).Reason);
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void A_client_expecting_another_server_key_reports_a_public_key_mismatch()
    {
        // RakPeer.cpp:4788-4801.
        var otherKey = ServerKey.Select(b => (byte)~b).ToArray();
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, new ClientSecurity { ServerPublicKey = otherKey });

        _client.Receive(ServerAddress, Encode(new SecureOpenConnectionReply1(ServerGuid, 42, ServerKey, 1492)), _link.NowUs);

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(Events(_client)));
        Assert.Equal(MessageId.PublicKeyMismatch, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Equal([(byte)MessageId.PublicKeyMismatch], failed.Packet.ToArray());
        Assert.Empty(Datagrams(_client));
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void An_insecure_client_answered_by_a_secure_server_reports_a_public_key_mismatch()
    {
        // RakPeer.cpp:4788-4801: with PKM_INSECURE_CONNECTION remote_public_key is never set (RakPeer.cpp:3197-3205),
        // so the server's key never matches it.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, Encode(new SecureOpenConnectionReply1(ServerGuid, 42, ServerKey, 1492)), _link.NowUs);

        Assert.Equal(MessageId.PublicKeyMismatch, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(Events(_client))).Reason);
    }

    [Fact]
    public void A_secure_reply_carrying_the_expected_key_is_not_a_rejection()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, new ClientSecurity { ServerPublicKey = ServerKey });

        _client.Receive(ServerAddress, Encode(new SecureOpenConnectionReply1(ServerGuid, 42, ServerKey, 1492)), _link.NowUs);

        Assert.Empty(Events(_client));
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
    }

    [Fact]
    public void An_insecure_client_told_security_is_required_reports_that_the_remote_system_requires_a_public_key()
    {
        // RakPeer.cpp:4914-4930: the packet is the ID and a 0 byte (server public key missing); the request stays queued.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, RequireSecurity(Encode(new OpenConnectionReply2(ServerGuid, ClientAddress, 1492))), _link.NowUs);

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(Events(_client)));
        Assert.Equal(MessageId.RemoteSystemRequiresPublicKey, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Equal([(byte)MessageId.RemoteSystemRequiresPublicKey, 0], failed.Packet.ToArray());
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_secure_client_ignores_an_insecure_open_connection_reply_2(bool acceptAnyKey)
    {
        // RakPeer.cpp:4966-4992: a request with a client handshake always checks the server's answer, which an insecure
        // reply lacks, so it returns before ID_CONNECTION_REQUEST and the request stays queued until it runs out.
        _link.Drop = (from, _, _) => from.Equals(ClientAddress);
        var security = acceptAnyKey ? ClientSecurity.AcceptAnyPublicKey : new ClientSecurity { ServerPublicKey = ServerKey };
        _client.Connect(ServerAddress, default, 3, 100, 0, _link.NowUs, security);
        _link.Step();
        Assert.Equal((byte)MessageId.OpenConnectionRequest1, Assert.Single(Datagrams(_client)).Data[0]);
        var sentBefore = _link.Sent.Count;

        _client.Receive(ServerAddress, Encode(new OpenConnectionReply2(ServerGuid, ClientAddress, 1492)), _link.NowUs);

        Assert.Empty(Datagrams(_client));
        Assert.Empty(Events(_client));
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
        _link.RunUntil(() => ClientEvents.Any(), TimeSpan.FromSeconds(2));
        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
        Assert.Equal(MessageId.ConnectionAttemptFailed, failed.Reason);
        Assert.All(_link.Sent.Skip(sentBefore), d => Assert.Equal((byte)MessageId.OpenConnectionRequest1, d.Data[0]));
    }

    [Fact]
    public void A_public_key_of_the_wrong_length_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, new ClientSecurity { ServerPublicKey = new byte[10] }));
    }

    // The rejections that already had tests in PeerCoreTests carry the server GUID and the ID alone too.

    [Fact]
    public void A_full_server_rejects_with_no_free_incoming_connections()
    {
        _server.MaxIncomingConnections = 0;

        var failed = ConnectAndFail();

        Assert.Equal(MessageId.NoFreeIncomingConnections, failed.Reason);
        Assert.Equal([(byte)MessageId.NoFreeIncomingConnections], failed.Packet.ToArray());
    }

    [Fact]
    public void A_server_with_a_different_protocol_rejects_with_incompatible_protocol_version_and_the_client_reports_the_id_alone()
    {
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs);

        _client.Receive(ServerAddress, Encode(new IncompatibleProtocolVersion(7, ServerGuid)), _link.NowUs);

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(Events(_client)));
        Assert.Equal([(byte)MessageId.IncompatibleProtocolVersion], failed.Packet.ToArray());
        Assert.Equal(ServerGuid, failed.Guid);
    }

    private static byte[] RequireSecurity(byte[] reply2)
    {
        // The flag is the top bit of the last byte (OpenConnectionReply2's remarks); the answer would follow.
        var bytes = reply2.ToArray();
        bytes[^1] |= 0x80;
        return bytes;
    }

    private ConnectionAttemptFailed ConnectAndFail()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(1));
        return Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
    }

    private void Connect()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => ClientEvents.OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(1));
    }
}
