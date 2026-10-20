using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.ReadyEvent;

public sealed class ReadyEventTests : IDisposable
{
    private const int EventId = 0;

    private readonly PluginMesh _mesh = new();
    private readonly Plugins.ReadyEvent _serverEvents = new();
    private readonly Plugins.ReadyEvent _clientEvents = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;

    public ReadyEventTests()
    {
        _server = _mesh.Add(maxIncoming: 4, _serverEvents);
        _client = _mesh.Add(maxIncoming: 4, _clientEvents);
    }

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void Two_systems_that_wait_on_each_other_complete_when_both_are_set()
    {
        // ReadyEventSample.cpp: each side adds the other to the wait list on connecting, then signals.
        Connect();
        Assert.True(_serverEvents.AddToWaitList(EventId, _client.RakNetGuid));
        Assert.True(_clientEvents.AddToWaitList(EventId, _server.RakNetGuid));
        _mesh.Settle();
        Assert.Equal(ReadyEventSystemStatus.Waiting, _serverEvents.GetReadyStatus(EventId, _client.RakNetGuid));

        Assert.True(_serverEvents.SetEvent(EventId, true));
        _mesh.Settle();
        Assert.False(_serverEvents.IsEventCompleted(EventId));
        Assert.Equal(ReadyEventSystemStatus.Ready, _clientEvents.GetReadyStatus(EventId, _server.RakNetGuid));

        Assert.True(_clientEvents.SetEvent(EventId, true));
        _mesh.Settle();

        Assert.True(_serverEvents.IsEventCompleted(EventId));
        Assert.True(_clientEvents.IsEventCompleted(EventId));
        Assert.True(_serverEvents.IsEventSet(EventId));
        Assert.Equal(ReadyEventSystemStatus.AllReady, _serverEvents.GetReadyStatus(EventId, _client.RakNetGuid));
        Assert.Equal(ReadyEventSystemStatus.AllReady, _clientEvents.GetReadyStatus(EventId, _server.RakNetGuid));
        // ReadyEvent.cpp:281-306: the updates reach the user; the queries are consumed.
        Assert.Equal(
            [(byte)MessageId.ReadyEventSet, (byte)MessageId.ReadyEventAllSet],
            ReadyMessages(_client).Select(d => d[0]));
        // The client goes from unset straight to all set, as the server was already set (ReadyEvent.cpp:462-502).
        Assert.Equal([(byte)MessageId.ReadyEventAllSet], ReadyMessages(_server).Select(d => d[0]));
    }

    [Fact]
    public void An_update_is_the_status_then_the_event_id_big_endian()
    {
        Connect();
        _serverEvents.AddToWaitList(0x01020304, _client.RakNetGuid);
        _clientEvents.AddToWaitList(0x01020304, _server.RakNetGuid);

        _serverEvents.SetEvent(0x01020304, true);
        _mesh.Settle();

        // ReadyEvent.cpp:509-512: bs.Write(eventStatus); bs.Write(eventId). The client's query, answered after the set,
        // repeats it.
        Assert.All(ReadyMessages(_client), m => Assert.Equal([(byte)MessageId.ReadyEventSet, 1, 2, 3, 4], m));
    }

    [Fact]
    public void A_query_from_a_waiting_system_is_answered_with_a_set_status_and_never_reaches_the_user()
    {
        Connect();
        // The server signals before the client knows the event, so its first update is ignored there.
        _serverEvents.AddToWaitList(EventId, _client.RakNetGuid);
        _serverEvents.SetEvent(EventId, true);
        _mesh.Settle();
        Assert.False(_clientEvents.HasEvent(EventId));

        // ReadyEvent.cpp:373-388: AddToWaitList queries the server, which forces its non-default status out again.
        _clientEvents.AddToWaitList(EventId, _server.RakNetGuid);
        _mesh.Settle();

        Assert.Equal(ReadyEventSystemStatus.Ready, _clientEvents.GetReadyStatus(EventId, _server.RakNetGuid));
        Assert.DoesNotContain(_mesh.Seen(_server).Concat(_mesh.Seen(_client)), e => e.Id == (byte)MessageId.ReadyEventQuery);
    }

    [Fact]
    public void Unsetting_reaches_the_other_system()
    {
        Connect();
        WaitOnEachOther();
        _clientEvents.SetEvent(EventId, true);
        _mesh.Settle();

        Assert.True(_clientEvents.SetEvent(EventId, false));
        _mesh.Settle();

        Assert.Equal(ReadyEventSystemStatus.Waiting, _serverEvents.GetReadyStatus(EventId, _client.RakNetGuid));
        Assert.False(_clientEvents.IsEventSet(EventId));
        Assert.Equal((byte)MessageId.ReadyEventUnset, ReadyMessages(_server)[^1][0]);
    }

    [Fact]
    public void Setting_a_status_already_held_reports_no_change()
    {
        Assert.True(_serverEvents.SetEvent(EventId, false));
        Assert.False(_serverEvents.SetEvent(EventId, false));
        Assert.True(_serverEvents.SetEvent(EventId, true));
        Assert.False(_serverEvents.SetEvent(EventId, true));
    }

    [Fact]
    public void An_event_created_set_completes_only_once_its_status_is_next_updated()
    {
        // ReadyEvent.cpp:59-72, 452-461: a new event is created SET without UpdateReadyStatus, so it is not ALL_SET.
        _serverEvents.SetEvent(EventId, true);
        Assert.False(_serverEvents.IsEventCompleted(EventId));

        _serverEvents.SetEvent(EventId, false);
        _serverEvents.SetEvent(EventId, true);

        Assert.True(_serverEvents.IsEventCompleted(EventId));
        Assert.False(_serverEvents.IsEventCompletionProcessing(EventId));
    }

    [Fact]
    public void Forcing_completion_completes_the_event_on_every_system_that_knows_it_and_cannot_be_undone()
    {
        Connect();
        WaitOnEachOther();

        _serverEvents.ForceCompletion(EventId);
        _mesh.Settle();

        Assert.True(_serverEvents.IsEventCompleted(EventId));
        Assert.True(_clientEvents.IsEventCompleted(EventId));
        // ReadyEvent.cpp:286-288: the user still sees the packet.
        Assert.Equal([(byte)MessageId.ReadyEventForceAllSet, 0, 0, 0, 0], ReadyMessages(_client)[^1]);
        // ReadyEvent.cpp:409-410: a forced event cannot change.
        Assert.False(_clientEvents.SetEvent(EventId, false));
        Assert.True(_clientEvents.IsEventCompleted(EventId));
        // IsEventSet looks only for SET and ALL_SET (ReadyEvent.cpp:101-110).
        Assert.False(_serverEvents.IsEventSet(EventId));
    }

    [Fact]
    public void A_system_that_disconnects_leaves_every_wait_list_and_completes_the_event()
    {
        Connect();
        WaitOnEachOther();
        _serverEvents.SetEvent(EventId, true);
        _mesh.Settle();
        Assert.False(_serverEvents.IsEventCompleted(EventId));

        _client.Connections.Single().Close();
        _mesh.Settle();

        Assert.False(_serverEvents.IsInWaitList(EventId, _client.RakNetGuid));
        Assert.Equal(0, _serverEvents.GetRemoteWaitListSize(EventId));
        Assert.True(_serverEvents.IsEventCompleted(EventId));
    }

    [Fact]
    public void Adding_the_unassigned_guid_waits_on_every_connected_system()
    {
        var third = _mesh.Add(maxIncoming: 0);
        Connect();
        _mesh.Connect(third, _server);
        _mesh.RunUntil(() => _server.ConnectionCount == 2, "both clients connected at the server");

        Assert.True(_serverEvents.AddToWaitList(EventId, RakNetGuid.Unassigned));
        Assert.False(_serverEvents.AddToWaitList(EventId, _client.RakNetGuid));

        Assert.Equal(2, _serverEvents.GetRemoteWaitListSize(EventId));
        // DataStructures::OrderedList keyed on RakNetGUID: ascending.
        var expected = new[] { _client.RakNetGuid, third.RakNetGuid }.OrderBy(g => g.Value).ToArray();
        Assert.Equal(expected, new[] { _serverEvents.GetFromWaitListAtIndex(EventId, 0), _serverEvents.GetFromWaitListAtIndex(EventId, 1) });

        // ReadyEvent.cpp:192-223: removing everyone falls through to return false.
        Assert.False(_serverEvents.RemoveFromWaitList(EventId, RakNetGuid.Unassigned));
        Assert.Equal(0, _serverEvents.GetRemoteWaitListSize(EventId));
    }

    [Fact]
    public void Events_are_listed_in_ascending_order_and_can_be_deleted()
    {
        _serverEvents.SetEvent(7, true);
        _serverEvents.SetEvent(-3, false);
        _serverEvents.AddToWaitList(2, new RakNetGuid(99));

        Assert.Equal(3, _serverEvents.EventCount);
        Assert.Equal([-3, 2, 7], Enumerable.Range(0, 3).Select(_serverEvents.GetEventAtIndex));
        Assert.True(_serverEvents.DeleteEvent(2));
        Assert.False(_serverEvents.DeleteEvent(2));
        Assert.False(_serverEvents.HasEvent(2));
        Assert.Equal(ReadyEventSystemStatus.UnknownEvent, _serverEvents.GetReadyStatus(2, new RakNetGuid(99)));
        Assert.Equal(ReadyEventSystemStatus.NotWaiting, _serverEvents.GetReadyStatus(7, new RakNetGuid(99)));
    }

    [Fact]
    public void An_update_from_a_system_not_waited_on_changes_nothing_but_reaches_the_user()
    {
        Connect();
        _serverEvents.SetEvent(EventId, false);
        _clientEvents.AddToWaitList(EventId, _server.RakNetGuid);
        _clientEvents.SetEvent(EventId, true);
        _mesh.Settle();

        Assert.Equal(ReadyEventSystemStatus.NotWaiting, _serverEvents.GetReadyStatus(EventId, _client.RakNetGuid));
        Assert.Contains(ReadyMessages(_server), d => d[0] == (byte)MessageId.ReadyEventSet);
    }

    [Fact]
    public void Shutting_down_forgets_every_event()
    {
        _serverEvents.SetEvent(EventId, true);

        _server.Dispose();

        Assert.Equal(0, _serverEvents.EventCount);
    }

    private void Connect() => _mesh.Connect(_client, _server);

    private void WaitOnEachOther()
    {
        _serverEvents.AddToWaitList(EventId, _client.RakNetGuid);
        _clientEvents.AddToWaitList(EventId, _server.RakNetGuid);
        _mesh.Settle();
    }

    private List<byte[]> ReadyMessages(RakPeer peer) =>
    [
        .. _mesh.Seen(peer)
            .Where(e => e.Id is (byte)MessageId.ReadyEventSet or (byte)MessageId.ReadyEventUnset or (byte)MessageId.ReadyEventAllSet
                or (byte)MessageId.ReadyEventForceAllSet or (byte)MessageId.ReadyEventQuery)
            .Select(e => e.Data),
    ];
}
