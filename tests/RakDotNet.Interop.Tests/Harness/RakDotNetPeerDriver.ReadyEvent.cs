using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The readyEvent* commands on a <see cref="ReadyEvent"/>, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : IReadyEventDriver
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(5);

    private ReadyEvent? _readyEvent;

    public async Task AttachReadyEventAsync(byte channel = 0, CancellationToken cancellationToken = default)
    {
        var peer = Started("readyEventAttach");
        if (_readyEvent is not null)
            return;
        _readyEvent = new ReadyEvent { SendChannel = channel };
        await AttachAsync(peer, _readyEvent, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SetReadyEventAsync(int eventId, bool isReady, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireReadyEvent().SetEvent(eventId, isReady));

    public Task ForceReadyEventCompletionAsync(int eventId, CancellationToken cancellationToken = default)
    {
        RequireReadyEvent().ForceCompletion(eventId);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteReadyEventAsync(int eventId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireReadyEvent().DeleteEvent(eventId));

    public Task<bool> AddToReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireReadyEvent().AddToWaitList(eventId, ToGuid(target)));

    public Task<bool> RemoveFromReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireReadyEvent().RemoveFromWaitList(eventId, ToGuid(target)));

    public Task<ReadyEventInfo> GetReadyEventInfoAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var readyEvent = RequireReadyEvent();
        var waitList = new List<ReadyEventWaiter>();
        for (var i = 0; i < readyEvent.GetRemoteWaitListSize(eventId); i++)
        {
            var guid = readyEvent.GetFromWaitListAtIndex(eventId, i);
            waitList.Add(new ReadyEventWaiter(guid.Value, StatusName(readyEvent.GetReadyStatus(eventId, guid))));
        }
        return Task.FromResult(new ReadyEventInfo(
            [.. Enumerable.Range(0, readyEvent.EventCount).Select(readyEvent.GetEventAtIndex)],
            readyEvent.HasEvent(eventId),
            readyEvent.IsEventSet(eventId),
            readyEvent.IsEventCompleted(eventId),
            readyEvent.IsEventCompletionProcessing(eventId),
            waitList));
    }

    // RakNet's AttachPlugin acts at once; here an attach from outside a hook is applied by the next dispatch step, which
    // the pump runs, so wait for it before the plugin's members send through the peer.
    private static async Task AttachAsync(RakPeer peer, IRakPlugin plugin, CancellationToken cancellationToken)
    {
        peer.Plugins.Attach(plugin);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AttachTimeout);
        while (!peer.Plugins.Contains(plugin))
            await Task.Delay(1, timeout.Token).ConfigureAwait(false);
    }

    private static RakNetGuid ToGuid(ulong? target) => target is { } guid ? new RakNetGuid(guid) : RakNetGuid.Unassigned;

    private static string StatusName(ReadyEventSystemStatus status) => status switch
    {
        ReadyEventSystemStatus.NotWaiting => "RES_NOT_WAITING",
        ReadyEventSystemStatus.Waiting => "RES_WAITING",
        ReadyEventSystemStatus.Ready => "RES_READY",
        ReadyEventSystemStatus.AllReady => "RES_ALL_READY",
        _ => "RES_UNKNOWN_EVENT",
    };

    // interop-peer's RequireReadyEvent.
    private ReadyEvent RequireReadyEvent() =>
        _readyEvent ?? throw new PeerCommandException("readyEvent", "no ReadyEvent is attached; send readyEventAttach first");
}
