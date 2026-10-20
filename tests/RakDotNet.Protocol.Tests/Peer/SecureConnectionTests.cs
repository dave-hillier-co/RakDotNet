using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Security;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using static RakDotNet.Protocol.Tests.Peer.PeerWire;

namespace RakDotNet.Protocol.Tests.Peer;

// The LIBCAT_SECURITY paths of RakPeer.cpp between two cores: InitializeSecurity on the server, a PublicKey on Connect,
// the cookie and key in ID_OPEN_CONNECTION_REPLY_1, the challenge and answer in the second offline exchange, the proof
// and identity in ID_CONNECTION_REQUEST, and AuthenticatedEncryption on every connected datagram.
public sealed class SecureConnectionTests : IDisposable
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);
    private static readonly TestKeyPair ServerKeys = TestKeyPair.Generate();

    private readonly InMemoryLink _link = new();
    private readonly PeerCore _client;
    private readonly PeerCore _server;

    public SecureConnectionTests() : this(requireClientKey: false)
    {
    }

    private SecureConnectionTests(bool requireClientKey, string? exception = null)
    {
        _client = _link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid, LocalAddresses = LocalAddressList.Build([ClientAddress.Address], 50_001) }));
        _server = _link.Attach(ServerAddress, NewServer(requireClientKey));
        if (exception is not null)
            _server.SecurityExceptions.Add(exception);
    }

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    private IReadOnlyList<PeerEvent> ServerEvents => _link.EventsOf(_server);

    private static PeerCore NewServer(bool requireClientKey) =>
        new(new PeerOptions
        {
            Guid = ServerGuid,
            MaxIncomingConnections = 8,
            LocalAddresses = LocalAddressList.Build([ServerAddress.Address], 19_132),
            Security = ServerKeys.Server(requireClientKey),
        });

    [Fact]
    public void A_client_that_knows_the_servers_key_connects_and_every_connected_datagram_is_encrypted()
    {
        ConnectAndWait(ServerKeys.Known());
        var secret = "the quick brown fox"u8.ToArray();
        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, .. secret], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _server.Send(ClientAddress, [(byte)MessageId.UserPacketEnum, .. secret], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);

        _link.RunUntil(() => ClientEvents.OfType<MessageReceived>().Any() && ServerEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(1));

        Assert.Equal(secret, ServerEvents.OfType<MessageReceived>().Single().Data[1..].ToArray());
        Assert.Equal(secret, ClientEvents.OfType<MessageReceived>().Single().Data[1..].ToArray());
        Assert.DoesNotContain(_link.Sent, d => d.Data.AsSpan().IndexOf(secret) >= 0);
        // Nothing after the offline handshake parses as a plain reliability-layer datagram carrying the request.
        Assert.DoesNotContain(ConnectedMessages(_link.Sent.Select(d => d.Data)), m => m.Payload.Span[0] == (byte)MessageId.ConnectionRequest);
        Assert.True(Slot(_server, ClientAddress).Session.UsesSecurity);
        Assert.True(Slot(_client, ServerAddress).Session.UsesSecurity);
    }

    [Fact]
    public void The_offline_handshake_carries_the_cookie_key_challenge_and_answer()
    {
        ConnectAndWait(ServerKeys.Known());

        var reply1 = Decode<SecureOpenConnectionReply1>(Sent(MessageId.OpenConnectionReply1).First());
        Assert.Equal(ServerKeys.Public, reply1.ServerPublicKey.ToArray());
        var request2 = Decode<SecureOpenConnectionRequest2>(Sent(MessageId.OpenConnectionRequest2).First());
        Assert.Equal(reply1.Cookie, request2.Cookie);
        Assert.Equal(EasyHandshake.ChallengeBytes, request2.Challenge.Length);
        var reply2 = Decode<OpenConnectionReply2>(Sent(MessageId.OpenConnectionReply2).First());
        Assert.True(reply2.RequiresSecurity);
        Assert.Equal(EasyHandshake.AnswerBytes, reply2.Answer.Length);
        Assert.Equal(reply2.Answer.ToArray(), Slot(_server, ClientAddress).Answer);
    }

    [Fact]
    public void A_client_accepting_any_key_connects()
    {
        ConnectAndWait(TestKeyPair.AcceptAny());

        Assert.True(Slot(_client, ServerAddress).Session.UsesSecurity);
    }

    [Fact]
    public void The_mtu_is_reported_whole_and_the_reliability_layer_takes_the_overhead_off()
    {
        // GetMTUSize returns MTUSize (RakPeer.cpp:2570-2581); only Reset takes OVERHEAD_BYTES off (ReliabilityLayer.cpp:353-358).
        ConnectAndWait(ServerKeys.Known());

        Assert.Equal(1492, _server.GetMtuSize(ClientAddress));
        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, .. new byte[4_999]], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(2));
        Assert.Equal(5_000, ServerEvents.OfType<MessageReceived>().Single().Data.Length);
        Assert.All(_link.Sent, d => Assert.True(d.Data.Length <= 1492 - 28, $"{d.Data.Length} bytes"));
    }

    [Fact]
    public void A_wrong_password_is_refused_over_the_secure_connection()
    {
        _server.IncomingPassword = "secret"u8.ToArray();
        _client.Connect(ServerAddress, "guess"u8, 12, 500, 0, _link.NowUs, ServerKeys.Known());

        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(2));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents));
        Assert.Equal(MessageId.InvalidPassword, failed.Reason);
    }

    [Fact]
    public void A_tampered_connected_datagram_is_dropped_and_the_connection_carries_on()
    {
        ConnectAndWait(ServerKeys.Known());
        var tampered = 0;
        _link.Drop = (from, _, data) =>
        {
            if (!from.Equals(ClientAddress) || tampered > 0)
                return false;
            data[data.Length / 2] ^= 0x40;
            tampered++;
            return false;
        };

        _client.Send(ServerAddress, [(byte)MessageId.UserPacketEnum, 1], Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Any(), TimeSpan.FromSeconds(5));

        Assert.Equal(1, tampered);
        Assert.Equal(ConnectionState.IsConnected, _server.GetConnectionState(ClientAddress));
    }

    [Fact]
    public void Two_way_authentication_gives_the_server_the_clients_identity_key()
    {
        using var test = new SecureConnectionTests(requireClientKey: true);
        var identity = TestKeyPair.Generate();

        test.ConnectAndWait(ServerKeys.TwoWay(identity));

        Assert.Equal(identity.Public, Slot(test._server, ClientAddress).ClientPublicKey);
    }

    [Fact]
    public void A_server_not_requiring_client_keys_ignores_an_identity()
    {
        // RakPeer.cpp:3484-3501: "Otherwise ignore the client public key".
        ConnectAndWait(ServerKeys.TwoWay(TestKeyPair.Generate()));

        Assert.All(Slot(_server, ClientAddress).ClientPublicKey, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_client_without_an_identity_is_refused_by_a_server_requiring_one_and_fails_when_its_attempt_times_out()
    {
        // RakPeer.cpp:3503-3515: ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY with reason 1, which the connecting client drops
        // (RakPeer.cpp:6226-6241), and the server closes silently. The client fails 10 s after it took its slot.
        using var test = new SecureConnectionTests(requireClientKey: true);
        test._client.Connect(ServerAddress, default, 12, 500, 0, test._link.NowUs, ServerKeys.Known());

        test._link.RunUntil(() => test.ClientEvents.Count > 0, TimeSpan.FromSeconds(11));

        var failed = Assert.IsType<ConnectionAttemptFailed>(Assert.Single(test.ClientEvents));
        Assert.Equal(MessageId.ConnectionAttemptFailed, failed.Reason);
        Assert.Equal(ServerGuid, failed.Guid);
        Assert.Empty(test.ServerEvents);
        Assert.Equal(ConnectionState.IsDisconnected, test._server.GetConnectionState(ClientAddress));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public void A_server_requiring_client_keys_tells_the_client_why_it_refused(bool sendIdentity, byte reason)
    {
        // RakPeer.cpp:3487-3513: reason 1 when the identity is missing, 2 when it does not verify. A key pair whose
        // halves do not belong together passes SetIdentity, as cat never checks, and fails verification.
        var server = NewServer(requireClientKey: true);
        var client = HandClient.Connect(server, sendIdentity ? (TestKeyPair.Generate().Public, TestKeyPair.Generate().Private) : null, _link.NowUs);

        var refusal = client.ExchangeRequest(server, out _);

        Assert.Equal(new byte[] { (byte)MessageId.RemoteSystemRequiresPublicKey, reason }, refusal);
        // DISCONNECT_ASAP_SILENTLY: closed once the reply has gone, with no event.
        Assert.Equal(ConnectionState.IsDisconnected, server.GetConnectionState(ClientAddress));
    }

    [Fact]
    public void A_request_with_a_wrong_proof_closes_the_connection_silently()
    {
        // RakPeer.cpp:3467-3473.
        var server = NewServer(requireClientKey: false);
        var client = HandClient.Connect(server, null, _link.NowUs);

        client.ExchangeRequest(server, out _, corruptProof: true);

        // DISCONNECT_ASAP_SILENTLY: closed once the reply has gone, with no event.
        Assert.Equal(ConnectionState.IsDisconnected, server.GetConnectionState(ClientAddress));
        Assert.Empty(TakeEvents(server));
    }

    [Fact]
    public void A_request_without_security_is_ignored_by_a_secure_server()
    {
        // RakPeer.cpp:3460-3465: doSecurity must be 1.
        var server = NewServer(requireClientKey: false);
        var client = HandClient.Connect(server, null, _link.NowUs);

        client.ExchangeRequest(server, out var replies, withoutSecurity: true);

        Assert.Empty(replies);
        Assert.Equal(ConnectionState.IsConnecting, server.GetConnectionState(ClientAddress));
    }

    [Fact]
    public void A_proven_request_is_accepted()
    {
        var server = NewServer(requireClientKey: false);
        var client = HandClient.Connect(server, null, _link.NowUs);

        var reply = client.ExchangeRequest(server, out _);

        Assert.Equal((byte)MessageId.ConnectionRequestAccepted, reply[0]);
    }

    [Fact]
    public void A_wrong_cookie_is_ignored()
    {
        // RakPeer.cpp:5217-5222.
        _link.Drop = (from, _, data) =>
        {
            if (from.Equals(ClientAddress) && data[0] == (byte)MessageId.OpenConnectionRequest2)
                data[1 + OfflineMagic.Length] ^= 0x01;
            return false;
        };
        _client.Connect(ServerAddress, default, 3, 100, 0, _link.NowUs, ServerKeys.Known());

        _link.RunUntil(() => ClientEvents.Count > 0, TimeSpan.FromSeconds(2));

        Assert.Equal(MessageId.ConnectionAttemptFailed, Assert.IsType<ConnectionAttemptFailed>(Assert.Single(ClientEvents)).Reason);
        Assert.Empty(Sent(MessageId.OpenConnectionReply2));
    }

    [Fact]
    public void A_cookie_expires_after_four_seconds()
    {
        // CookieJar::EXPIRE_TIME is 4000 ms in 16 bins of 250 ms.
        var server = NewServer(requireClientKey: false);
        var start = _link.NowUs;
        var (cookie, challenge) = HandClient.Probe(server, start);

        server.Receive(ClientAddress, Encode(new SecureOpenConnectionRequest2(cookie, challenge, ServerAddress, 1492, ClientGuid)), start + 4_500_000);

        Assert.Empty(Datagrams(server));
        server.Receive(ClientAddress, Encode(new SecureOpenConnectionRequest2(cookie, challenge, ServerAddress, 1492, ClientGuid)), start + 3_000_000);
        Assert.Equal((byte)MessageId.OpenConnectionReply2, Assert.Single(Datagrams(server)).Data[0]);
    }

    [Fact]
    public void A_request_without_a_challenge_is_ignored_unless_the_address_is_an_exception()
    {
        // RakPeer.cpp:5211-5228.
        var server = NewServer(requireClientKey: false);
        var (cookie, _) = HandClient.Probe(server, _link.NowUs);
        var request = Encode(new SecureOpenConnectionRequest2(cookie, ReadOnlyMemory<byte>.Empty, ServerAddress, 1492, ClientGuid));

        server.Receive(ClientAddress, request, _link.NowUs);
        Assert.Empty(Datagrams(server));

        server.SecurityExceptions.Add("10.0.0.*");
        server.Receive(ClientAddress, request, _link.NowUs);
        var reply = Decode<OpenConnectionReply2>(Assert.Single(Datagrams(server)).Data);
        Assert.False(reply.RequiresSecurity);
        Assert.False(Slot(server, ClientAddress).Session.UsesSecurity);
    }

    [Fact]
    public void A_client_in_the_exception_list_passes_the_offline_handshake_but_its_request_is_ignored()
    {
        // The exception list only spares the client the challenge: its ID_CONNECTION_REQUEST carries no proof, which
        // ParseConnectionRequestPacket ignores (RakPeer.cpp:3460-3465), so the attempt fails once the half-open
        // connection times out.
        var server = NewServer(requireClientKey: false);
        server.SecurityExceptions.Add("10.0.0.1");
        var (cookie, _) = HandClient.Probe(server, _link.NowUs);
        server.Receive(ClientAddress, Encode(new SecureOpenConnectionRequest2(cookie, ReadOnlyMemory<byte>.Empty, ServerAddress, 1492, ClientGuid)), _link.NowUs);
        Datagrams(server);
        var layer = new ReliabilityLayer(1492, _link.NowUs);
        layer.Send(Encode(new ConnectionRequest(ClientGuid, 1, ReadOnlyMemory<byte>.Empty)), Reliability.Reliable, Priority.Immediate, 0, 0, _link.NowUs);
        layer.Update(_link.NowUs + 1_000);
        while (layer.TryDequeueDatagram(out var datagram))
            server.Receive(ClientAddress, datagram, _link.NowUs + 1_000);
        server.Update(_link.NowUs + 2_000);

        Assert.Equal(ConnectionState.IsConnecting, server.GetConnectionState(ClientAddress));
        server.Update(_link.NowUs + 10_002_000);
        Assert.Equal(ConnectionState.IsDisconnected, server.GetConnectionState(ClientAddress));
        Assert.Empty(TakeEvents(server));
    }

    [Fact]
    public void A_duplicate_request_gets_the_same_answer_again()
    {
        // RakPeer.cpp:5307-5332: outcome 1 resends the cached answer.
        var server = NewServer(requireClientKey: false);
        var (cookie, challenge) = HandClient.Probe(server, _link.NowUs);
        var request = Encode(new SecureOpenConnectionRequest2(cookie, challenge, ServerAddress, 1492, ClientGuid));

        server.Receive(ClientAddress, request, _link.NowUs);
        var first = Assert.Single(Datagrams(server)).Data;
        server.Receive(ClientAddress, request, _link.NowUs);
        var second = Assert.Single(Datagrams(server)).Data;

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_bad_challenge_gets_no_answer_and_leaves_the_guid_in_use()
    {
        // RakPeer.cpp:5391-5402: DereferenceRemoteSystem only takes the system out of the address lookup, so its GUID is
        // still active and the next request is answered ID_ALREADY_CONNECTED (outcome 3).
        var server = NewServer(requireClientKey: false);
        var (cookie, _) = HandClient.Probe(server, _link.NowUs);
        var bad = Encode(new SecureOpenConnectionRequest2(cookie, new byte[EasyHandshake.ChallengeBytes], ServerAddress, 1492, ClientGuid));

        server.Receive(ClientAddress, bad, _link.NowUs);
        Assert.Empty(Datagrams(server));

        server.Receive(ClientAddress, bad, _link.NowUs);
        Assert.Equal((byte)MessageId.AlreadyConnected, Assert.Single(Datagrams(server)).Data[0]);
    }

    [Fact]
    public void A_client_expecting_another_key_is_told_of_the_mismatch_on_every_reply()
    {
        _client.Connect(ServerAddress, default, 3, 100, 0, _link.NowUs, TestKeyPair.Generate().Known());

        _link.RunUntil(() => ClientEvents.Any(e => e.Id == MessageId.ConnectionAttemptFailed), TimeSpan.FromSeconds(2));

        Assert.Equal(4, ClientEvents.Count(e => e.Id == MessageId.PublicKeyMismatch));
        Assert.Empty(Sent(MessageId.OpenConnectionRequest2));
    }

    [Fact]
    public void An_insecure_client_is_told_of_a_mismatch_by_a_secure_server()
    {
        // With PKM_INSECURE_CONNECTION remote_public_key is never set (RakPeer.cpp:3197-3205).
        _client.Connect(ServerAddress, default, 3, 100, 0, _link.NowUs);

        _link.RunUntil(() => ClientEvents.Any(e => e.Id == MessageId.ConnectionAttemptFailed), TimeSpan.FromSeconds(2));

        Assert.All(ClientEvents.SkipLast(1), e => Assert.Equal(MessageId.PublicKeyMismatch, e.Id));
    }

    [Fact]
    public void A_secure_client_to_an_insecure_server_reports_our_system_requires_security()
    {
        var link = new InMemoryLink();
        using var client = link.Attach(ClientAddress, new PeerCore(new PeerOptions { Guid = ClientGuid }));
        using var server = link.Attach(ServerAddress, new PeerCore(new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8 }));
        client.Connect(ServerAddress, default, 3, 100, 0, link.NowUs, ServerKeys.Known());

        link.RunUntil(() => link.EventsOf(client).Any(e => e.Id == MessageId.ConnectionAttemptFailed), TimeSpan.FromSeconds(2));

        Assert.All(link.EventsOf(client).SkipLast(1), e => Assert.Equal(MessageId.OurSystemRequiresSecurity, e.Id));
        Assert.Equal(4, link.EventsOf(client).Count - 1);
    }

    [Fact]
    public void A_secure_client_given_an_insecure_reply_2_takes_its_slot_but_sends_no_request()
    {
        // RakPeer.cpp:4941-4992: the slot is assigned before the missing answer is processed; ProcessAnswer fails and the
        // request stays queued.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, ServerKeys.Known());
        _link.Step();
        Datagrams(_client);

        _client.Receive(ServerAddress, Encode(new OpenConnectionReply2(ServerGuid, ClientAddress, 1492)), _link.NowUs);

        Assert.Empty(Datagrams(_client));
        Assert.Equal(ConnectionState.IsPending, _client.GetConnectionState(ServerAddress));
        Assert.Equal(ConnectionState.IsConnecting, _client.GetConnectionState(ServerGuid));
    }

    [Fact]
    public void A_forged_insecure_reply_2_cannot_downgrade_a_secure_connect()
    {
        // Native RakPeer.cpp:4941-4998 reuses the slot the forged reply assigned without security, keys an auth_enc it
        // never uses, and sends the request with its proof in the clear. A secure attempt always takes a secure slot.
        _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, ServerKeys.Known());
        _client.Receive(ServerAddress, Encode(new OpenConnectionReply2(ServerGuid, ClientAddress, 1492)), _link.NowUs);

        // The genuine offline handshake runs, but everything else the client sends goes to a man in the middle. An
        // encrypted datagram's first byte is ciphertext, so only the offline magic tells the two apart.
        var captured = new List<byte[]>();
        _link.Drop = (from, _, data) =>
        {
            if (!from.Equals(ClientAddress) || OfflineMagic.Matches(data.AsSpan(1)))
                return false;
            captured.Add(data);
            return true;
        };
        _link.RunUntil(() => captured.Count > 0, TimeSpan.FromSeconds(5));

        Assert.True(Slot(_client, ServerAddress).Session.UsesSecurity);
        // ID_CONNECTION_REQUEST carries the client's GUID; in the clear it would be there to read.
        var guid = new byte[sizeof(ulong)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(guid, ClientGuid);
        Assert.DoesNotContain(captured, d => d.AsSpan().IndexOf(guid) >= 0);

        // The man in the middle accepts in the clear.
        var mitm = new ReliabilityLayer(1492, _link.NowUs);
        mitm.Send(Encode(new ConnectionRequestAccepted(ClientAddress, 0, InternalAddressList.Pad([ServerAddress]), 1, 2)), Reliability.Reliable, Priority.Immediate, 0, 0, _link.NowUs);
        mitm.Update(_link.NowUs + 1_000);
        while (mitm.TryDequeueDatagram(out var datagram))
            _client.Receive(ServerAddress, datagram, _link.NowUs);
        _link.Step();
        Assert.DoesNotContain(ClientEvents, e => e is OutgoingConnectionCompleted);

        // The genuine server, once reachable, completes the secure connection.
        _link.Drop = null;
        _link.RunUntil(() => ClientEvents.OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(5));
        Assert.True(Slot(_client, ServerAddress).Session.UsesSecurity);
    }

    [Fact]
    public void Shutting_down_zeroes_every_connections_keys()
    {
        ConnectAndWait(ServerKeys.Known());
        var clientKeys = Slot(_client, ServerAddress).Session.Encryption;
        var serverKeys = Slot(_server, ClientAddress).Session.Encryption;
        Assert.False(IsZero(clientKeys.ExportKeyState()));

        _client.Dispose();
        _server.Dispose();

        Assert.True(IsZero(clientKeys.ExportKeyState()));
        Assert.True(IsZero(serverKeys.ExportKeyState()));
    }

    [Fact]
    public void Closing_a_connection_zeroes_its_keys()
    {
        ConnectAndWait(ServerKeys.Known());
        var keys = Slot(_client, ServerAddress).Session.Encryption;

        _client.CloseConnection(ServerAddress, false, 0, Priority.Low, _link.NowUs);

        Assert.True(IsZero(keys.ExportKeyState()));
    }

    private static bool IsZero(KeyState state) =>
        new[] { state.LocalCipherWords, state.RemoteCipherWords, state.LocalMacInitial, state.LocalMacFinal, state.RemoteMacInitial, state.RemoteMacFinal }
            .All(bytes => bytes.All(b => b == 0));

    [Fact]
    public void A_bad_server_key_is_refused_when_connecting()
    {
        // GenerateConnectionRequestChallenge fails, which Connect reports as SECURITY_INITIALIZATION_FAILED.
        var notAPoint = new byte[EasyHandshake.PublicKeyBytes];
        notAPoint[0] = 1;

        Assert.Null(ClientSecurity.Create(ClientKeyMode.UseKnownPublicKey, notAPoint, default, default, SystemSecureRandom.Instance));
        Assert.Null(ClientSecurity.Create(ClientKeyMode.UseKnownPublicKey, new byte[10], default, default, SystemSecureRandom.Instance));
        Assert.Null(ClientSecurity.Create(ClientKeyMode.UseTwoWayAuthentication, ServerKeys.Public, notAPoint, ServerKeys.Private, SystemSecureRandom.Instance));
    }

    [Fact]
    public void A_server_key_pair_cat_refuses_is_a_programming_error()
    {
        Assert.Throws<ArgumentException>(() => new PeerCore(new PeerOptions { Guid = ServerGuid, Security = new ServerSecurityOptions(new byte[64], ServerKeys.Private, false) }));
    }

    private void ConnectAndWait(ClientSecurity security)
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs, security));
        _link.RunUntil(
            () => ClientEvents.OfType<OutgoingConnectionCompleted>().Any() && ServerEvents.OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(2));
    }

    private IEnumerable<byte[]> Sent(MessageId id) => _link.Sent.Where(d => d.Data[0] == (byte)id).Select(d => d.Data);

    private static RemoteSystem Slot(PeerCore core, IPEndPoint address) =>
        core.ActiveSystems.Single(s => s.Address.Equals(address));

    private static List<PeerEvent> TakeEvents(PeerCore core) => Events(core);

    // A client run by hand against a server core: the offline handshake through a real ClientEasyHandshake, then a
    // secure reliability layer keyed by the server's answer, as RakPeer's client does it.
    private sealed class HandClient
    {
        private readonly ReliabilityLayer _layer;
        private readonly byte[]? _identity;
        private long _nowUs;

        private HandClient(ReliabilityLayer layer, byte[]? identity, long nowUs)
        {
            _layer = layer;
            _identity = identity;
            _nowUs = nowUs;
        }

        public static (uint Cookie, byte[] Challenge) Probe(PeerCore server, long nowUs)
        {
            server.Receive(ClientAddress, Encode(new OpenConnectionRequest1(RakNetConstants.ProtocolVersion, 1492)), nowUs);
            var reply1 = Decode<SecureOpenConnectionReply1>(Assert.Single(Datagrams(server)).Data);
            using var handshake = new ClientEasyHandshake(SystemSecureRandom.Instance);
            var challenge = new byte[EasyHandshake.ChallengeBytes];
            Assert.True(handshake.Initialize(reply1.ServerPublicKey.Span));
            Assert.True(handshake.GenerateChallenge(challenge));
            return (reply1.Cookie, challenge);
        }

        public static HandClient Connect(PeerCore server, (byte[] Public, byte[] Private)? identityKeys, long nowUs)
        {
            server.Receive(ClientAddress, Encode(new OpenConnectionRequest1(RakNetConstants.ProtocolVersion, 1492)), nowUs);
            var reply1 = Decode<SecureOpenConnectionReply1>(Assert.Single(Datagrams(server)).Data);
            using var handshake = new ClientEasyHandshake(SystemSecureRandom.Instance);
            var challenge = new byte[EasyHandshake.ChallengeBytes];
            Assert.True(handshake.Initialize(reply1.ServerPublicKey.Span));
            if (identityKeys is { } keys)
                Assert.True(handshake.SetIdentity(keys.Public, keys.Private));
            Assert.True(handshake.GenerateChallenge(challenge));
            server.Receive(ClientAddress, Encode(new SecureOpenConnectionRequest2(reply1.Cookie, challenge, ServerAddress, 1492, ClientGuid)), nowUs);
            var reply2 = Decode<OpenConnectionReply2>(Assert.Single(Datagrams(server)).Data);

            var layer = new ReliabilityLayer(1492, nowUs, useSecurity: true);
            byte[]? identity = null;
            if (identityKeys is not null)
            {
                identity = new byte[EasyHandshake.IdentityBytes];
                Assert.True(handshake.ProcessAnswerWithIdentity(reply2.Answer.Span, identity, layer.Encryption));
            }
            else
            {
                Assert.True(handshake.ProcessAnswer(reply2.Answer.Span, layer.Encryption));
            }
            return new HandClient(layer, identity, nowUs);
        }

        // Sends ID_CONNECTION_REQUEST over the secure layer and returns the first message the server sends back.
        public byte[] ExchangeRequest(PeerCore server, out List<byte[]> replies, bool corruptProof = false, bool withoutSecurity = false)
        {
            var proof = new byte[ConnectionRequest.ProofLength];
            _layer.Encryption.GenerateProof(proof);
            if (corruptProof)
                proof[0] ^= 1;
            var request = withoutSecurity
                ? new ConnectionRequest(ClientGuid, 1, ReadOnlyMemory<byte>.Empty)
                : new ConnectionRequest(ClientGuid, 1, ReadOnlyMemory<byte>.Empty) { DoSecurity = true, Proof = proof, Identity = _identity ?? ReadOnlyMemory<byte>.Empty };
            _layer.Send(Encode(request), Reliability.Reliable, Priority.Immediate, 0, 0, _nowUs);

            replies = [];
            for (var i = 0; i < 20; i++)
            {
                _nowUs += 1_000;
                _layer.Update(_nowUs);
                while (_layer.TryDequeueDatagram(out var datagram))
                    server.Receive(ClientAddress, datagram, _nowUs);
                server.Update(_nowUs);
                foreach (var datagram in Datagrams(server))
                    _layer.Receive(datagram.Data, _nowUs);
                while (_layer.TryDequeueEvent(out var e))
                {
                    if (e is MessageDelivered delivered)
                        replies.Add(delivered.Payload.ToArray());
                }
            }
            return replies.FirstOrDefault() ?? [];
        }
    }
}
