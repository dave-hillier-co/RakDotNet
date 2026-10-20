using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The messageFilter* commands on a <see cref="MessageFilter"/>, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : IMessageFilterDriver
{
    private readonly Lock _filterEventsGate = new();
    private readonly List<MessageFilterEvent> _disallowed = [];
    private readonly List<MessageFilterEvent> _timeouts = [];
    private MessageFilter? _messageFilter;

    public Task AttachMessageFilterAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("messageFilterAttach");
        if (_messageFilter is null)
        {
            _messageFilter = new MessageFilter();
            peer.Plugins.Attach(_messageFilter);
        }
        return Task.CompletedTask;
    }

    public Task SetAutoAddNewConnectionsToFilterAsync(int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().AutoAddNewConnectionsToFilter = filterSetId;
        return Task.CompletedTask;
    }

    public Task SetAllowMessageIdAsync(bool allow, byte firstId, byte lastId, int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().SetAllowMessageId(allow, firstId, lastId, filterSetId);
        TrackFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task SetAllowRpc4Async(bool allow, string uniqueId, int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().SetAllowRpc4(allow, uniqueId, filterSetId);
        TrackFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task SetActionOnDisallowedMessageAsync(bool kick, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().SetActionOnDisallowedMessage(kick, ban, banDuration, filterSetId);
        TrackFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task SetFilterMaxTimeAsync(TimeSpan allowedTime, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().SetFilterMaxTime(allowedTime, ban, banDuration, filterSetId);
        TrackFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task SetSystemFilterSetAsync(ulong target, int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().SetSystemFilterSet(new RakNetGuid(target), filterSetId);
        if (filterSetId >= 0)
            TrackFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task<int> GetSystemFilterSetAsync(ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireMessageFilter().GetSystemFilterSet(new RakNetGuid(target)));

    public Task DeleteFilterSetAsync(int filterSetId, CancellationToken cancellationToken = default)
    {
        RequireMessageFilter().DeleteFilterSet(filterSetId);
        return Task.CompletedTask;
    }

    public Task<MessageFilterInfo> GetMessageFilterInfoAsync(CancellationToken cancellationToken = default)
    {
        var filter = RequireMessageFilter();
        lock (_filterEventsGate)
        {
            return Task.FromResult(new MessageFilterInfo(
                [.. Enumerable.Range(0, filter.FilterSetCount).Select(filter.GetFilterSetIdByIndex)],
                filter.GetSystemCount(),
                [.. _disallowed],
                [.. _timeouts]));
        }
    }

    // interop-peer's RequireMessageFilter.
    private MessageFilter RequireMessageFilter() =>
        _messageFilter ?? throw new PeerCommandException("messageFilter", "no MessageFilter is attached; send messageFilterAttach first");

    // interop-peer's TrackFilterSet: every configured filter set reports to GetMessageFilterInfoAsync.
    private void TrackFilterSet(int filterSetId)
    {
        var filter = RequireMessageFilter();
        filter.SetDisallowedMessageCallback(filterSetId, (system, set, id) => Record(_disallowed, Describe(system, set, id)));
        filter.SetTimeoutCallback(filterSetId, (system, set) => Record(_timeouts, Describe(system, set, -1)));
    }

    private void Record(List<MessageFilterEvent> events, MessageFilterEvent filterEvent)
    {
        lock (_filterEventsGate)
            events.Add(filterEvent);
    }

    private static MessageFilterEvent Describe(FilteredSystem system, int filterSetId, int messageId) => new(
        system.RakNetGuid.IsAssigned ? system.RakNetGuid.Value : 0,
        system.Address.IsAssigned ? system.Address.ToString(writePort: true, portDelimiter: ':') : "",
        filterSetId,
        messageId);
}
