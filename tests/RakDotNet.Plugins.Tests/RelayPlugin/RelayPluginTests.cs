using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.RelayPlugin;

/// <summary>
/// RelayPlugin, from Samples/RelayPluginTest: a server that accepts participants and clients that add themselves,
/// send to each other by name, and join, message and leave groups.
/// </summary>
public sealed class RelayPluginTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly Plugins.RelayPlugin _serverPlugin = new();
    private readonly RakPeer _server;
    private readonly List<(RakPeer Peer, Plugins.RelayPlugin Plugin)> _clients = [];

    public RelayPluginTests()
    {
        _server = _mesh.Add(4, _serverPlugin);
        for (var i = 0; i < 3; i++)
        {
            var plugin = new Plugins.RelayPlugin();
            var client = _mesh.Add(0, plugin);
            _mesh.Connect(client, _server);
            _clients.Add((client, plugin));
        }
        _mesh.Settle();
    }

    public void Dispose() => _mesh.Dispose();

    private (RakPeer Peer, Plugins.RelayPlugin Plugin) Alice => _clients[0];

    private (RakPeer Peer, Plugins.RelayPlugin Plugin) Bob => _clients[1];

    private (RakPeer Peer, Plugins.RelayPlugin Plugin) Carol => _clients[2];

    [Fact]
    public void Without_accepting_requests_the_server_refuses_them_with_the_name()
    {
        Alice.Plugin.AddParticipantRequestFromClient("alice", _server.RakNetGuid);

        var reply = WaitForRelay(Alice.Peer, RelayPluginMessage.AddClientNotAllowed);
        Assert.Equal("alice", ReadCompressed(reply, 2, out _));
    }

    [Fact]
    public void An_accepted_request_adds_the_participant_and_a_taken_name_is_refused()
    {
        _serverPlugin.AcceptAddParticipantRequests = true;

        Alice.Plugin.AddParticipantRequestFromClient("alice", _server.RakNetGuid);
        Assert.Equal("alice", ReadCompressed(WaitForRelay(Alice.Peer, RelayPluginMessage.AddClientSuccess), 2, out _));
        Bob.Plugin.AddParticipantRequestFromClient("alice", _server.RakNetGuid);
        Assert.Equal("alice", ReadCompressed(WaitForRelay(Bob.Peer, RelayPluginMessage.AddClientNameAlreadyInUse), 2, out _));
    }

    [Fact]
    public void Only_a_connected_system_can_be_added_on_the_server()
    {
        Assert.Equal(RelayPluginMessage.AddClientTargetNotConnected, _serverPlugin.AddParticipantOnServer("x", new RakNetGuid(99)));
        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("x", Alice.Peer.RakNetGuid));
        Assert.Equal(RelayPluginMessage.AddClientNameAlreadyInUse, _serverPlugin.AddParticipantOnServer("x", Bob.Peer.RakNetGuid));
    }

    [Fact]
    public void A_message_to_a_participant_arrives_with_the_sender_name_and_every_bit_after_the_name()
    {
        _serverPlugin.AddParticipantOnServer("alice", Alice.Peer.RakNetGuid);
        _serverPlugin.AddParticipantOnServer("bob", Bob.Peer.RakNetGuid);

        // Samples/RelayPluginTest: the message is a compressed RakString.
        using var data = new BitStream();
        var writer = data.BeginWrite(64);
        writer.WriteCompressedRakString("hello bob".AsSpan());
        data.EndWrite(writer);
        Alice.Plugin.SendToParticipant(_server.RakNetGuid, "bob", data, Priority.High, Reliability.ReliableOrdered, 0);

        var relayed = WaitForRelay(Bob.Peer, RelayPluginMessage.MessageToClientFromServer);
        Assert.Equal("alice", ReadCompressed(relayed, 2, out var afterName));
        var reader = new BitStreamReader(relayed);
        reader.IgnoreBits(afterName);
        reader.AlignToByte();
        Assert.True(reader.TryReadCompressedRakString(out var text));
        Assert.Equal("hello bob", text);
        // The relayed bits run to the end of the request's last byte, padding included (RelayPlugin.cpp:177).
        var requestBits = 16 + 24 + KeyBits("bob") + data.BitsWritten;
        var padding = (8 - (requestBits % 8)) % 8;
        var expectedBits = (((16 + KeyBits("alice") + 7) / 8) * 8) + data.BitsWritten + padding;
        Assert.Equal((expectedBits + 7) / 8, relayed.Length);
    }

    [Fact]
    public void Messages_from_a_system_that_is_not_a_participant_or_to_an_unknown_name_are_dropped()
    {
        _serverPlugin.AddParticipantOnServer("bob", Bob.Peer.RakNetGuid);
        Alice.Plugin.SendToParticipant(_server.RakNetGuid, "bob", [1, 2, 3], Priority.High, Reliability.ReliableOrdered, 0);
        _mesh.Settle();
        _serverPlugin.AddParticipantOnServer("alice", Alice.Peer.RakNetGuid);
        Alice.Plugin.SendToParticipant(_server.RakNetGuid, "nobody", [1, 2, 3], Priority.High, Reliability.ReliableOrdered, 0);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(Bob.Peer), e => e.Id == (byte)MessageId.RelayPlugin);
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == (byte)MessageId.RelayPlugin);
    }

    [Fact]
    public void Joining_a_group_tells_its_members_and_lists_them_to_the_joiner()
    {
        AddEveryone();

        Alice.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        var aliceJoined = WaitForRelay(Alice.Peer, RelayPluginMessage.JoinGroupSuccess);
        Bob.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        var bobJoined = WaitForRelay(Bob.Peer, RelayPluginMessage.JoinGroupSuccess);

        Assert.Equal(["alice"], Names(aliceJoined));
        Assert.Equal(["alice", "bob"], Names(bobJoined));
        Assert.Equal("bob", ReadCompressed(WaitForRelay(Alice.Peer, RelayPluginMessage.UserEnteredRoom), 2, out _));
        // Joining the group one is in fails.
        Bob.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        WaitForRelay(Bob.Peer, RelayPluginMessage.JoinGroupFailure);
    }

    [Fact]
    public void A_group_message_goes_to_everyone_else_in_the_group()
    {
        AddEveryone();
        foreach (var (peer, plugin) in _clients)
        {
            plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
            WaitForRelay(peer, RelayPluginMessage.JoinGroupSuccess);
        }

        // Samples/RelayPluginTest: the message is a RakString.
        using var data = new BitStream();
        var writer = data.BeginWrite(64);
        writer.WriteRakString("hi all".AsSpan());
        data.EndWrite(writer);
        Alice.Plugin.SendGroupMessage(_server.RakNetGuid, data, Priority.High, Reliability.ReliableOrdered, 0);

        foreach (var (peer, _) in new[] { Bob, Carol })
        {
            var message = WaitForRelay(peer, RelayPluginMessage.GroupMsgFromServer);
            Assert.Equal("alice", ReadCompressed(message, 2, out var afterName));
            var reader = new BitStreamReader(message);
            reader.IgnoreBits(afterName);
            reader.AlignToByte();
            Assert.True(reader.TryReadRakString(out var text));
            Assert.Equal("hi all", text);
        }
        Assert.DoesNotContain(_mesh.Seen(Alice.Peer), e => e.Id == (byte)MessageId.RelayPlugin && e.Data[1] == (byte)RelayPluginMessage.GroupMsgFromServer);
    }

    [Fact]
    public void Leaving_tells_the_rest_and_the_last_to_leave_removes_the_group()
    {
        AddEveryone();
        Alice.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        WaitForRelay(Alice.Peer, RelayPluginMessage.JoinGroupSuccess);
        Bob.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        WaitForRelay(Bob.Peer, RelayPluginMessage.JoinGroupSuccess);
        Carol.Plugin.JoinGroupRequest(_server.RakNetGuid, "chess");
        WaitForRelay(Carol.Peer, RelayPluginMessage.JoinGroupSuccess);

        Carol.Plugin.GetGroupList(_server.RakNetGuid);
        Assert.Equal([("lobby", 2), ("chess", 1)], Groups(WaitForRelay(Carol.Peer, RelayPluginMessage.GetGroupListReplyFromServer)));

        Bob.Plugin.LeaveGroup(_server.RakNetGuid);
        Assert.Equal("bob", ReadCompressed(WaitForRelay(Alice.Peer, RelayPluginMessage.UserLeftRoom), 2, out _));
        Alice.Plugin.LeaveGroup(_server.RakNetGuid);
        _mesh.Settle();

        Carol.Plugin.GetGroupList(_server.RakNetGuid);
        _mesh.RunUntil(() => RelayMessages(Carol.Peer, RelayPluginMessage.GetGroupListReplyFromServer).Count() == 2, "the second list");
        Assert.Equal([("chess", 1)], Groups(RelayMessages(Carol.Peer, RelayPluginMessage.GetGroupListReplyFromServer).Last()));
    }

    [Fact]
    public void A_participant_that_disconnects_is_removed_and_its_group_told()
    {
        AddEveryone();
        Alice.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        WaitForRelay(Alice.Peer, RelayPluginMessage.JoinGroupSuccess);
        Bob.Plugin.JoinGroupRequest(_server.RakNetGuid, "lobby");
        WaitForRelay(Bob.Peer, RelayPluginMessage.JoinGroupSuccess);

        Bob.Peer.Close(_server.RakNetGuid, notify: true);

        Assert.Equal("bob", ReadCompressed(WaitForRelay(Alice.Peer, RelayPluginMessage.UserLeftRoom), 2, out _));
        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("bob", Carol.Peer.RakNetGuid));
    }

    [Fact]
    public void Removing_a_participant_on_request_frees_its_name()
    {
        AddEveryone();

        Alice.Plugin.RemoveParticipantRequestFromClient(_server.RakNetGuid);
        _mesh.Settle();

        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("alice", Carol.Peer.RakNetGuid));
    }

    private void AddEveryone()
    {
        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("alice", Alice.Peer.RakNetGuid));
        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("bob", Bob.Peer.RakNetGuid));
        Assert.Equal(RelayPluginMessage.AddClientSuccess, _serverPlugin.AddParticipantOnServer("carol", Carol.Peer.RakNetGuid));
    }

    private IEnumerable<byte[]> RelayMessages(RakPeer peer, RelayPluginMessage message) =>
        _mesh.Seen(peer).Where(e => e.Data.Length > 1 && e.Id == (byte)MessageId.RelayPlugin && e.Data[1] == (byte)message).Select(e => e.Data);

    private byte[] WaitForRelay(RakPeer peer, RelayPluginMessage message)
    {
        _mesh.RunUntil(() => RelayMessages(peer, message).Any(), message.ToString());
        return RelayMessages(peer, message).First();
    }

    private static string ReadCompressed(byte[] data, int byteOffset, out int bitsAfter)
    {
        var reader = new BitStreamReader(data);
        reader.IgnoreBytes(byteOffset);
        Assert.True(reader.TryReadCompressedRakString(out var text));
        bitsAfter = (data.Length * 8) - reader.BitsRemaining;
        return text;
    }

    private static int KeyBits(string key)
    {
        using var stream = new BitStream();
        var writer = stream.BeginWrite(64);
        writer.WriteCompressedRakString(key.AsSpan());
        stream.EndWrite(writer);
        return stream.BitsWritten;
    }

    private static List<string> Names(byte[] joined)
    {
        var reader = new BitStreamReader(joined);
        reader.IgnoreBytes(2);
        Assert.True(reader.TryReadUInt16(out var count));
        var names = new List<string>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(reader.TryReadCompressedRakString(out var name));
            names.Add(name);
        }
        return names;
    }

    private static List<(string Name, int Users)> Groups(byte[] list)
    {
        var reader = new BitStreamReader(list);
        reader.IgnoreBytes(2);
        Assert.True(reader.TryReadUInt16(out var count));
        var groups = new List<(string, int)>();
        for (var i = 0; i < count; i++)
        {
            Assert.True(reader.TryReadCompressedRakString(out var name));
            Assert.True(reader.TryReadUInt16(out var users));
            groups.Add((name, users));
        }
        return groups;
    }
}
