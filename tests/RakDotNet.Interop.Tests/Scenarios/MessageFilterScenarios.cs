using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// A MessageFilter on the server, from Samples/MessageFilter/MessageFilterTest.cpp: only the message IDs the sender's
/// filter set allows reach the server's packet stream, and a disallowed message or an outstayed filter set gets the
/// sender kicked or banned on the wire. Messages are sent RELIABLE_ORDERED on one channel, so once a later message has
/// arrived an earlier one that did not was dropped by the filter.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class MessageFilterScenarios
{
    private const byte User = ScenarioFlows.UserPacket;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Only_the_ids_the_senders_filter_set_allows_arrive_and_a_change_of_set_takes_effect(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var filter = (IMessageFilterDriver)session.Server;
        await filter.AttachMessageFilterAsync(Token);
        // MessageFilterTest.cpp:34-39.
        await filter.SetAutoAddNewConnectionsToFilterAsync(0, Token);
        await filter.SetAllowMessageIdAsync(true, User, User, 0, Token);
        await filter.SetAllowMessageIdAsync(true, User + 1, User + 1, 1, Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        Assert.Equal(0, await filter.GetSystemFilterSetAsync(clientGuid, Token));

        // MessageFilterTest.cpp:66-72: the disallowed message first, then the allowed one.
        await SendAsync(session, serverGuid, User + 1);
        await SendAsync(session, serverGuid, User);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == User, "the allowed ID_USER_PACKET_ENUM", Timeout, Token);
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == User + 1);

        // MessageFilterTest.cpp:88: the sender moves to filter set 1, where only ID_USER_PACKET_ENUM+1 is allowed.
        await filter.SetSystemFilterSetAsync(clientGuid, 1, Token);
        await SendAsync(session, serverGuid, User);
        await SendAsync(session, serverGuid, User + 1);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == User + 1, "the allowed ID_USER_PACKET_ENUM+1", Timeout, Token);
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == User);

        var info = await filter.GetMessageFilterInfoAsync(Token);
        Assert.Equal([0, 1], info.FilterSetIds);
        Assert.Equal(1, info.SystemCount);
        Assert.Equal([(clientGuid, 0, User + 1), (clientGuid, 1, User)], info.Disallowed.Select(e => (e.PeerGuid, e.FilterSetId, e.MessageId)));
        Assert.All(info.Disallowed, e => Assert.Equal(session.AddressAtServer(session.ClientPeer).ToString(), e.Address));

        // Connection notifications always pass (MessageFilter.cpp:301-316), whatever the filter set allows.
        await session.Client.CloseAsync(serverGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_disallowed_message_gets_its_sender_kicked_and_banned(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var filter = (IMessageFilterDriver)session.Server;
        await filter.AttachMessageFilterAsync(Token);
        await filter.SetAutoAddNewConnectionsToFilterAsync(0, Token);
        await filter.SetActionOnDisallowedMessageAsync(kick: true, ban: true, TimeSpan.FromMinutes(1), 0, Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        await SendAsync(session, serverGuid, User);

        // MessageFilter.cpp:251-256: CloseConnection(..., true, 0) notifies the sender.
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == User);
        var disallowed = Assert.Single((await filter.GetMessageFilterInfoAsync(Token)).Disallowed);
        Assert.Equal((clientGuid, 0, User), (disallowed.PeerGuid, disallowed.FilterSetId, disallowed.MessageId));

        // MessageFilter.cpp:245-250: the sender's IP is banned, so connecting again is refused.
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionBanned, "ID_CONNECTION_BANNED", Timeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_system_that_outstays_its_filter_set_is_kicked(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var filter = (IMessageFilterDriver)session.Server;
        await filter.AttachMessageFilterAsync(Token);
        await filter.SetAutoAddNewConnectionsToFilterAsync(0, Token);
        await filter.SetFilterMaxTimeAsync(TimeSpan.FromMilliseconds(500), ban: false, TimeSpan.Zero, 0, Token);
        var (_, clientGuid) = await session.ConnectAsync(Token);

        // The time limit is checked every 2 s (MessageFilter.cpp:262, 296).
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);

        var info = await filter.GetMessageFilterInfoAsync(Token);
        var timeout = Assert.Single(info.Timeouts);
        Assert.Equal((clientGuid, 0), (timeout.PeerGuid, timeout.FilterSetId));
        Assert.Equal(-1, await filter.GetSystemFilterSetAsync(clientGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_timestamped_message_is_filtered_on_the_byte_four_bytes_into_its_time(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var filter = (IMessageFilterDriver)session.Server;
        await filter.AttachMessageFilterAsync(Token);
        await filter.SetAutoAddNewConnectionsToFilterAsync(0, Token);
        await filter.SetAllowMessageIdAsync(true, User, User, 0, Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // MessageFilter.cpp:327 reads data[1 + sizeof(RakNet::TimeMS)], inside the 8-byte time. The receiver shifts the
        // time by the clock differential first (RakPeer.cpp:1549-1555); 0x80 below byte 5 absorbs any shift of hours.
        byte[] filteredOnItsTime = [(byte)MessageId.Timestamp, 0, 0, 0, 0, 1, 0x80, 0, 0, User];
        byte[] allowedByItsTime = [(byte)MessageId.Timestamp, 0, 0, 0, 0, User, 0x80, 0, 0, User + 1];
        await session.Client.SendAsync(new SendOptions(serverGuid, filteredOnItsTime), Token);
        await session.Client.SendAsync(new SendOptions(serverGuid, allowedByItsTime), Token);

        var arrived = await session.ServerInbox.WaitForPacketAsync(p => p.Timestamp is not null, "a timestamped message", Timeout, Token);
        Assert.Equal(User + 1, arrived.MessageId);
        var disallowed = Assert.Single((await filter.GetMessageFilterInfoAsync(Token)).Disallowed);
        // The callback is given the packet's first byte (MessageFilter.cpp:332).
        Assert.Equal((clientGuid, (int)MessageId.Timestamp), (disallowed.PeerGuid, disallowed.MessageId));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task An_rpc4_call_arrives_only_when_its_function_is_allowed(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var filter = (IMessageFilterDriver)session.Server;
        await filter.AttachMessageFilterAsync(Token);
        await filter.SetAutoAddNewConnectionsToFilterAsync(0, Token);
        await filter.SetAllowRpc4Async(true, "Allowed", 0, Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        // MessageFilter.cpp:336-347: the function name is the compressed RakString after two bytes.
        await session.Client.SendAsync(new SendOptions(serverGuid, Rpc4Call("Other")), Token);
        await session.Client.SendAsync(new SendOptions(serverGuid, Rpc4Call("Allowed")), Token);

        var arrived = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.RpcPlugin, "ID_RPC_PLUGIN", Timeout, Token);
        Assert.Equal(Rpc4Call("Allowed"), arrived.Data.ToArray());
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.RpcPlugin);
        Assert.Single((await filter.GetMessageFilterInfoAsync(Token)).Disallowed);
    }

    private static Task<uint> SendAsync(InteropSession session, ulong serverGuid, int messageId) =>
        session.Client.SendAsync(new SendOptions(serverGuid, new[] { (byte)messageId, (byte)0x2A }), Token);

    private static byte[] Rpc4Call(string function)
    {
        var writer = new BitStreamWriter(new byte[64]);
        writer.WriteByte((byte)MessageId.RpcPlugin);
        writer.WriteByte(0);
        writer.WriteCompressedRakString(function.AsSpan());
        return writer.Written.ToArray();
    }
}
