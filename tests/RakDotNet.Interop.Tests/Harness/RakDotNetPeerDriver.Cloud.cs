using System.Net;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The cloud* commands on a <see cref="CloudClient"/> and a <see cref="CloudServer"/>, with interop-peer's results, records and errors.</summary>
public sealed partial class RakDotNetPeerDriver : ICloudDriver
{
    private readonly CloudRecords _cloudRecords = new();
    private CloudClient? _cloudClient;
    private CloudServer? _cloudServer;

    public async Task AttachCloudClientAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("cloudClientAttach");
        if (_cloudClient is not null)
            return;
        _cloudClient = new CloudClient();
        await AttachAsync(peer, _cloudClient, cancellationToken).ConfigureAwait(false);
        await AttachAsync(peer, new CloudResponseRecorder(_cloudClient, _cloudRecords), cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachCloudServerAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("cloudServerAttach");
        if (_cloudServer is not null)
            return;
        _cloudServer = new CloudServer();
        await AttachAsync(peer, _cloudServer, cancellationToken).ConfigureAwait(false);
    }

    public Task CloudPostAsync(ulong target, CloudKey key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        RequireCloudClient().Post(key, data.Span, new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task CloudReleaseAsync(ulong target, IReadOnlyList<CloudKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        RequireCloudClient().Release([.. keys], new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task CloudGetAsync(ulong target, IReadOnlyList<CloudKey> keys, uint startingRowIndex = 0, uint maxRowsToReturn = 0, bool subscribe = false,
                              IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var client = RequireCloudClient();
        var query = new CloudQuery { StartingRowIndex = startingRowIndex, MaxRowsToReturn = maxRowsToReturn, SubscribeToResults = subscribe };
        query.Keys.AddRange(keys);
        if (specificSystems is null)
            client.Get(query, new RakNetGuid(target));
        else
            client.Get(query, [.. specificSystems.Select(g => new RakNetGuid(g))], new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task CloudUnsubscribeAsync(ulong target, IReadOnlyList<CloudKey> keys, IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var client = RequireCloudClient();
        if (specificSystems is null)
            client.Unsubscribe([.. keys], new RakNetGuid(target));
        else
            client.Unsubscribe([.. keys], [.. specificSystems.Select(g => new RakNetGuid(g))], new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task CloudServerAddServerAsync(ulong server, CancellationToken cancellationToken = default)
    {
        RequireCloudServer().AddServer(new RakNetGuid(server));
        return Task.CompletedTask;
    }

    public Task CloudServerRemoveServerAsync(ulong server, CancellationToken cancellationToken = default)
    {
        RequireCloudServer().RemoveServer(new RakNetGuid(server));
        return Task.CompletedTask;
    }

    public Task CloudServerSetMaxUploadBytesPerClientAsync(ulong bytes, CancellationToken cancellationToken = default)
    {
        RequireCloudServer().MaxUploadBytesPerClient = bytes;
        return Task.CompletedTask;
    }

    public Task CloudServerForceExternalAddressAsync(string host, ushort port, CancellationToken cancellationToken = default)
    {
        var server = RequireCloudServer();
        if (!IPAddress.TryParse(host, out var address))
            throw new PeerCommandException("cloudServerForceExternalAddress", "field 'host' is not an address");
        server.ForcedExternalAddress = new SystemAddress(address, port);
        return Task.CompletedTask;
    }

    public Task CloudServerSetFilterAsync(string? rejectPostPrimaryKey, ulong? exemptGuid, CancellationToken cancellationToken = default)
    {
        var server = RequireCloudServer();
        server.RemoveAllQueryFilters();
        server.AddQueryFilter(new RecordingCloudFilter(_cloudRecords, rejectPostPrimaryKey ?? "", exemptGuid is { } g ? new RakNetGuid(g) : RakNetGuid.Unassigned));
        return Task.CompletedTask;
    }

    public Task<CloudInfo> GetCloudInfoAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("cloudInfo");
        var servers = _cloudServer?.RemoteServers.Select(g => g.Value).ToList() ?? [];
        // GetLocalIP(0): ipList[0] without its port.
        var localIp = peer.InternalAddresses[0].ToString(writePort: false);
        lock (_cloudRecords)
            return Task.FromResult(new CloudInfo([.. _cloudRecords.Gets], [.. _cloudRecords.Notifications], [.. _cloudRecords.FilterCalls], servers, localIp));
    }

    // interop-peer's RequireClient and RequireServer.
    private CloudClient RequireCloudClient() =>
        _cloudClient ?? throw new PeerCommandException("cloud", "no CloudClient is attached; send cloudClientAttach first");

    private CloudServer RequireCloudServer() =>
        _cloudServer ?? throw new PeerCommandException("cloud", "no CloudServer is attached; send cloudServerAttach first");

    private static CloudRowRecord RowRecord(CloudQueryRow row) => new(
        row.Key.PrimaryKey,
        row.Key.SecondaryKey,
        Convert.ToHexStringLower(row.Data.Span),
        row.ServerSystemAddress.ToString(writePort: true, ':'),
        row.ClientSystemAddress.ToString(writePort: true, ':'),
        row.ServerGuid.Value,
        row.ClientGuid.Value);

    /// <summary>What the client and the filter reported; locked on itself, as the pump thread writes it.</summary>
    private sealed class CloudRecords
    {
        public List<CloudGetRecord> Gets { get; } = [];

        public List<CloudNotificationRecord> Notifications { get; } = [];

        public List<CloudFilterCall> FilterCalls { get; } = [];
    }

    /// <summary>Passes responses to the CloudClient with a recording handler, as interop-peer's CloudRecorder does, and leaves them for the application.</summary>
    private sealed class CloudResponseRecorder(CloudClient client, CloudRecords records) : IRakPlugin, ICloudClientHandler
    {
        public PluginResult OnEvent(PeerEvent e)
        {
            var data = e.Data;
            if (data.Length > 0 && data[0] == (byte)MessageId.CloudGetResponse)
                client.OnGetResponse(data, this);
            else if (data.Length > 0 && data[0] == (byte)MessageId.CloudSubscriptionNotification)
                client.OnSubscriptionNotification(data, this);
            return PluginResult.Continue;
        }

        public void OnGet(CloudQueryResult result)
        {
            var record = new CloudGetRecord([.. result.Query.Keys], result.Query.StartingRowIndex, result.Query.MaxRowsToReturn, result.Query.SubscribeToResults,
                                            result.SubscribeToResults, [.. result.Rows.Select(RowRecord)]);
            lock (records)
                records.Gets.Add(record);
        }

        public void OnSubscriptionNotification(CloudQueryRow row, bool wasUpdated)
        {
            lock (records)
                records.Notifications.Add(new CloudNotificationRecord(wasUpdated, RowRecord(row)));
        }
    }

    /// <summary>Samples/CloudServer's CloudServerHelperFilter, recording every call as interop-peer's CloudFilter does.</summary>
    private sealed class RecordingCloudFilter(CloudRecords records, string rejectPostPrimaryKey, RakNetGuid exempt) : ICloudServerQueryFilter
    {
        public bool OnPostRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudKey key, ReadOnlySpan<byte> data)
        {
            Record(new CloudFilterCall("post", clientGuid.Value, Text(clientAddress), [key], Convert.ToHexStringLower(data), []));
            return clientGuid == exempt || rejectPostPrimaryKey.Length == 0 || key.PrimaryKey != rejectPostPrimaryKey;
        }

        public bool OnReleaseRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys)
        {
            Record(new CloudFilterCall("release", clientGuid.Value, Text(clientAddress), [.. keys], null, []));
            return true;
        }

        public bool OnGetRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudQuery query, List<RakNetGuid> specificSystems)
        {
            Record(new CloudFilterCall("get", clientGuid.Value, Text(clientAddress), [.. query.Keys], null, [.. specificSystems.Select(g => g.Value)]));
            return true;
        }

        public bool OnUnsubscribeRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys, List<RakNetGuid> specificSystems)
        {
            Record(new CloudFilterCall("unsubscribe", clientGuid.Value, Text(clientAddress), [.. keys], null, [.. specificSystems.Select(g => g.Value)]));
            return true;
        }

        private static string Text(SystemAddress address) => address.ToString(writePort: true, ':');

        private void Record(CloudFilterCall call)
        {
            lock (records)
                records.FilterCalls.Add(call);
        }
    }
}
