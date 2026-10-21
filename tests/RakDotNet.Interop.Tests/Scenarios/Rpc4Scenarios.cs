using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// RPC4 on both systems, from Samples/RPC4/RPC4Sample.cpp: two slots for Event1 and a blocking function returning a
/// compressed string, plus what the sample leaves interactive or commented out: signalling, plain calls and the remote
/// error, the messages as a system without RPC4 receives them, CallLoopback and local callbacks.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class Rpc4Scenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_signal_runs_the_slots_of_the_other_system_by_priority_and_of_this_one_when_asked(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientRpc, serverRpc) = await AttachAsync(session);
        var (_, clientGuid) = await session.ConnectAsync(Token);
        // RPC4Sample.cpp registers CFunc1 and CFunc2 as slots of Event1 at priority 0; a third at a higher priority runs first.
        var cFunc1 = await serverRpc.RegisterRpc4SlotAsync("Event1", 0, cancellationToken: Token);
        var cFunc2 = await serverRpc.RegisterRpc4SlotAsync("Event1", 0, cancellationToken: Token);
        var first = await serverRpc.RegisterRpc4SlotAsync("Event1", 5, cancellationToken: Token);
        var local = await clientRpc.RegisterRpc4SlotAsync("Event1", 0, cancellationToken: Token);
        var testData = CompressedString("testData");

        // The sample's commented-out Signal, broadcast and invoked locally.
        await clientRpc.Rpc4SignalAsync("Event1", testData, new Rpc4Send(Target: null, Broadcast: true), invokeLocal: true, Token);

        var invocations = await WaitForInvocationsAsync(serverRpc, 3);
        Assert.Equal([first, cFunc1, cFunc2], invocations.Select(i => i.Handler));
        Assert.All(invocations, i =>
        {
            Assert.Equal(clientGuid, i.PeerGuid);
            Assert.False(i.Local);
            // Each slot reads the parameters from the start (RPC4Plugin.cpp:705-740), to the end of the message's bytes.
            Assert.Equal("testData", ReadCompressedString(i.Parameters));
            Assert.Equal(Padded(testData), i.Parameters);
        });
        // RPC4Plugin.cpp:668-703: the local slots run inside Signal, with the caller's own bits and generated locally.
        var self = Assert.Single(await clientRpc.GetRpc4InvocationsAsync(Token));
        Assert.Equal((local, clientGuid, true), (self.Handler, self.PeerGuid, self.Local));
        Assert.Equal(testData, self.Parameters);
        AssertNoRpcMessages(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_slot_that_interrupts_the_signal_stops_the_slots_after_it(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientRpc, serverRpc) = await AttachAsync(session);
        var (serverGuid, _) = await session.ConnectAsync(Token);
        var interrupting = await serverRpc.RegisterRpc4SlotAsync("Event1", 1, interrupt: true, Token);
        await serverRpc.RegisterRpc4SlotAsync("Event1", 0, cancellationToken: Token);
        var other = await serverRpc.RegisterRpc4SlotAsync("Event2", 0, cancellationToken: Token);

        await clientRpc.Rpc4SignalAsync("Event1", null, new Rpc4Send(serverGuid), invokeLocal: false, Token);
        await clientRpc.Rpc4SignalAsync("Event2", null, new Rpc4Send(serverGuid), invokeLocal: false, Token);

        var invocations = await WaitForInvocationsAsync(serverRpc, 2);
        Assert.Equal([interrupting, other], invocations.Select(i => i.Handler));
        // Without parameters a signal is the name alone; the slots read from the next byte, so nothing is left.
        Assert.All(invocations, i => Assert.Equal(0, i.Parameters.Bits));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_blocking_call_returns_what_the_remote_function_wrote(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientRpc, serverRpc) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        // RPC4Sample.cpp's CFunc3 returns WriteCompressed("CFunc3 returnData").
        var blocking = await serverRpc.RegisterRpc4BlockingAsync("Blocking", CompressedString("CFunc3 returnData"), Token);

        var (returned, returnData) = await clientRpc.Rpc4CallBlockingAsync("Blocking", CompressedString("testData"), new Rpc4Send(serverGuid), Token);

        Assert.True(returned);
        Assert.Equal("CFunc3 returnData", ReadCompressedString(returnData));
        // The return is ID_RPC_PLUGIN, ID_RPC4_RETURN and the returned bits, read back to the end of its bytes.
        Assert.Equal(Padded(CompressedString("CFunc3 returnData")), returnData);
        var call = Assert.Single(await serverRpc.GetRpc4InvocationsAsync(Token));
        Assert.Equal((blocking!.Value, clientGuid, "testData"), (call.Handler, call.PeerGuid, ReadCompressedString(call.Parameters)));
        AssertNoRpcMessages(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_call_runs_the_named_function_and_an_unknown_name_is_answered_with_the_remote_error(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientRpc, serverRpc) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var function = await serverRpc.RegisterRpc4FunctionAsync("CFunc1", Token);
        Assert.Null(await serverRpc.RegisterRpc4FunctionAsync("CFunc1", Token));

        await clientRpc.Rpc4CallAsync("CFunc1", CompressedString("testData"), new Rpc4Send(serverGuid), Token);
        await clientRpc.Rpc4CallAsync("Missing", null, new Rpc4Send(serverGuid), Token);

        // RPC4Plugin.cpp:779-787: the error, the code, then the name and its terminating zero.
        var error = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RpcRemoteError, "ID_RPC_REMOTE_ERROR", Timeout, Token);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, .. Encoding.ASCII.GetBytes("Missing"), 0], error.Data.ToArray());
        Assert.Equal(serverGuid, error.PeerGuid);
        var call = Assert.Single(await WaitForInvocationsAsync(serverRpc, 1));
        Assert.Equal((function!.Value, clientGuid, "testData"), (call.Handler, call.PeerGuid, ReadCompressedString(call.Parameters)));

        // A blocking call to a name with no blocking function: RakNet's CallBlocking misreads the error and waits for a
        // disconnect (RPC4Plugin.cpp:624-637), so only the error is checked here, as the client receives it.
        Assert.True(await serverRpc.UnregisterRpc4Async(Rpc4Registration.Function, "CFunc1", cancellationToken: Token));
        Assert.False(await serverRpc.UnregisterRpc4Async(Rpc4Registration.Function, "CFunc1", cancellationToken: Token));
        await clientRpc.Rpc4CallAsync("CFunc1", null, new Rpc4Send(serverGuid), Token);
        var second = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RpcRemoteError, "ID_RPC_REMOTE_ERROR for CFunc1", Timeout, Token);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, .. Encoding.ASCII.GetBytes("CFunc1"), 0], second.Data.ToArray());
        AssertNoRpcMessages(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_system_without_RPC4_receives_calls_and_signals_as_RakNet_writes_them(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var clientRpc = (IRpc4Driver)session.Client;
        await clientRpc.AttachRpc4Async(Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);
        var parameters = new Rpc4Bits([0xAB, 0xC0], 10);

        await clientRpc.Rpc4CallAsync("F", parameters, new Rpc4Send(serverGuid), Token);
        await clientRpc.Rpc4SignalAsync("Event1", parameters, new Rpc4Send(serverGuid), invokeLocal: false, Token);
        await clientRpc.Rpc4CallAsync("G", null, new Rpc4Send(serverGuid), Token);

        var messages = await session.ServerInbox.WaitForPacketsAsync(p => p.MessageId == (byte)MessageId.RpcPlugin, 3, "three ID_RPC_PLUGIN messages", Timeout, Token);
        // RPC4Plugin.cpp:561-575, 654-666: ID_RPC_PLUGIN, the sub-ID, the compressed name, a call's blocking bit, then the
        // parameters from the next byte; the native-native run shows these are RakNet's bytes.
        Assert.Equal(Message(0, "F", isBlocking: false, parameters), messages[0].Data.ToArray());
        Assert.Equal(Message(2, "Event1", isBlocking: null, parameters), messages[1].Data.ToArray());
        Assert.Equal(Message(0, "G", isBlocking: false, parameters: null), messages[2].Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task CallLoopback_and_local_callbacks_run_functions_on_this_system(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (_, serverRpc) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var local = await serverRpc.RegisterRpc4FunctionAsync("Local", Token);
        var onUser = await serverRpc.RegisterRpc4FunctionAsync("OnUser", Token);
        const byte userId = (byte)MessageId.UserPacketEnum + 1;
        await serverRpc.RegisterRpc4LocalCallbackAsync("OnUser", userId, Token);

        // RPC4Plugin.cpp:498-560: through the server's own event stream, from its GUID and no address.
        await serverRpc.Rpc4CallLoopbackAsync("Local", CompressedString("self"), Token);
        await serverRpc.Rpc4CallLoopbackAsync("Missing", null, Token);
        var error = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RpcRemoteError, "ID_RPC_REMOTE_ERROR from CallLoopback", Timeout, Token);
        Assert.Equal([(byte)MessageId.RpcRemoteError, 0, .. Encoding.ASCII.GetBytes("Missing"), 0], error.Data.ToArray());
        Assert.Equal(serverGuid, error.PeerGuid);

        // RPC4Plugin.cpp:844-864: a message with a registered ID runs the function with the whole message and goes on.
        await session.Client.SendAsync(new SendOptions(serverGuid, new byte[] { userId, 1, 2, 3 }), Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == userId, "the user message", Timeout, Token);

        var invocations = await WaitForInvocationsAsync(serverRpc, 2);
        Assert.Equal((local!.Value, serverGuid, "", "self"), (invocations[0].Handler, invocations[0].PeerGuid, invocations[0].Address, ReadCompressedString(invocations[0].Parameters)));
        Assert.Equal((onUser!.Value, clientGuid), (invocations[1].Handler, invocations[1].PeerGuid));
        Assert.Equal(new Rpc4Bits([userId, 1, 2, 3], 32), invocations[1].Parameters);
        Assert.True(await serverRpc.UnregisterRpc4Async(Rpc4Registration.LocalCallback, "OnUser", userId, Token));
    }

    private static async Task<(IRpc4Driver Client, IRpc4Driver Server)> AttachAsync(InteropSession session)
    {
        var client = (IRpc4Driver)session.Client;
        var server = (IRpc4Driver)session.Server;
        await client.AttachRpc4Async(Token);
        await server.AttachRpc4Async(Token);
        return (client, server);
    }

    private static async Task<IReadOnlyList<Rpc4Invocation>> WaitForInvocationsAsync(IRpc4Driver driver, int count)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var invocations = await driver.GetRpc4InvocationsAsync(Token);
            if (invocations.Count >= count)
            {
                Assert.Equal(count, invocations.Count);
                return invocations;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Only {invocations.Count} of {count} RPC4 invocations arrived.");
            await Task.Delay(20, Token);
        }
    }

    // RPC4Plugin.cpp:841: every ID_RPC_PLUGIN message is consumed by the plugin.
    private static void AssertNoRpcMessages(InteropSession session)
    {
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.RpcPlugin);
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId == (byte)MessageId.RpcPlugin);
    }

    private static Rpc4Bits CompressedString(string value)
    {
        using var stream = new BitStream();
        var writer = stream.BeginWrite(256);
        writer.WriteCompressedRakString(value);
        stream.EndWrite(writer);
        return Rpc4Bits.From(stream);
    }

    private static string ReadCompressedString(Rpc4Bits bits)
    {
        var reader = bits.CreateReader();
        return reader.TryReadCompressedRakString(out var value) ? value : "<unreadable>";
    }

    // A receiver reads to the end of the message's bytes (BitStream bsIn(packet->data, packet->length, false)).
    private static Rpc4Bits Padded(Rpc4Bits bits) => bits with { Bits = bits.Data.Length * 8 };

    private static byte[] Message(byte subId, string name, bool? isBlocking, Rpc4Bits? parameters)
    {
        using var stream = new BitStream();
        var writer = stream.BeginWrite(256);
        writer.WriteByte((byte)MessageId.RpcPlugin);
        writer.WriteByte(subId);
        writer.WriteCompressedRakString(name);
        if (isBlocking is { } blocking)
            writer.WriteBit(blocking);
        if (parameters is not null)
        {
            writer.AlignToByte();
            writer.WriteBits(parameters.Data, parameters.Bits, rightAlignedBits: false);
        }
        stream.EndWrite(writer);
        return stream.Written.ToArray();
    }
}
