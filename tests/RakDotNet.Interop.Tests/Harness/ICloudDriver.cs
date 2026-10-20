using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a CloudClient and a CloudServer on a peer: the cloud* commands of docs/interop-protocol.md. Both
/// drivers implement it; a command fails until its plugin is attached. What the client decodes from
/// ID_CLOUD_GET_RESPONSE and ID_CLOUD_SUBSCRIPTION_NOTIFICATION, and what the server's filter is asked, is read with
/// <see cref="GetCloudInfoAsync"/>.
/// </summary>
public interface ICloudDriver
{
    /// <summary>AttachPlugin of a CloudClient, and of a plugin passing its responses to OnGetReponse and OnSubscriptionNotification.</summary>
    Task AttachCloudClientAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a CloudServer.</summary>
    Task AttachCloudServerAsync(CancellationToken cancellationToken = default);

    /// <summary>CloudClient::Post</summary>
    Task CloudPostAsync(ulong target, CloudKey key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>CloudClient::Release</summary>
    Task CloudReleaseAsync(ulong target, IReadOnlyList<CloudKey> keys, CancellationToken cancellationToken = default);

    /// <summary>CloudClient::Get, with the specific systems overload when <paramref name="specificSystems"/> is given.</summary>
    Task CloudGetAsync(ulong target, IReadOnlyList<CloudKey> keys, uint startingRowIndex = 0, uint maxRowsToReturn = 0, bool subscribe = false,
                       IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default);

    /// <summary>CloudClient::Unsubscribe, with the specific systems overload when <paramref name="specificSystems"/> is given.</summary>
    Task CloudUnsubscribeAsync(ulong target, IReadOnlyList<CloudKey> keys, IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default);

    /// <summary>CloudServer::AddServer</summary>
    Task CloudServerAddServerAsync(ulong server, CancellationToken cancellationToken = default);

    /// <summary>CloudServer::RemoveServer</summary>
    Task CloudServerRemoveServerAsync(ulong server, CancellationToken cancellationToken = default);

    /// <summary>CloudServer::SetMaxUploadBytesPerClient</summary>
    Task CloudServerSetMaxUploadBytesPerClientAsync(ulong bytes, CancellationToken cancellationToken = default);

    /// <summary>CloudServer::ForceExternalSystemAddress</summary>
    Task CloudServerForceExternalAddressAsync(string host, ushort port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the server's query filters with Samples/CloudServer's CloudServerHelperFilter: posts under
    /// <paramref name="rejectPostPrimaryKey"/> are rejected unless they come from <paramref name="exemptGuid"/>. Every
    /// call is recorded.
    /// </summary>
    Task CloudServerSetFilterAsync(string? rejectPostPrimaryKey, ulong? exemptGuid, CancellationToken cancellationToken = default);

    /// <summary>Everything recorded so far, the server's remote servers, and GetLocalIP(0).</summary>
    Task<CloudInfo> GetCloudInfoAsync(CancellationToken cancellationToken = default);
}

/// <summary>A row as a client decoded it; <see cref="Data"/> is hex and addresses are written with ':' before the port.</summary>
public sealed record CloudRowRecord(string PrimaryKey, uint SecondaryKey, string Data, string ServerAddress, string ClientAddress, ulong ServerGuid, ulong ClientGuid)
{
    public CloudKey Key => new(PrimaryKey, SecondaryKey);
}

/// <summary>What CloudClientCallback::OnGet reported.</summary>
public sealed record CloudGetRecord(IReadOnlyList<CloudKey> Keys, uint StartingRowIndex, uint MaxRowsToReturn, bool QuerySubscribe, bool SubscribeToResults,
                                    IReadOnlyList<CloudRowRecord> Rows);

/// <summary>What CloudClientCallback::OnSubscriptionNotification reported.</summary>
public sealed record CloudNotificationRecord(bool WasUpdated, CloudRowRecord Row);

/// <summary>A call to the server's filter; <see cref="Data"/> (hex) only for posts.</summary>
public sealed record CloudFilterCall(string Op, ulong ClientGuid, string ClientAddress, IReadOnlyList<CloudKey> Keys, string? Data, IReadOnlyList<ulong> SpecificSystems);

/// <summary>The cloudInfo reply.</summary>
public sealed record CloudInfo(
    IReadOnlyList<CloudGetRecord> Gets,
    IReadOnlyList<CloudNotificationRecord> Notifications,
    IReadOnlyList<CloudFilterCall> FilterCalls,
    IReadOnlyList<ulong> RemoteServers,
    string LocalIp);
