namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives one NetworkIDManager and NetworkIDObjects named by handle: the networkId* commands of
/// docs/interop-protocol.md. Both drivers implement it; the manager is created on first use.
/// </summary>
public interface INetworkIdDriver
{
    /// <summary>Creates an object with no manager and no ID.</summary>
    Task NewNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default);

    /// <summary>SetNetworkIDManager of the peer's manager, or of none.</summary>
    Task SetNetworkIdManagerAsync(int handle, bool attach = true, CancellationToken cancellationToken = default);

    /// <summary>SetNetworkID</summary>
    Task SetNetworkIdAsync(int handle, ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>SetParent to another object, or to none.</summary>
    Task SetNetworkIdParentAsync(int handle, int? parent, CancellationToken cancellationToken = default);

    /// <summary>Destroys the object, which stops its manager tracking it.</summary>
    Task DeleteNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default);

    /// <summary>GetNetworkID, and whether GetNetworkIDManager is set.</summary>
    Task<(ulong NetworkId, bool HasManager)> GetNetworkIdObjectInfoAsync(int handle, CancellationToken cancellationToken = default);

    /// <summary>GET_BASE_OBJECT_FROM_ID and GET_OBJECT_FROM_ID, as handles.</summary>
    Task<(int? Handle, int? Object)> LookupNetworkIdAsync(ulong networkId, CancellationToken cancellationToken = default);

    /// <summary>Clear</summary>
    Task ClearNetworkIdsAsync(CancellationToken cancellationToken = default);
}
