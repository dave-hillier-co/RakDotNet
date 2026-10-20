namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a ReadyEvent on a peer: the readyEvent* commands of docs/interop-protocol.md. Both drivers implement
/// it; every command but <see cref="AttachReadyEventAsync"/> fails until one is attached.
/// </summary>
public interface IReadyEventDriver
{
    /// <summary>AttachPlugin of a ReadyEvent sending on <paramref name="channel"/> (SetSendChannel).</summary>
    Task AttachReadyEventAsync(byte channel = 0, CancellationToken cancellationToken = default);

    /// <summary>SetEvent</summary>
    Task<bool> SetReadyEventAsync(int eventId, bool isReady, CancellationToken cancellationToken = default);

    /// <summary>ForceCompletion</summary>
    Task ForceReadyEventCompletionAsync(int eventId, CancellationToken cancellationToken = default);

    /// <summary>DeleteEvent</summary>
    Task<bool> DeleteReadyEventAsync(int eventId, CancellationToken cancellationToken = default);

    /// <summary>AddToWaitList; a null target is UNASSIGNED_RAKNET_GUID, every connected system.</summary>
    Task<bool> AddToReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default);

    /// <summary>RemoveFromWaitList; a null target is UNASSIGNED_RAKNET_GUID, everyone.</summary>
    Task<bool> RemoveFromReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default);

    /// <summary>The known events and the state of <paramref name="eventId"/>.</summary>
    Task<ReadyEventInfo> GetReadyEventInfoAsync(int eventId, CancellationToken cancellationToken = default);
}

/// <summary>GetEventAtIndex for every event, then HasEvent, IsEventSet, IsEventCompleted, IsEventCompletionProcessing and the wait list of one.</summary>
public sealed record ReadyEventInfo(
    IReadOnlyList<int> EventIds,
    bool HasEvent,
    bool IsSet,
    bool IsCompleted,
    bool IsCompletionProcessing,
    IReadOnlyList<ReadyEventWaiter> WaitList);

/// <summary>GetFromWaitListAtIndex and GetReadyStatus, the status as RakNet's ReadyEventSystemStatus name (RES_WAITING, ...).</summary>
public sealed record ReadyEventWaiter(ulong PeerGuid, string Status);
