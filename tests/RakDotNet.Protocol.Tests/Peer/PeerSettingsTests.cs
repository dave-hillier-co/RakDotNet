using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Peer;

// The RakPeer settings that reach each connection's reliability layer (RakPeer.cpp:3644-3648, 2697-2724, 2961-2964,
// 5871), the ones read during the handshake (SetIncomingPassword, AllowConnectionResponseIPMigration at
// RakPeer.cpp:6133-6137), ChangeSystemAddress (RakPeer.cpp:2846-2856, 5646-5654), and the bit-exact length of a
// message (RakPeer.cpp:1408, 6231).
public class PeerSettingsTests
{
    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;

    private static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    private static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private readonly InMemoryLink _link = new();
    private PeerCore _client = null!;
    private PeerCore _server = null!;

    private void Attach(PeerOptions? client = null, PeerOptions? server = null)
    {
        _client = _link.Attach(ClientAddress, new PeerCore(client ?? new PeerOptions { Guid = ClientGuid }));
        _server = _link.Attach(ServerAddress, new PeerCore(server ?? new PeerOptions { Guid = ServerGuid, MaxIncomingConnections = 8 }));
    }

    private IReadOnlyList<PeerEvent> ClientEvents => _link.EventsOf(_client);

    private IReadOnlyList<PeerEvent> ServerEvents => _link.EventsOf(_server);

    [Fact]
    public void A_new_connection_takes_RakPeers_unreliable_timeout_by_default()
    {
        Attach();
        Connect();

        // RakPeer.cpp:238 and 3647: every connection gets the peer's 1000 ms, not the reliability layer's own 0.
        Assert.Equal(ReliabilityLayer.RakPeerUnreliableTimeoutMs, SessionOf(_client, ServerGuid).UnreliableTimeoutMs);
        Assert.Equal(1000, _client.UnreliableTimeoutMs);
    }

    [Fact]
    public void A_new_connection_takes_the_unreliable_timeout_progress_interval_and_limits_from_the_options()
    {
        var limits = SessionLimits.Default with { MaxSplitCount = 77 };
        Attach(server: new PeerOptions
        {
            Guid = ServerGuid,
            MaxIncomingConnections = 8,
            UnreliableTimeoutMs = 250,
            SplitMessageProgressInterval = 3,
            SessionLimits = limits,
        });
        Connect();

        var session = SessionOf(_server, ClientGuid);
        Assert.Equal(250, session.UnreliableTimeoutMs);
        Assert.Equal(3, session.SplitMessageProgressInterval);
        Assert.Equal(limits, session.Limits);
    }

    [Fact]
    public void Changing_the_unreliable_timeout_and_progress_interval_reaches_every_live_connection()
    {
        Attach();
        Connect();

        // RakPeer::SetUnreliableTimeout and SetSplitMessageProgressInterval loop over every remote system
        // (RakPeer.cpp:2697-2724).
        _client.UnreliableTimeoutMs = 40;
        _client.SplitMessageProgressInterval = 5;

        var session = SessionOf(_client, ServerGuid);
        Assert.Equal(40, session.UnreliableTimeoutMs);
        Assert.Equal(5, session.SplitMessageProgressInterval);
    }

    [Fact]
    public void A_progress_interval_makes_the_receiver_report_ID_DOWNLOAD_PROGRESS_while_a_split_message_arrives()
    {
        Attach();
        Connect();
        _server.SplitMessageProgressInterval = 2;

        var message = new byte[10_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        _client.Send(ServerGuid, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Any(m => m.Data.Length == message.Length), TimeSpan.FromSeconds(5));

        Assert.Contains(ServerEvents.OfType<MessageReceived>(), m => m.Data.Span[0] == (byte)MessageId.DownloadProgress);
    }

    [Fact]
    public void The_outgoing_bandwidth_limit_holds_back_new_data_and_shows_in_the_statistics()
    {
        Attach();
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));

        // RakPeer.cpp:5871 passes maxOutgoingBPS to every reliability layer update.
        _client.OutgoingBandwidthLimit = 8_000;
        var message = new byte[1_000];
        message[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 20; i++)
            _client.Send(ServerGuid, message, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(500));

        var statistics = SessionOf(_client, ServerGuid).GetStatistics(_link.NowUs);
        Assert.True(statistics.IsLimitedByOutgoingBandwidthLimit);
        Assert.Equal(1_000UL, statistics.BpsLimitByOutgoingBandwidthLimit);
        Assert.InRange(ServerEvents.OfType<MessageReceived>().Count(m => m.Data.Length == message.Length), 1, 5);
    }

    [Fact]
    public void A_changed_incoming_password_applies_to_later_connection_requests()
    {
        Attach();
        _server.IncomingPassword = "secret"u8.ToArray();

        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => ClientEvents.OfType<ConnectionAttemptFailed>().Any(), TimeSpan.FromSeconds(2));
        Assert.Equal((byte)MessageId.InvalidPassword, ClientEvents.OfType<ConnectionAttemptFailed>().Single().Data.Span[0]);

        // The server keeps the refused slot until its ID_INVALID_PASSWORD is acknowledged or times out.
        _link.RunUntil(() => _server.ActiveSystemCount == 0, TimeSpan.FromSeconds(15), stepUs: 10_000);
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, "secret"u8, 12, 500, 0, _link.NowUs));
        _link.RunUntil(() => ClientEvents.OfType<OutgoingConnectionCompleted>().Any(), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void A_repeated_connection_acceptance_is_ignored_unless_IP_migration_is_allowed()
    {
        Attach();
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));

        // A connection request over an established connection is answered with another acceptance (RakPeer.cpp:5990-5998).
        RequestAcceptanceAgain();
        Assert.Single(ClientEvents.OfType<OutgoingConnectionCompleted>());

        // RakPeer.cpp:6133-6137: with migration allowed the acceptance is processed again and reported.
        _client.AllowConnectionResponseIpMigration = true;
        RequestAcceptanceAgain();
        Assert.Equal(2, ClientEvents.OfType<OutgoingConnectionCompleted>().Count());
    }

    [Fact]
    public void ChangeSystemAddress_moves_a_connection_to_a_new_address()
    {
        Attach();
        Connect();
        var moved = new IPEndPoint(IPAddress.Parse("10.0.0.3"), 40_000);

        // RakPeer.cpp:5646-5654: the system found by GUID is referenced at the new address.
        _client.ChangeSystemAddress(ServerGuid, moved);

        Assert.True(_client.TryGetAddress(ServerGuid, out var address));
        Assert.Equal(moved, address);
        Assert.Equal(Protocol.Peer.ConnectionState.IsConnected, _client.GetConnectionState(moved));
        var sentBefore = _link.Sent.Count;
        _client.Send(ServerGuid, new byte[] { (byte)MessageId.UserPacketEnum }, Reliability.Reliable, Priority.High, 0, _link.NowUs);
        _link.Step();
        _link.Step();
        Assert.Contains(_link.Sent.Skip(sentBefore), d => d.From.Equals(ClientAddress) && d.To.Equals(moved));
    }

    [Fact]
    public void A_message_whose_length_ends_mid_byte_arrives_with_its_bit_length()
    {
        Attach();
        Connect();

        // RakPeer.cpp:1408 sends Send(BitStream*)'s bit length; RakPeer.cpp:6231 reports the length delivered.
        _client.Send(ServerGuid, new byte[] { (byte)MessageId.UserPacketEnum, 0xF0 }, 12, Reliability.ReliableOrdered, Priority.High, 0, _link.NowUs);
        _link.RunUntil(() => ServerEvents.OfType<MessageReceived>().Any(m => m.Data.Span[0] == (byte)MessageId.UserPacketEnum), TimeSpan.FromSeconds(1));

        var received = ServerEvents.OfType<MessageReceived>().Single(m => m.Data.Span[0] == (byte)MessageId.UserPacketEnum);
        Assert.Equal(12, received.BitLength);
        Assert.Equal(2, received.Data.Length);
    }

    [Fact]
    public void An_unreliable_receipt_discarded_unsent_is_reported_as_discarded()
    {
        Attach();
        Connect();
        _link.Run(TimeSpan.FromMilliseconds(50));
        _client.UnreliableTimeoutMs = 100;

        // Nothing comes back from the server, so the window stays full and the unreliable message waits until culled
        // (ReliabilityLayer.cpp:1745-1793). RakNet reports nothing for it; the core says so, so the receipt can end.
        _link.Drop = (from, _, _) => from.Equals(ServerAddress);
        var payload = new byte[1_000];
        payload[0] = (byte)MessageId.UserPacketEnum;
        for (var i = 0; i < 100; i++)
            _client.Send(ServerGuid, payload, Reliability.Reliable, Priority.High, 0, _link.NowUs);
        var receipt = _client.Send(ServerGuid, payload, Reliability.UnreliableWithAckReceipt, Priority.Low, 0, _link.NowUs);
        _link.RunUntil(() => ClientEvents.OfType<SendReceiptDiscarded>().Any(e => e.Receipt == receipt), TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(ClientEvents.OfType<SendReceiptLost>(), e => e.Receipt == receipt);
        Assert.DoesNotContain(ClientEvents.OfType<SendReceiptAcked>(), e => e.Receipt == receipt);
    }

    [Fact]
    public void The_ban_table_can_be_shared_with_another_thread()
    {
        Attach();

        // RakPeer guards banList with banListMutex because AddToBanList runs on the caller's thread.
        Parallel.For(0, 1_000, i =>
        {
            _server.Bans.Add($"10.1.{i % 250}.{i % 7}", 0, _link.NowMs);
            _server.Bans.IsBanned("10.0.0.1", _link.NowMs);
        });

        Assert.True(_server.Bans.IsBanned("10.1.3.3", _link.NowMs));
        Assert.True(_server.Bans.Remove("10.1.3.3"));
        Assert.False(_server.Bans.Remove("10.1.3.3"));
    }

    private void RequestAcceptanceAgain()
    {
        var request = new ConnectionRequest(ClientGuid, (ulong)_link.NowMs, ReadOnlyMemory<byte>.Empty);
        var writer = new BitStreamWriter(new byte[64]);
        request.Write(ref writer);
        _client.Send(ServerGuid, writer.Written, Reliability.ReliableOrdered, Priority.Immediate, 0, _link.NowUs);
        _link.Run(TimeSpan.FromMilliseconds(50));
    }

    private void Connect()
    {
        Assert.Equal(ConnectionAttemptResult.ConnectionAttemptStarted, _client.Connect(ServerAddress, default, 12, 500, 0, _link.NowUs));
        _link.RunUntil(
            () => ClientEvents.OfType<OutgoingConnectionCompleted>().Any() && ServerEvents.OfType<IncomingConnectionCompleted>().Any(),
            TimeSpan.FromSeconds(1)
        );
    }

    private static ReliabilityLayer SessionOf(PeerCore peer, ulong guid)
    {
        Assert.True(peer.TryGetRemoteSystem(guid, out var system));
        return system.Session;
    }
}
