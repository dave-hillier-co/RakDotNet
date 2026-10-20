using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>A remote system's status for one event, as <see cref="ReadyEvent.GetReadyStatus"/> reports it: ReadyEventSystemStatus.</summary>
public enum ReadyEventSystemStatus
{
    /// <summary>RES_NOT_WAITING: the system is not in the event's wait list.</summary>
    NotWaiting,

    /// <summary>RES_WAITING: the system has not set the event, or has unset it.</summary>
    Waiting,

    /// <summary>RES_READY: the system set the event and is waiting for others.</summary>
    Ready,

    /// <summary>RES_ALL_READY: the system set the event and is no longer waiting for anyone.</summary>
    AllReady,

    /// <summary>RES_UNKNOWN_EVENT: no such event.</summary>
    UnknownEvent,
}

/// <summary>
/// ReadyEvent: peer-to-peer ready signals. Each system sets or unsets an event and waits on a list of other systems; the
/// event completes on a system when it and everyone it waits on are set, and everyone they wait on is too.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ReadyEvent.cpp. Each status change is sent as the status ID (ID_READY_EVENT_SET, _UNSET, _ALL_SET or
/// _FORCE_ALL_SET) followed by the event ID as a big-endian 32-bit integer, HIGH_PRIORITY and RELIABLE_ORDERED on
/// <see cref="SendChannel"/>, to each system in the wait list whose last sent status differs. Adding a system to a wait
/// list sends it ID_READY_EVENT_QUERY with the event ID, which it answers with its status unless that is unset. The
/// updates reach the application; the queries are consumed.
/// </para>
/// <para>
/// RakNet's behaviour is kept where it surprises: an event created by <see cref="SetEvent"/> as ready is not updated,
/// so it is not complete until its status next changes (ReadyEvent.cpp:59-72, 452-461); removing every system from a
/// wait list returns false (ReadyEvent.cpp:192-223); <see cref="IsEventSet"/> is false for a forced event
/// (ReadyEvent.cpp:101-110); and no ID_READY_EVENT_ALL_SET is pushed to the application on completion, as
/// PushCompletionPacket is empty (ReadyEvent.cpp:554-567). A truncated update, query or force message is ignored, where
/// RakNet would read an uninitialised event ID.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class ReadyEvent : IRakPlugin
{
    private const int MessageLength = 1 + sizeof(int);

    private readonly Lock _gate = new();
    private readonly SortedList<int, EventNode> _events = [];
    private IPluginHost? _host;
    private byte _sendChannel;

    /// <summary>SetSendChannel: the ordering channel of every message this plugin sends. 0 by default.</summary>
    public byte SendChannel
    {
        get => _sendChannel;
        set => _sendChannel = value;
    }

    /// <summary>GetEventListSize</summary>
    public int EventCount
    {
        get
        {
            lock (_gate)
                return _events.Count;
        }
    }

    /// <summary>
    /// SetEvent: sets this system ready or not for <paramref name="eventId"/>, creating the event if it is unknown. False
    /// when the status does not change or the event was forced complete.
    /// </summary>
    public bool SetEvent(int eventId, bool isReady)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
            {
                CreateEvent(eventId, isReady);
                return true;
            }
            return SetEventByIndex(node, isReady);
        }
    }

    /// <summary>
    /// ForceCompletion: completes the event here and on every system in its wait list that knows it, whatever anyone's
    /// status. A forced event cannot be changed.
    /// </summary>
    public void ForceCompletion(int eventId)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                node = CreateEvent(eventId, true);
            node.Status = MessageId.ReadyEventForceAllSet;
            UpdateReadyStatus(node);
        }
    }

    /// <summary>DeleteEvent: forgets the event and its wait list. False for an unknown event.</summary>
    public bool DeleteEvent(int eventId)
    {
        lock (_gate)
            return _events.Remove(eventId);
    }

    /// <summary>IsEventSet: what was last passed to <see cref="SetEvent"/>; false for an unknown or forced event.</summary>
    public bool IsEventSet(int eventId)
    {
        lock (_gate)
            return _events.TryGetValue(eventId, out var node) && node.Status is MessageId.ReadyEventSet or MessageId.ReadyEventAllSet;
    }

    /// <summary>
    /// IsEventCompletionProcessing: some system in the wait list has completed and some has not, so the event is about to
    /// complete here. False for a forced event.
    /// </summary>
    public bool IsEventCompletionProcessing(int eventId)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node) || node.Status == MessageId.ReadyEventForceAllSet)
                return false;
            var anyAllReady = false;
            var allAllReady = true;
            foreach (var system in node.Systems)
            {
                if (system.LastReceivedStatus == MessageId.ReadyEventAllSet)
                    anyAllReady = true;
                else
                    allAllReady = false;
            }
            return anyAllReady && !allAllReady;
        }
    }

    /// <summary>
    /// IsEventCompleted: this system is all set and every system in the wait list is too, or the event was forced. False
    /// for an unknown event.
    /// </summary>
    public bool IsEventCompleted(int eventId)
    {
        lock (_gate)
            return _events.TryGetValue(eventId, out var node) && IsCompleted(node);
    }

    /// <summary>HasEvent: the event is known, by <see cref="SetEvent"/>, <see cref="AddToWaitList"/> or <see cref="ForceCompletion"/>.</summary>
    public bool HasEvent(int eventId)
    {
        lock (_gate)
            return _events.ContainsKey(eventId);
    }

    /// <summary>GetEventAtIndex, in ascending event ID order.</summary>
    public int GetEventAtIndex(int index)
    {
        lock (_gate)
            return _events.Keys[index];
    }

    /// <summary>
    /// AddToWaitList: waits on <paramref name="rakNetGuid"/> for the event, creating it unset if it is unknown, and queries
    /// that system's status. <see cref="RakNetGuid.Unassigned"/> adds every connected system. False when nobody was added.
    /// </summary>
    public bool AddToWaitList(int eventId, RakNetGuid rakNetGuid)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                node = CreateEvent(eventId, false);
            var added = 0;
            if (!rakNetGuid.IsAssigned)
            {
                // GetMaximumNumberOfPeers / GetGUIDFromIndex: the connected systems in slot order.
                if (_host?.Peer is { } peer)
                {
                    foreach (var connection in peer.Connections.OrderBy(c => c.SystemIndex))
                        added += AddToWaitListInternal(node, connection.RakNetGuid) ? 1 : 0;
                }
            }
            else
            {
                added = AddToWaitListInternal(node, rakNetGuid) ? 1 : 0;
            }
            if (added > 0)
                UpdateReadyStatus(node);
            return added > 0;
        }
    }

    /// <summary>
    /// RemoveFromWaitList: stops waiting on <paramref name="rakNetGuid"/>, or on everyone for
    /// <see cref="RakNetGuid.Unassigned"/>. True only when one assigned system was removed.
    /// </summary>
    public bool RemoveFromWaitList(int eventId, RakNetGuid rakNetGuid)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                return false;
            if (!rakNetGuid.IsAssigned)
            {
                node.Systems.Clear();
                UpdateReadyStatus(node);
                return false;
            }
            var index = node.IndexOf(rakNetGuid);
            if (index < 0)
                return false;
            node.Systems.RemoveAt(index);
            UpdateReadyStatus(node);
            return true;
        }
    }

    /// <summary>IsInWaitList</summary>
    public bool IsInWaitList(int eventId, RakNetGuid rakNetGuid)
    {
        lock (_gate)
            return _events.TryGetValue(eventId, out var node) && node.IndexOf(rakNetGuid) >= 0;
    }

    /// <summary>GetRemoteWaitListSize: the systems waited on, not counting this one; 0 for an unknown event.</summary>
    public int GetRemoteWaitListSize(int eventId)
    {
        lock (_gate)
            return _events.TryGetValue(eventId, out var node) ? node.Systems.Count : 0;
    }

    /// <summary>GetFromWaitListAtIndex, in ascending GUID order; <see cref="RakNetGuid.Unassigned"/> for an unknown event.</summary>
    public RakNetGuid GetFromWaitListAtIndex(int eventId, int index)
    {
        lock (_gate)
            return _events.TryGetValue(eventId, out var node) ? node.Systems[index].RakNetGuid : RakNetGuid.Unassigned;
    }

    /// <summary>GetReadyStatus: what <paramref name="rakNetGuid"/> last told this system about the event.</summary>
    public ReadyEventSystemStatus GetReadyStatus(int eventId, RakNetGuid rakNetGuid)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                return ReadyEventSystemStatus.UnknownEvent;
            var index = node.IndexOf(rakNetGuid);
            if (index < 0)
                return ReadyEventSystemStatus.NotWaiting;
            return node.Systems[index].LastReceivedStatus switch
            {
                MessageId.ReadyEventSet => ReadyEventSystemStatus.Ready,
                MessageId.ReadyEventUnset => ReadyEventSystemStatus.Waiting,
                MessageId.ReadyEventAllSet => ReadyEventSystemStatus.AllReady,
                // ReadyEvent.cpp:257-275: a system that sent ID_READY_EVENT_FORCE_ALL_SET falls through.
                _ => ReadyEventSystemStatus.UnknownEvent,
            };
        }
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnRakPeerShutdown: Clear (ReadyEvent.cpp:397-400).</summary>
    void IRakPlugin.OnShutdown()
    {
        lock (_gate)
            _events.Clear();
    }

    /// <summary>OnClosedConnection: RemoveFromAllLists (ReadyEvent.cpp:389-396, 535-553).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
        {
            foreach (var node in _events.Values)
            {
                var index = node.IndexOf(connection.RakNetGuid);
                if (index >= 0)
                    node.Systems.RemoveAt(index);
                UpdateReadyStatus(node);
            }
        }
    }

    /// <summary>OnReceive (ReadyEvent.cpp:281-306).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        switch ((MessageId)data[0])
        {
            case MessageId.ReadyEventUnset:
            case MessageId.ReadyEventSet:
            case MessageId.ReadyEventAllSet:
                if (TryReadEventId(data, out var updated))
                    OnPacketUpdate(updated, (MessageId)data[0], e.RakNetGuid);
                return PluginResult.Continue;
            case MessageId.ReadyEventForceAllSet:
                if (TryReadEventId(data, out var forced))
                    OnForceAllSet(forced);
                return PluginResult.Continue;
            case MessageId.ReadyEventQuery:
                if (TryReadEventId(data, out var queried))
                    OnQuery(queried, e.RakNetGuid);
                return PluginResult.Consume;
            default:
                return PluginResult.Continue;
        }
    }

    // OnReadyEventPacketUpdate (ReadyEvent.cpp:343-372).
    private void OnPacketUpdate(int eventId, MessageId status, RakNetGuid sender)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                return;
            var index = node.IndexOf(sender);
            if (index < 0 || node.Systems[index].LastReceivedStatus == status)
                return;
            node.Systems[index].LastReceivedStatus = status;
            if (node.Status == MessageId.ReadyEventForceAllSet)
                return;
            UpdateReadyStatus(node);
        }
    }

    // OnReadyEventForceAllSet (ReadyEvent.cpp:325-342).
    private void OnForceAllSet(int eventId)
    {
        lock (_gate)
        {
            if (_events.TryGetValue(eventId, out var node))
                node.Status = MessageId.ReadyEventForceAllSet;
        }
    }

    // OnReadyEventQuery (ReadyEvent.cpp:373-388): our first update may have reached a system that did not yet know the
    // event, so the status is sent again unless it is unset.
    private void OnQuery(int eventId, RakNetGuid sender)
    {
        lock (_gate)
        {
            if (!_events.TryGetValue(eventId, out var node))
                return;
            var index = node.IndexOf(sender);
            if (index >= 0)
                SendReadyUpdate(node, node.Systems[index], forceIfNotDefault: true);
        }
    }

    // SetEventByIndex (ReadyEvent.cpp:402-427). The completion check that follows only pushes nothing.
    private bool SetEventByIndex(EventNode node, bool isReady)
    {
        if (isReady && node.Status is MessageId.ReadyEventAllSet or MessageId.ReadyEventSet)
            return false;
        if (!isReady && node.Status == MessageId.ReadyEventUnset)
            return false;
        if (node.Status == MessageId.ReadyEventForceAllSet)
            return false;
        node.Status = isReady ? MessageId.ReadyEventSet : MessageId.ReadyEventUnset;
        UpdateReadyStatus(node);
        return true;
    }

    // CreateNewEvent (ReadyEvent.cpp:452-461).
    private EventNode CreateEvent(int eventId, bool isReady)
    {
        var node = new EventNode(eventId) { Status = isReady ? MessageId.ReadyEventSet : MessageId.ReadyEventUnset };
        _events.Add(eventId, node);
        return node;
    }

    // AddToWaitListInternal (ReadyEvent.cpp:307-324).
    private bool AddToWaitListInternal(EventNode node, RakNetGuid rakNetGuid)
    {
        var index = node.IndexOf(rakNetGuid);
        if (index >= 0)
            return false;
        node.Systems.Insert(~index, new RemoteSystem(rakNetGuid));
        SendQuery(node.EventId, rakNetGuid);
        return true;
    }

    // IsEventCompletedByIndex (ReadyEvent.cpp:428-441).
    private static bool IsCompleted(EventNode node)
    {
        if (node.Status == MessageId.ReadyEventForceAllSet)
            return true;
        if (node.Status != MessageId.ReadyEventAllSet)
            return false;
        foreach (var system in node.Systems)
        {
            if (system.LastReceivedStatus != MessageId.ReadyEventAllSet)
                return false;
        }
        return true;
    }

    // UpdateReadyStatus (ReadyEvent.cpp:462-502): set becomes all set when nobody waited on is unset, and back again
    // when someone is; then every system whose last sent status differs is told.
    private void UpdateReadyStatus(EventNode node)
    {
        if (node.Status is MessageId.ReadyEventSet or MessageId.ReadyEventAllSet)
        {
            var anyUnset = node.Systems.Exists(s => s.LastReceivedStatus == MessageId.ReadyEventUnset);
            if (node.Status == MessageId.ReadyEventSet && !anyUnset)
                node.Status = MessageId.ReadyEventAllSet;
            else if (node.Status == MessageId.ReadyEventAllSet && anyUnset)
                node.Status = MessageId.ReadyEventSet;
        }
        foreach (var system in node.Systems)
            SendReadyUpdate(node, system, forceIfNotDefault: false);
    }

    // SendReadyUpdate (ReadyEvent.cpp:503-518).
    private void SendReadyUpdate(EventNode node, RemoteSystem system, bool forceIfNotDefault)
    {
        if (node.Status == system.LastSentStatus && !(forceIfNotDefault && node.Status != MessageId.ReadyEventUnset))
            return;
        Send(node.Status, node.EventId, system.RakNetGuid);
        system.LastSentStatus = node.Status;
    }

    // SendReadyStateQuery (ReadyEvent.cpp:528-534).
    private void SendQuery(int eventId, RakNetGuid rakNetGuid) => Send(MessageId.ReadyEventQuery, eventId, rakNetGuid);

    private void Send(MessageId id, int eventId, RakNetGuid target)
    {
        var host = _host ?? throw new InvalidOperationException("The ReadyEvent is not attached to a peer.");
        Span<byte> message = stackalloc byte[MessageLength];
        message[0] = (byte)id;
        // BitStream::Write(int): big-endian on the wire.
        BinaryPrimitives.WriteInt32BigEndian(message[1..], eventId);
        host.Send(message, target, Reliability.ReliableOrdered, Priority.High, _sendChannel);
    }

    private static bool TryReadEventId(ReadOnlySpan<byte> data, out int eventId)
    {
        if (data.Length < MessageLength)
        {
            eventId = 0;
            return false;
        }
        eventId = BinaryPrimitives.ReadInt32BigEndian(data[1..]);
        return true;
    }

    // ReadyEventNode: the systems are a DataStructures::OrderedList sorted on RakNetGUID.
    private sealed class EventNode(int eventId)
    {
        public int EventId { get; } = eventId;

        public MessageId Status { get; set; }

        public List<RemoteSystem> Systems { get; } = [];

        public int IndexOf(RakNetGuid rakNetGuid)
        {
            int low = 0, high = Systems.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) >>> 1;
                var comparison = Systems[middle].RakNetGuid.Value.CompareTo(rakNetGuid.Value);
                if (comparison == 0)
                    return middle;
                if (comparison < 0)
                    low = middle + 1;
                else
                    high = middle - 1;
            }
            return ~low;
        }
    }

    // RemoteSystem: both statuses start unset (ReadyEvent.cpp:315-317).
    private sealed class RemoteSystem(RakNetGuid rakNetGuid)
    {
        public RakNetGuid RakNetGuid { get; } = rakNetGuid;

        public MessageId LastSentStatus { get; set; } = MessageId.ReadyEventUnset;

        public MessageId LastReceivedStatus { get; set; } = MessageId.ReadyEventUnset;
    }
}
