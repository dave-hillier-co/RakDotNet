using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// RelayPlugin, from Samples/RelayPluginTest: a relay server of the server's kind, Alice of the client's kind and Bob of
/// the server's, which add themselves as participants, send to each other by name, and join, message, list and leave
/// groups. Expected messages are written as RelayPlugin.cpp writes them.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class RelayPluginScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_server_adds_participants_on_request_only_when_it_accepts_them(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (alice, bob) = await StartAsync(session, client, server, accept: false);
        var serverGuid = session.Server.Started.PeerGuid;

        await alice.Relay().AddParticipantRequestAsync("alice", serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.AddClientNotAllowed, s => Compressed(s, "alice")), await NextRelayAsync(alice));

        // The sample's server accepts requests.
        await session.Server.Relay().AttachRelayAsync(acceptAddParticipantRequests: true, Token);
        await alice.Relay().AddParticipantRequestAsync("alice", serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.AddClientSuccess, s => Compressed(s, "alice")), await NextRelayAsync(alice));
        await bob.Relay().AddParticipantRequestAsync("alice", serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.AddClientNameAlreadyInUse, s => Compressed(s, "alice")), await NextRelayAsync(bob));

        Assert.Equal("RPE_ADD_CLIENT_TARGET_NOT_CONNECTED", await session.Server.Relay().AddParticipantOnServerAsync("carol", 0x0102030405060708, Token));
        Assert.Equal("RPE_ADD_CLIENT_SUCCESS", await session.Server.Relay().AddParticipantOnServerAsync("bob", bob.Started.PeerGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_message_to_a_participant_by_name_arrives_with_the_senders_name(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (alice, bob) = await StartAsync(session, client, server, accept: true);
        var serverGuid = session.Server.Started.PeerGuid;
        await AddAsync(alice, "alice", serverGuid);
        await AddAsync(bob, "bob", serverGuid);

        // SendTo(P)articipant: the message is a compressed RakString, so not a whole number of bytes.
        var (text, textBits) = Bits(s => Compressed(s, "hello bob"));
        await alice.Relay().SendToParticipantAsync(serverGuid, "bob", text, textBits, cancellationToken: Token);

        // RelayPlugin.cpp:175-186: the sender's name, alignment, then every bit after the name in the request, the
        // padding of its last byte included.
        var request = Relay(RelayPluginMessage.MessageToServerFromClient, s =>
        {
            s.WriteByte((byte)Priority.High);
            s.WriteByte((byte)Reliability.ReliableOrdered);
            s.WriteByte(0);
            Compressed(s, "bob");
            WriteBits(s, text, textBits);
        });
        const int keyEnd = 2 + 3;
        var expected = Relay(RelayPluginMessage.MessageToClientFromServer, s =>
        {
            Compressed(s, "alice");
            s.AlignToByte();
            var reader = new BitStreamReader(request);
            reader.IgnoreBytes(keyEnd);
            Assert.True(reader.TryReadCompressedRakStringBytes(out _));
            s.Write(ref reader);
        });
        var relayed = await NextRelayAsync(bob);
        Assert.Equal(expected, relayed);

        // Bob answers by name, with whole bytes.
        await bob.Relay().SendToParticipantAsync(serverGuid, "alice", [1, 2, 3], 24, cancellationToken: Token);
        var answer = await NextRelayAsync(alice);
        var answerReader = new BitStreamReader(answer);
        answerReader.IgnoreBytes(2);
        Assert.True(answerReader.TryReadCompressedRakString(out var from));
        Assert.Equal("bob", from);
        answerReader.AlignToByte();
        Assert.Equal([1, 2, 3], answerReader.RemainingAlignedBytes[..3].ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Group_members_are_told_of_each_other_get_each_others_messages_and_see_the_groups(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (alice, bob) = await StartAsync(session, client, server, accept: true);
        var serverGuid = session.Server.Started.PeerGuid;
        await AddAsync(alice, "alice", serverGuid);
        await AddAsync(bob, "bob", serverGuid);

        // (J)oinGroupRequest: the joiner gets the members, itself last; the members are told first.
        await alice.Relay().JoinGroupAsync(serverGuid, "lobby", Token);
        Assert.Equal(Relay(RelayPluginMessage.JoinGroupSuccess, s => { s.WriteUInt16(1); Compressed(s, "alice"); }), await NextRelayAsync(alice));
        await bob.Relay().JoinGroupAsync(serverGuid, "lobby", Token);
        Assert.Equal(Relay(RelayPluginMessage.JoinGroupSuccess, s => { s.WriteUInt16(2); Compressed(s, "alice"); Compressed(s, "bob"); }), await NextRelayAsync(bob));
        Assert.Equal(Relay(RelayPluginMessage.UserEnteredRoom, s => Compressed(s, "bob")), await NextRelayAsync(alice));
        // Joining the group one is in fails.
        await bob.Relay().JoinGroupAsync(serverGuid, "lobby", Token);
        Assert.Equal(Relay(RelayPluginMessage.JoinGroupFailure, _ => { }), await NextRelayAsync(bob));

        // (S)endGroupMessage: the message is a RakString, to everyone in the group but the sender.
        var (text, textBits) = Bits(s =>
        {
            var writer = s.BeginWrite(32);
            writer.WriteRakString("hi lobby".AsSpan());
            s.EndWrite(writer);
        });
        await bob.Relay().SendGroupMessageAsync(serverGuid, text, textBits, cancellationToken: Token);
        Assert.Equal(Relay(RelayPluginMessage.GroupMsgFromServer, s => { Compressed(s, "bob"); s.AlignToByte(); s.WriteBytes(text); }), await NextRelayAsync(alice));

        // (G)etGroupList.
        await alice.Relay().GetGroupListAsync(serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.GetGroupListReplyFromServer, s => { s.WriteUInt16(1); Compressed(s, "lobby"); s.WriteUInt16(2); }), await NextRelayAsync(alice));

        // (L)eaveGroup tells the rest; a participant that disconnects leaves too.
        await bob.Relay().LeaveGroupAsync(serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.UserLeftRoom, s => Compressed(s, "bob")), await NextRelayAsync(alice));
        await bob.Relay().JoinGroupAsync(serverGuid, "lobby", Token);
        await NextRelayAsync(bob);
        Assert.Equal(Relay(RelayPluginMessage.UserEnteredRoom, s => Compressed(s, "bob")), await NextRelayAsync(alice));
        await bob.Driver.CloseAsync(serverGuid, notify: true, Token);
        Assert.Equal(Relay(RelayPluginMessage.UserLeftRoom, s => Compressed(s, "bob")), await NextRelayAsync(alice));
        Assert.DoesNotContain(bob.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.RelayPlugin && p.Data.Span[1] == (byte)RelayPluginMessage.GroupMsgFromServer);
    }

    private static async Task<(NatPeer Alice, NatPeer Bob)> StartAsync(NatSession session, PeerKind client, PeerKind server, bool accept)
    {
        await session.Server.Relay().AttachRelayAsync(accept, Token);
        var alice = await session.AddClientAsync(client, null, Token);
        var bob = await session.AddClientAsync(server, null, Token);
        await alice.Relay().AttachRelayAsync(false, Token);
        await bob.Relay().AttachRelayAsync(false, Token);
        await session.ConnectAsync(alice, Token);
        await session.ConnectAsync(bob, Token);
        return (alice, bob);
    }

    // (A)ddParticipantRequestFromClient, answered with success.
    private static async Task AddAsync(NatPeer peer, string name, ulong serverGuid)
    {
        await peer.Relay().AddParticipantRequestAsync(name, serverGuid, Token);
        Assert.Equal(Relay(RelayPluginMessage.AddClientSuccess, s => Compressed(s, name)), await NextRelayAsync(peer));
    }

    private static async Task<byte[]> NextRelayAsync(NatPeer peer) =>
        (await peer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RelayPlugin, "ID_RELAY_PLUGIN", Timeout, Token)).Data.ToArray();

    private static byte[] Relay(RelayPluginMessage message, Action<BitStream> body)
    {
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.RelayPlugin);
        stream.WriteByte((byte)message);
        body(stream);
        return stream.Written.ToArray();
    }

    private static (byte[] Data, int Bits) Bits(Action<BitStream> body)
    {
        using var stream = new BitStream();
        body(stream);
        return (stream.Written.ToArray(), stream.BitsWritten);
    }

    // BitStream::WriteCompressed of a RakString.
    private static void Compressed(BitStream stream, string text)
    {
        var writer = stream.BeginWrite(64);
        writer.WriteCompressedRakString(text.AsSpan());
        stream.EndWrite(writer);
    }

    private static void WriteBits(BitStream stream, byte[] data, int bits)
    {
        var reader = new BitStreamReader(data, bits);
        stream.Write(ref reader);
    }
}
