namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a MessageFilter on a peer: the messageFilter* commands of docs/interop-protocol.md. Both drivers
/// implement it; every command but <see cref="AttachMessageFilterAsync"/> fails until a filter is attached.
/// </summary>
public interface IMessageFilterDriver
{
    /// <summary>AttachPlugin of a MessageFilter whose auto-add filter set is -1.</summary>
    Task AttachMessageFilterAsync(CancellationToken cancellationToken = default);

    /// <summary>SetAutoAddNewConnectionsToFilter; -1 adds none.</summary>
    Task SetAutoAddNewConnectionsToFilterAsync(int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>SetAllowMessageID for <paramref name="firstId"/> to <paramref name="lastId"/> inclusive.</summary>
    Task SetAllowMessageIdAsync(bool allow, byte firstId, byte lastId, int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>SetAllowRPC4</summary>
    Task SetAllowRpc4Async(bool allow, string uniqueId, int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>SetActionOnDisallowedMessage; a zero ban duration is permanent.</summary>
    Task SetActionOnDisallowedMessageAsync(bool kick, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>SetFilterMaxTime</summary>
    Task SetFilterMaxTimeAsync(TimeSpan allowedTime, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>SetSystemFilterSet for the system with the GUID; -1 removes it from every set.</summary>
    Task SetSystemFilterSetAsync(ulong target, int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>GetSystemFilterSet for the system with the GUID; -1 for none.</summary>
    Task<int> GetSystemFilterSetAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>DeleteFilterSet</summary>
    Task DeleteFilterSetAsync(int filterSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The filter set IDs in order, the number of filtered systems, and the disallowed messages and timeouts reported so
    /// far by the filter sets the commands configured.
    /// </summary>
    Task<MessageFilterInfo> GetMessageFilterInfoAsync(CancellationToken cancellationToken = default);
}

public sealed record MessageFilterInfo(
    IReadOnlyList<int> FilterSetIds,
    int SystemCount,
    IReadOnlyList<MessageFilterEvent> Disallowed,
    IReadOnlyList<MessageFilterEvent> Timeouts);

/// <summary>A disallowed message (with its first byte) or a timeout (MessageId -1) a filter set reported.</summary>
/// <param name="PeerGuid">The system's GUID, or 0 when the filter keyed it by address alone.</param>
/// <param name="Address">The system's address as "a.b.c.d:port", or empty when the filter keyed it by GUID alone.</param>
public sealed record MessageFilterEvent(ulong PeerGuid, string Address, int FilterSetId, int MessageId);
