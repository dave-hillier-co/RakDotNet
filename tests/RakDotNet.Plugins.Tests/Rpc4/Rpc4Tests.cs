using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.Rpc4;

public sealed class Rpc4Tests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly Plugins.Rpc4 _serverRpc = new();
    private readonly Plugins.Rpc4 _clientRpc = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;
    private readonly List<(string Name, string Parameters, Rpc4Sender Sender)> _calls = [];

    public Rpc4Tests()
    {
        _server = _mesh.Add(maxIncoming: 4, _serverRpc);
        _client = _mesh.Add(maxIncoming: 4, _clientRpc);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_call_runs_the_function_registered_under_its_name_with_the_parameters_and_never_reaches_the_user()
    {
        _mesh.Connect(_client, _server);
        Assert.True(_serverRpc.RegisterFunction("CFunc1", Record("CFunc1")));
        using var parameters = CompressedString("testData");

        _clientRpc.Call("CFunc1", parameters, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false);
        _mesh.Settle();

        var (name, read, sender) = Assert.Single(_calls);
        Assert.Equal(("CFunc1", "testData"), (name, read));
        Assert.Equal(_client.RakNetGuid, sender.RakNetGuid);
        Assert.False(sender.IsLocal);
        // RPC4Plugin.cpp:841: ID_RPC_PLUGIN is always consumed.
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == (byte)MessageId.RpcPlugin);
    }

    [Fact]
    public void A_call_is_the_plugin_id_the_call_id_the_compressed_name_a_blocking_bit_then_the_aligned_parameters()
    {
        // The server has no RPC4, so the call reaches its user as sent (RPC4Plugin.cpp:561-575).
        using var mesh = new PluginMesh();
        var caller = new Plugins.Rpc4();
        var client = mesh.Add(maxIncoming: 4, caller);
        var server = mesh.Add(maxIncoming: 4);
        mesh.Connect(client, server);
        using var parameters = new BitStream();
        parameters.WriteByte(0xAB);

        caller.Call("F", parameters, Priority.High, Reliability.ReliableOrdered, 0, server.RakNetGuid, broadcast: false);
        mesh.Settle();

        using var expected = new BitStream();
        var writer = expected.BeginWrite(64);
        writer.WriteByte((byte)MessageId.RpcPlugin);
        writer.WriteByte(0);
        writer.WriteCompressedRakString("F");
        writer.WriteBit(false);
        writer.AlignToByte();
        writer.WriteByte(0xAB);
        expected.EndWrite(writer);
        Assert.Equal(expected.Written.ToArray(), mesh.Seen(server).Single(e => e.Id == (byte)MessageId.RpcPlugin).Data);
    }

    [Fact]
    public void A_call_to_an_unregistered_function_is_answered_with_the_remote_error_and_the_name()
    {
        _mesh.Connect(_client, _server);

        _clientRpc.Call("Missing", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false);
        _mesh.Settle();

        // RPC4Plugin.cpp:779-787: ID_RPC_REMOTE_ERROR, RPC_ERROR_FUNCTION_NOT_REGISTERED, the name and its terminator.
        var error = _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.RpcRemoteError);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, .. Encoding.ASCII.GetBytes("Missing"), 0], error.Data);
        Assert.Equal(_server.RakNetGuid, error.RakNetGuid);
    }

    [Fact]
    public void A_signal_runs_every_slot_highest_priority_first_then_in_registration_order()
    {
        _mesh.Connect(_client, _server);
        // RPC4Sample.cpp registers two slots for Event1.
        _serverRpc.RegisterSlot("Event1", Record("low"), 0);
        _serverRpc.RegisterSlot("Event1", Record("high"), 5);
        _serverRpc.RegisterSlot("Event1", Record("low again"), 0);
        using var parameters = CompressedString("testData");

        _clientRpc.Signal("Event1", parameters, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false, invokeLocal: false);
        _mesh.Settle();

        // Each slot reads the parameters from the start (RPC4Plugin.cpp:705-740).
        Assert.Equal([("high", "testData"), ("low", "testData"), ("low again", "testData")], _calls.Select(c => (c.Name, c.Parameters)));
    }

    [Fact]
    public void A_signal_invoked_locally_runs_this_systems_slots_at_once_as_generated_locally()
    {
        _mesh.Connect(_client, _server);
        _clientRpc.RegisterSlot("Event1", Record("client"), 0);
        _serverRpc.RegisterSlot("Event1", Record("server"), 0);
        using var parameters = CompressedString("both");

        _clientRpc.Signal("Event1", parameters, Priority.High, Reliability.ReliableOrdered, 0, default, broadcast: true, invokeLocal: true);

        var local = Assert.Single(_calls);
        Assert.Equal(("client", "both"), (local.Name, local.Parameters));
        Assert.Equal(_client.RakNetGuid, local.Sender.RakNetGuid);
        Assert.True(local.Sender.IsLocal);
        _mesh.Settle();
        Assert.Equal(("server", "both"), (_calls[1].Name, _calls[1].Parameters));
    }

    [Fact]
    public void InterruptSignal_stops_the_remaining_slots()
    {
        _mesh.Connect(_client, _server);
        _serverRpc.RegisterSlot("Event1", (_, _) => { _calls.Add(("first", "", default)); _serverRpc.InterruptSignal(); }, 1);
        _serverRpc.RegisterSlot("Event1", Record("second"), 0);

        _clientRpc.Signal("Event1", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false, invokeLocal: false);
        _clientRpc.Signal("Event1", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false, invokeLocal: false);
        _mesh.Settle();

        Assert.Equal(["first", "first"], _calls.Select(c => c.Name));
    }

    [Fact]
    public void A_signal_with_no_slots_does_nothing_and_is_consumed()
    {
        _mesh.Connect(_client, _server);

        _clientRpc.Signal("Nobody", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, broadcast: false, invokeLocal: true);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id is (byte)MessageId.RpcPlugin or (byte)MessageId.RpcRemoteError);
        Assert.DoesNotContain(_mesh.Seen(_client), e => e.Id is (byte)MessageId.RpcPlugin or (byte)MessageId.RpcRemoteError);
    }

    [Fact]
    public async Task A_blocking_call_returns_what_the_remote_function_wrote()
    {
        // RPC4Sample.cpp: CFunc3 reads the parameter and returns "CFunc3 returnData".
        _mesh.Connect(_client, _server);
        _serverRpc.RegisterBlockingFunction("Blocking", (parameters, returnData, sender) =>
        {
            _calls.Add(("Blocking", ReadCompressedString(ref parameters), sender));
            WriteCompressedString(returnData, "CFunc3 returnData");
        });
        using var parameters = CompressedString("testData");
        using var returnData = new BitStream();

        var call = _clientRpc.CallBlockingAsync("Blocking", parameters, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken);
        _mesh.RunUntil(() => call.IsCompleted, "the blocking call returned");

        Assert.True(await call);
        var reader = returnData.CreateReader();
        Assert.Equal("CFunc3 returnData", ReadCompressedString(ref reader));
        Assert.Equal(("Blocking", "testData"), (_calls[0].Name, _calls[0].Parameters));
        Assert.DoesNotContain(_mesh.Seen(_client), e => e.Id == (byte)MessageId.RpcPlugin);
    }

    [Fact]
    public async Task A_blocking_call_to_a_system_that_is_not_connected_returns_false_at_once()
    {
        using var returnData = new BitStream();

        Assert.False(await _clientRpc.CallBlockingAsync("Blocking", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_blocking_call_returns_false_when_the_target_disconnects()
    {
        var connection = _mesh.Connect(_client, _server);
        using var returnData = new BitStream();
        // The server has no blocking function and RakNet's answer is an ID_RPC_REMOTE_ERROR; holding it back leaves the
        // call waiting, as it would on a slow function.
        _serverRpc.RegisterBlockingFunction("Slow", (_, _, _) => _client.Close(_server.RakNetGuid, notify: false));

        var call = _clientRpc.CallBlockingAsync("Slow", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken);
        _mesh.RunUntil(() => call.IsCompleted, "the blocking call ended");

        Assert.False(await call);
        Assert.Equal(ConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task A_blocking_call_to_an_unregistered_function_returns_false_on_the_remote_error()
    {
        _mesh.Connect(_client, _server);
        using var returnData = new BitStream();

        var call = _clientRpc.CallBlockingAsync("Missing", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken);
        _mesh.RunUntil(() => call.IsCompleted, "the blocking call ended");

        Assert.False(await call);
        // The error still reaches the user, as RakNet pushes it back (RPC4Plugin.cpp:624-637).
        Assert.Contains(_mesh.Seen(_client), e => e.Id == (byte)MessageId.RpcRemoteError);
    }

    [Fact]
    public void A_second_blocking_call_while_one_is_waiting_is_refused()
    {
        _mesh.Connect(_client, _server);
        using var returnData = new BitStream();
        _ = _clientRpc.CallBlockingAsync("A", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken);

        // CallBlockingAsync refuses synchronously, before returning a task.
        Assert.Throws<InvalidOperationException>(() => { _ = _clientRpc.CallBlockingAsync("B", null, Priority.High, Reliability.ReliableOrdered, 0, _server.RakNetGuid, returnData, TestContext.Current.CancellationToken); });
    }

    [Fact]
    public void CallLoopback_runs_a_local_function_through_the_event_stream()
    {
        _clientRpc.RegisterFunction("Local", Record("Local"));
        using var parameters = CompressedString("self");

        _clientRpc.CallLoopback("Local", parameters);
        Assert.Empty(_calls);
        _mesh.Step();

        var (name, read, sender) = Assert.Single(_calls);
        Assert.Equal(("Local", "self"), (name, read));
        Assert.Equal(_client.RakNetGuid, sender.RakNetGuid);
    }

    [Fact]
    public void CallLoopback_of_an_unregistered_function_queues_the_remote_error_locally()
    {
        _clientRpc.CallLoopback("Missing", null);
        _mesh.Step();

        // RPC4Plugin.cpp:504-528: from this system's GUID and UNASSIGNED_SYSTEM_ADDRESS.
        var error = _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.RpcRemoteError);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, .. Encoding.ASCII.GetBytes("Missing"), 0], error.Data);
        Assert.Equal(_client.RakNetGuid, error.RakNetGuid);
        Assert.Equal(SystemAddress.Unassigned, error.Address);
    }

    [Fact]
    public void A_local_callback_runs_registered_functions_on_a_message_id_with_the_whole_packet()
    {
        _mesh.Connect(_client, _server);
        _serverRpc.RegisterFunction("b", (parameters, sender) => _calls.Add(("b", Convert.ToHexStringLower(Rest(ref parameters)), sender)));
        _serverRpc.RegisterFunction("a", (parameters, sender) => _calls.Add(("a", Convert.ToHexStringLower(Rest(ref parameters)), sender)));
        _serverRpc.RegisterLocalCallback("b", (byte)MessageId.UserPacketEnum);
        _serverRpc.RegisterLocalCallback("a", (byte)MessageId.UserPacketEnum);
        _serverRpc.RegisterLocalCallback("not registered", (byte)MessageId.UserPacketEnum);

        _client.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1, 2 }, _server.RakNetGuid, Reliability.ReliableOrdered);
        _mesh.Settle();

        // In name order (RPC4Plugin.cpp:427-449, 844-864); the message continues to the user.
        Assert.Equal([("a", "860102"), ("b", "860102")], _calls.Select(c => (c.Name, c.Parameters)));
        Assert.Contains(_mesh.Seen(_server), e => e.Id == (byte)MessageId.UserPacketEnum);

        // True only for the last name of an ID (RPC4Plugin.cpp:460-484).
        Assert.False(_serverRpc.UnregisterLocalCallback("a", (byte)MessageId.UserPacketEnum));
        Assert.False(_serverRpc.UnregisterLocalCallback("b", (byte)MessageId.UserPacketEnum));
        Assert.True(_serverRpc.UnregisterLocalCallback("not registered", (byte)MessageId.UserPacketEnum));
        Assert.False(_serverRpc.UnregisterLocalCallback("a", (byte)MessageId.UserPacketEnum));
    }

    [Fact]
    public void Registering_a_name_twice_is_refused_and_unregistering_frees_it()
    {
        Assert.True(_serverRpc.RegisterFunction("F", Record("F")));
        Assert.False(_serverRpc.RegisterFunction("F", Record("F")));
        Assert.True(_serverRpc.UnregisterFunction("F"));
        Assert.False(_serverRpc.UnregisterFunction("F"));
        Assert.True(_serverRpc.RegisterFunction("F", Record("F")));

        Assert.True(_serverRpc.RegisterBlockingFunction("F", (_, _, _) => { }));
        Assert.False(_serverRpc.RegisterBlockingFunction("F", (_, _, _) => { }));
        Assert.True(_serverRpc.UnregisterBlockingFunction("F"));

        _serverRpc.RegisterSlot("S", Record("S"), 0);
        Assert.True(_serverRpc.UnregisterSlot("S"));
        Assert.False(_serverRpc.UnregisterSlot("S"));
    }

    [Fact]
    public void A_one_byte_rpc_message_is_consumed_and_runs_nothing()
    {
        _mesh.Connect(_client, _server);
        _serverRpc.RegisterFunction("CFunc1", Record("CFunc1"));

        // RakNet reads its sub-ID past the end of the message (RPC4Plugin.cpp:520-526).
        FromClient([(byte)MessageId.RpcPlugin]);
        _mesh.Settle();

        Assert.Empty(_calls);
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == (byte)MessageId.RpcPlugin);
        Assert.DoesNotContain(_mesh.Seen(_client), e => e.Id == (byte)MessageId.RpcRemoteError);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("ffffffffffffffff")]
    public void A_call_whose_name_cannot_be_read_is_answered_as_an_unregistered_empty_name(string nameBytes)
    {
        _mesh.Connect(_client, _server);
        _serverRpc.RegisterFunction("CFunc1", Record("CFunc1"));

        FromClient([(byte)MessageId.RpcPlugin, 0, .. Convert.FromHexString(nameBytes)]);
        _mesh.Settle();

        // The name RakString::DeserializeCompressed leaves when it fails is empty, and no function has it.
        Assert.Empty(_calls);
        var error = _mesh.Seen(_client).Single(e => e.Id == (byte)MessageId.RpcRemoteError);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, 0], error.Data);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(255)]
    public void A_return_or_unknown_sub_id_with_no_blocking_call_waiting_is_consumed_and_does_nothing(byte subId)
    {
        _mesh.Connect(_client, _server);

        FromServer([(byte)MessageId.RpcPlugin, subId, 1, 2, 3]);
        _mesh.Settle();

        Assert.DoesNotContain(_mesh.Seen(_client), e => e.Id is (byte)MessageId.RpcPlugin or (byte)MessageId.RpcRemoteError);
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id is (byte)MessageId.RpcPlugin or (byte)MessageId.RpcRemoteError);
    }

    [Fact]
    public async Task An_unknown_sub_id_while_a_blocking_call_waits_is_taken_as_its_return()
    {
        // The server has no RPC4, so the call is never answered but by the injected message (RPC4Plugin.cpp:589-595).
        using var mesh = new PluginMesh();
        var caller = new Plugins.Rpc4();
        var client = mesh.Add(maxIncoming: 4, caller);
        var server = mesh.Add(maxIncoming: 4);
        var connection = mesh.Connect(client, server);
        using var returnData = new BitStream();
        var call = caller.CallBlockingAsync("F", null, Priority.High, Reliability.ReliableOrdered, 0, server.RakNetGuid, returnData, TestContext.Current.CancellationToken);
        mesh.Settle();
        Assert.False(call.IsCompleted);

        client.Inject([(byte)MessageId.RpcPlugin, 7, 0xAB], connection.Address, connection.RakNetGuid);
        mesh.RunUntil(() => call.IsCompleted, "the blocking call returned");

        Assert.True(await call);
        Assert.Equal([0xAB], returnData.Written.ToArray());
    }

    private void FromClient(byte[] message)
    {
        var client = _server.Connections.Single();
        _server.Inject(message, client.Address, client.RakNetGuid);
    }

    private void FromServer(byte[] message)
    {
        var server = _client.Connections.Single();
        _client.Inject(message, server.Address, server.RakNetGuid);
    }

    private Rpc4Function Record(string name) => (parameters, sender) => _calls.Add((name, ReadCompressedString(ref parameters), sender));

    private static BitStream CompressedString(string value)
    {
        var stream = new BitStream();
        WriteCompressedString(stream, value);
        return stream;
    }

    private static void WriteCompressedString(BitStream stream, string value)
    {
        var writer = stream.BeginWrite(256);
        writer.WriteCompressedRakString(value);
        stream.EndWrite(writer);
    }

    private static string ReadCompressedString(ref BitStreamReader reader) =>
        reader.TryReadCompressedRakString(out var value) ? value : "<unreadable>";

    private static byte[] Rest(ref BitStreamReader reader)
    {
        var bytes = new byte[reader.BitsRemaining / 8];
        reader.TryReadBytes(bytes);
        return bytes;
    }
}
