using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// ReadyEvent on both systems, from Samples/ReadyEvent/ReadyEventSample.cpp: each system waits on the systems it connects
/// to, signals and unsignals the event, and forces completion. The status updates reach the application as RakNet sends
/// them, the status ID then the event ID big-endian (ReadyEvent.cpp:503-518); the queries never do.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ReadyEventScenarios
{
    // A non-zero event ID shows the byte order on the wire.
    private const int EventId = 0x01020304;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Two_systems_that_wait_on_each_other_complete_when_both_are_set(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientEvents, serverEvents) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        // ReadyEventSample.cpp: ID_NEW_INCOMING_CONNECTION and ID_CONNECTION_REQUEST_ACCEPTED add the other system.
        Assert.True(await serverEvents.AddToReadyEventWaitListAsync(EventId, clientGuid, Token));
        Assert.True(await clientEvents.AddToReadyEventWaitListAsync(EventId, serverGuid, Token));

        Assert.True(await serverEvents.SetReadyEventAsync(EventId, true, Token));
        var set = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventSet, "ID_READY_EVENT_SET", Timeout, Token);
        Assert.Equal([(byte)MessageId.ReadyEventSet, 1, 2, 3, 4], set.Data.ToArray());
        Assert.Equal(serverGuid, set.PeerGuid);
        Assert.Equal([new ReadyEventWaiter(serverGuid, "RES_READY")], (await clientEvents.GetReadyEventInfoAsync(EventId, Token)).WaitList);
        Assert.False((await serverEvents.GetReadyEventInfoAsync(EventId, Token)).IsCompleted);

        // The client is the last to set, so it goes straight to all set, and the server follows.
        Assert.True(await clientEvents.SetReadyEventAsync(EventId, true, Token));
        var allSetAtServer = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventAllSet, "ID_READY_EVENT_ALL_SET", Timeout, Token);
        Assert.Equal([(byte)MessageId.ReadyEventAllSet, 1, 2, 3, 4], allSetAtServer.Data.ToArray());
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventAllSet, "ID_READY_EVENT_ALL_SET", Timeout, Token);

        var serverInfo = await WaitForCompletionAsync(serverEvents);
        var clientInfo = await WaitForCompletionAsync(clientEvents);
        Assert.Equal([EventId], serverInfo.EventIds);
        Assert.True(serverInfo.IsSet);
        Assert.False(serverInfo.IsCompletionProcessing);
        Assert.Equal([new ReadyEventWaiter(clientGuid, "RES_ALL_READY")], serverInfo.WaitList);
        Assert.Equal([new ReadyEventWaiter(serverGuid, "RES_ALL_READY")], clientInfo.WaitList);
        AssertNoQueries(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Unsetting_reaches_the_other_system_and_forced_completion_completes_both(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientEvents, serverEvents) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        await serverEvents.AddToReadyEventWaitListAsync(EventId, clientGuid, Token);
        await clientEvents.AddToReadyEventWaitListAsync(EventId, serverGuid, Token);

        // ReadyEventSample.cpp 'S' then 'U'.
        await serverEvents.SetReadyEventAsync(EventId, true, Token);
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventSet, "ID_READY_EVENT_SET", Timeout, Token);
        Assert.True(await serverEvents.SetReadyEventAsync(EventId, false, Token));
        var unset = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventUnset, "ID_READY_EVENT_UNSET", Timeout, Token);
        Assert.Equal([(byte)MessageId.ReadyEventUnset, 1, 2, 3, 4], unset.Data.ToArray());
        Assert.Equal("RES_WAITING", Assert.Single((await clientEvents.GetReadyEventInfoAsync(EventId, Token)).WaitList).Status);

        // 'F': ForceCompletion; the other system completes too and can no longer change (ReadyEvent.cpp:325-342, 409-410).
        await clientEvents.ForceReadyEventCompletionAsync(EventId, Token);
        var forced = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventForceAllSet, "ID_READY_EVENT_FORCE_ALL_SET", Timeout, Token);
        Assert.Equal([(byte)MessageId.ReadyEventForceAllSet, 1, 2, 3, 4], forced.Data.ToArray());
        Assert.True((await serverEvents.GetReadyEventInfoAsync(EventId, Token)).IsCompleted);
        Assert.True((await clientEvents.GetReadyEventInfoAsync(EventId, Token)).IsCompleted);
        Assert.False(await serverEvents.SetReadyEventAsync(EventId, true, Token));
        AssertNoQueries(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_system_that_learns_of_the_event_late_gets_the_status_by_query(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientEvents, serverEvents) = await AttachAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // The server's update reaches a client that has no such event, which ignores it but passes it on.
        await serverEvents.AddToReadyEventWaitListAsync(EventId, clientGuid, Token);
        await serverEvents.SetReadyEventAsync(EventId, true, Token);
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventSet, "ID_READY_EVENT_SET", Timeout, Token);
        Assert.False((await clientEvents.GetReadyEventInfoAsync(EventId, Token)).HasEvent);

        // ReadyEvent.cpp:373-388: the client's query is answered with the server's status, sent again.
        await clientEvents.AddToReadyEventWaitListAsync(EventId, serverGuid, Token);
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ReadyEventSet, "ID_READY_EVENT_SET answering the query", Timeout, Token);
        Assert.Equal("RES_READY", Assert.Single((await clientEvents.GetReadyEventInfoAsync(EventId, Token)).WaitList).Status);
        AssertNoQueries(session);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Three_systems_complete_once_all_are_set_and_a_disconnect_leaves_the_wait_list(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // The second client is of the server's kind, so every pair of kinds talks.
        var second = await session.AddClientAsync(server, Token);
        var (clientEvents, serverEvents) = await AttachAsync(session);
        var secondEvents = (IReadyEventDriver)second.Driver;
        await secondEvents.AttachReadyEventAsync(cancellationToken: Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);
        var (_, secondGuid) = await session.ConnectAsync(second, Token);

        // UNASSIGNED_RAKNET_GUID waits on every connected system (ReadyEvent.cpp:174-185).
        Assert.True(await serverEvents.AddToReadyEventWaitListAsync(EventId, null, Token));
        Assert.Equal(
            new[] { clientGuid, secondGuid }.Order(),
            (await serverEvents.GetReadyEventInfoAsync(EventId, Token)).WaitList.Select(w => w.PeerGuid));
        await clientEvents.AddToReadyEventWaitListAsync(EventId, serverGuid, Token);
        await secondEvents.AddToReadyEventWaitListAsync(EventId, serverGuid, Token);

        await clientEvents.SetReadyEventAsync(EventId, true, Token);
        await secondEvents.SetReadyEventAsync(EventId, true, Token);
        await serverEvents.SetReadyEventAsync(EventId, true, Token);

        await WaitForCompletionAsync(serverEvents);
        await WaitForCompletionAsync(clientEvents);
        await WaitForCompletionAsync(secondEvents);

        // ReadyEvent.cpp:389-396: a system that disconnects leaves every wait list.
        await second.Driver.CloseAsync(serverGuid, notify: true, Token);
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification && p.PeerGuid == secondGuid, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
        Assert.Equal([new ReadyEventWaiter(clientGuid, "RES_ALL_READY")], (await serverEvents.GetReadyEventInfoAsync(EventId, Token)).WaitList);
    }

    private static async Task<(IReadyEventDriver Client, IReadyEventDriver Server)> AttachAsync(InteropSession session)
    {
        var client = (IReadyEventDriver)session.Client;
        var server = (IReadyEventDriver)session.Server;
        await client.AttachReadyEventAsync(cancellationToken: Token);
        await server.AttachReadyEventAsync(cancellationToken: Token);
        return (client, server);
    }

    private static async Task<ReadyEventInfo> WaitForCompletionAsync(IReadyEventDriver driver)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var info = await driver.GetReadyEventInfoAsync(EventId, Token);
            if (info.IsCompleted)
                return info;
            Assert.True(DateTime.UtcNow < deadline, $"The event did not complete: {info}");
            await Task.Delay(20, Token);
        }
    }

    // ReadyEvent.cpp:300-302: ID_READY_EVENT_QUERY is consumed.
    private static void AssertNoQueries(InteropSession session)
    {
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == (byte)MessageId.ReadyEventQuery);
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId == (byte)MessageId.ReadyEventQuery);
    }
}
