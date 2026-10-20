using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.TwoWayAuthentication;

public sealed class TwoWayAuthenticationTests : IDisposable
{
    private const byte Negotiation = (byte)MessageId.TwoWayAuthenticationNegotiation;
    private const byte NonceRequest = 0;
    private const byte NonceReply = 1;
    private const byte HashedNonceAndPassword = 2;

    private readonly PluginMesh _mesh = new();
    private readonly Plugins.TwoWayAuthentication _first = new();
    private readonly Plugins.TwoWayAuthentication _second = new();
    private readonly RakPeer _firstPeer;
    private readonly RakPeer _secondPeer;

    public TwoWayAuthenticationTests()
    {
        _firstPeer = _mesh.Add(maxIncoming: 4, _first);
        _secondPeer = _mesh.Add(maxIncoming: 4, _second);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_challenge_both_systems_hold_the_password_for_succeeds_on_both()
    {
        // TwoWayAuthenticationTest.cpp, stage 0.
        Connect();
        Assert.True(_first.AddPassword("PWD0", "Password0"));
        Assert.True(_first.AddPassword("PWD1", "Password1"));
        Assert.True(_second.AddPassword("PWD0", "Password0"));

        Assert.True(_first.Challenge("PWD0", _secondPeer.RakNetGuid));
        _mesh.Settle();

        // PushToUser (TwoWayAuthentication.cpp:258-274): the ID, then the identifier as a RakString, generated locally.
        var outgoing = Assert.Single(Pushed(_firstPeer));
        Assert.Equal([(byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess, .. RakString("PWD0")], outgoing.Data);
        Assert.Equal(_secondPeer.RakNetGuid, outgoing.RakNetGuid);
        var incoming = Assert.Single(Pushed(_secondPeer));
        Assert.Equal([(byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess, .. RakString("PWD0")], incoming.Data);
        Assert.Equal(_firstPeer.RakNetGuid, incoming.RakNetGuid);
        Assert.DoesNotContain(_mesh.Seen(_firstPeer).Concat(_mesh.Seen(_secondPeer)), e => e.Id == Negotiation);
    }

    [Fact]
    public void Challenges_succeed_in_either_direction_and_again()
    {
        // TwoWayAuthenticationTest.cpp, stages 1 and 2.
        Connect();
        _first.AddPassword("PWD0", "Password0");
        _second.AddPassword("PWD0", "Password0");

        Assert.True(_second.Challenge("PWD0", _firstPeer.RakNetGuid));
        _mesh.Settle();
        Assert.True(_second.Challenge("PWD0", _firstPeer.RakNetGuid));
        _mesh.Settle();

        Assert.Equal(2, Pushed(_secondPeer).Count(e => e.Id == (byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess));
        Assert.Equal(2, Pushed(_firstPeer).Count(e => e.Id == (byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess));
    }

    [Fact]
    public void A_challenge_with_an_identifier_never_added_is_refused()
    {
        // TwoWayAuthenticationTest.cpp, stage 3.
        Connect();
        _first.AddPassword("PWD0", "Password0");

        Assert.False(_first.Challenge("PWD3", _secondPeer.RakNetGuid));
    }

    [Fact]
    public void A_challenge_for_a_password_the_other_system_lacks_fails_on_both()
    {
        // TwoWayAuthenticationTest.cpp, stage 4: "should fail twice".
        Connect();
        _first.AddPassword("PWD0", "Password0");
        _first.AddPassword("PWD1", "Password1");
        _second.AddPassword("PWD0", "Password0");

        Assert.True(_first.Challenge("PWD1", _secondPeer.RakNetGuid));
        _mesh.Settle();

        Assert.Equal(
            [(byte)MessageId.TwoWayAuthenticationOutgoingChallengeFailure, .. RakString("PWD1")],
            Assert.Single(Pushed(_firstPeer)).Data);
        // TwoWayAuthentication.cpp:374-386: the hashed-nonce message itself, its first byte rewritten, reaches the user.
        var failure = Assert.Single(_mesh.Seen(_secondPeer), e => e.Id == (byte)MessageId.TwoWayAuthenticationIncomingChallengeFailure);
        Assert.Equal(HashedNonceAndPassword, failure.Data[1]);
        Assert.Equal([.. RakString("PWD1")], failure.Data[4..^20]);
        Assert.Equal(_firstPeer.RakNetGuid, failure.RakNetGuid);
    }

    [Fact]
    public void A_challenge_with_a_different_password_fails_on_both()
    {
        Connect();
        _first.AddPassword("PWD0", "Password0");
        _second.AddPassword("PWD0", "Other");

        _first.Challenge("PWD0", _secondPeer.RakNetGuid);
        _mesh.Settle();

        Assert.Equal((byte)MessageId.TwoWayAuthenticationOutgoingChallengeFailure, Assert.Single(Pushed(_firstPeer)).Id);
        Assert.Contains(_mesh.Seen(_secondPeer), e => e.Id == (byte)MessageId.TwoWayAuthenticationIncomingChallengeFailure);
    }

    [Fact]
    public void A_challenge_nobody_answers_times_out()
    {
        var plain = _mesh.Add(maxIncoming: 0);
        _mesh.Connect(plain, _firstPeer);
        _first.AddPassword("PWD0", "Password0");

        _first.Challenge("PWD0", plain.RakNetGuid);
        _mesh.Run(TimeSpan.FromSeconds(2));
        Assert.Empty(Pushed(_firstPeer));
        // Update first checks CHALLENGE_MINIMUM_TIMEOUT after the plugin starts, too soon for this challenge, then waits
        // twice that (TwoWayAuthentication.cpp:180-195).
        _mesh.Run(TimeSpan.FromSeconds(8));

        var timeout = Assert.Single(Pushed(_firstPeer));
        Assert.Equal([(byte)MessageId.TwoWayAuthenticationOutgoingChallengeTimeout, .. RakString("PWD0")], timeout.Data);
        Assert.Equal(plain.RakNetGuid, timeout.RakNetGuid);
        Assert.Equal(SystemAddress.Unassigned, timeout.Address);
    }

    [Fact]
    public void A_challenge_to_a_system_that_disconnects_is_forgotten()
    {
        var plain = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(plain, _firstPeer);
        _first.AddPassword("PWD0", "Password0");
        _first.Challenge("PWD0", plain.RakNetGuid);

        connection.Close();
        _mesh.Run(TimeSpan.FromSeconds(10));

        Assert.Empty(Pushed(_firstPeer));
    }

    [Fact]
    public void The_negotiation_is_a_nonce_then_the_sha1_of_nonce_and_password_as_RakNet_lays_them_out()
    {
        // A peer without the plugin plays the challenger byte by byte (TwoWayAuthentication.cpp:276-357).
        var raw = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(raw, _firstPeer);
        _first.AddPassword("PWD0", "Password0");

        connection.Send(new byte[] { Negotiation, NonceRequest }, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(raw).Any(e => e.Id == Negotiation), "the nonce reply");
        var reply = _mesh.Seen(raw).Single(e => e.Id == Negotiation).Data;
        Assert.Equal(1 + 1 + 2 + 20, reply.Length);
        Assert.Equal(NonceReply, reply[1]);
        // NonceGenerator::nextRequestId starts at 0.
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2)));
        var nonce = reply[4..];

#pragma warning disable CA5350 // TwoWayAuthentication.cpp:434-438 hashes with SHA-1.
        var hash = SHA1.HashData([.. nonce, .. "Password0"u8]);
#pragma warning restore CA5350
        connection.Send((byte[])[Negotiation, HashedNonceAndPassword, 0, 0, .. RakString("PWD0"), .. hash], Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(raw).Any(e => e.Id == (byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess), "the result");

        // TwoWayAuthentication.cpp:362-367: the ID, the nonce, the hash, then the identifier.
        var result = _mesh.Seen(raw).Single(e => e.Id == (byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess).Data;
        Assert.Equal([(byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess, .. nonce, .. hash, .. RakString("PWD0")], result);
        Assert.Equal([(byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess, .. RakString("PWD0")], Assert.Single(Pushed(_firstPeer)).Data);
    }

    [Fact]
    public void A_hash_for_a_nonce_never_handed_out_is_dropped()
    {
        var raw = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(raw, _firstPeer);
        _first.AddPassword("PWD0", "Password0");

        connection.Send((byte[])[Negotiation, HashedNonceAndPassword, 0, 7, .. RakString("PWD0"), .. new byte[20]], Reliability.ReliableOrdered);
        connection.Send(new byte[] { 0x86 }, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(_firstPeer).Any(e => e.Id == 0x86), "the marker");

        Assert.DoesNotContain(_mesh.Seen(_firstPeer), e => e.Id is Negotiation or (byte)MessageId.TwoWayAuthenticationIncomingChallengeFailure);
        Assert.DoesNotContain(_mesh.Seen(raw), e => e.Id == (byte)MessageId.TwoWayAuthenticationOutgoingChallengeFailure);
    }

    [Fact]
    public void Results_sent_by_another_system_are_dropped()
    {
        // TwoWayAuthentication.cpp:226-248: only locally generated results reach the user.
        var raw = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(raw, _firstPeer);

        foreach (var id in new[] { MessageId.TwoWayAuthenticationIncomingChallengeSuccess, MessageId.TwoWayAuthenticationOutgoingChallengeSuccess,
                     MessageId.TwoWayAuthenticationIncomingChallengeFailure, MessageId.TwoWayAuthenticationOutgoingChallengeFailure,
                     MessageId.TwoWayAuthenticationOutgoingChallengeTimeout })
            connection.Send((byte[])[(byte)id, .. new byte[40], .. RakString("PWD0")], Reliability.ReliableOrdered);
        connection.Send(new byte[] { 0x86 }, Reliability.ReliableOrdered);
        _mesh.RunUntil(() => _mesh.Seen(_firstPeer).Any(e => e.Id == 0x86), "the marker");

        Assert.DoesNotContain(_mesh.Seen(_firstPeer), e => e.Id is >= (byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess and <= (byte)MessageId.TwoWayAuthenticationNegotiation);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("id", "")]
    [InlineData("same", "same")]
    public void A_password_that_is_blank_or_equal_to_its_identifier_is_refused(string identifier, string password) =>
        Assert.False(_first.AddPassword(identifier, password));

    [Fact]
    public void An_identifier_can_be_added_only_once()
    {
        Assert.True(_first.AddPassword("PWD0", "Password0"));
        Assert.False(_first.AddPassword("PWD0", "Password1"));

        _first.Clear();

        Assert.True(_first.AddPassword("PWD0", "Password1"));
    }

    private void Connect() => _mesh.Connect(_secondPeer, _firstPeer);

    private List<SeenEvent> Pushed(RakPeer peer) =>
        [.. _mesh.Seen(peer).Where(e => e.Id is >= (byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess and <= (byte)MessageId.TwoWayAuthenticationOutgoingChallengeTimeout && e.IsLocal)];

    private static byte[] RakString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [(byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }
}
