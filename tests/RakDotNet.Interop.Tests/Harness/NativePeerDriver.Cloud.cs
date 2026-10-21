using System.Text;
using System.Text.Json.Nodes;
using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : ICloudDriver
{
    public Task AttachCloudClientAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudClientAttach", [], cancellationToken);

    public Task AttachCloudServerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudServerAttach", [], cancellationToken);

    public Task CloudPostAsync(ulong target, CloudKey key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudPost", new JsonObject
        {
            ["target"] = FormatGuid(target),
            ["key"] = KeyJson(key),
            ["data"] = Convert.ToHexStringLower(data.Span),
        }, cancellationToken);

    public Task CloudReleaseAsync(ulong target, IReadOnlyList<CloudKey> keys, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudRelease", new JsonObject { ["target"] = FormatGuid(target), ["keys"] = KeysJson(keys) }, cancellationToken);

    public Task CloudGetAsync(ulong target, IReadOnlyList<CloudKey> keys, uint startingRowIndex = 0, uint maxRowsToReturn = 0, bool subscribe = false,
                              IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject
        {
            ["target"] = FormatGuid(target),
            ["keys"] = KeysJson(keys),
            ["startingRowIndex"] = startingRowIndex,
            ["maxRowsToReturn"] = maxRowsToReturn,
            ["subscribe"] = subscribe,
        };
        if (specificSystems is not null)
            fields["specificSystems"] = GuidsJson(specificSystems);
        return SendCommandAsync("cloudGet", fields, cancellationToken);
    }

    public Task CloudUnsubscribeAsync(ulong target, IReadOnlyList<CloudKey> keys, IReadOnlyList<ulong>? specificSystems = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["target"] = FormatGuid(target), ["keys"] = KeysJson(keys) };
        if (specificSystems is not null)
            fields["specificSystems"] = GuidsJson(specificSystems);
        return SendCommandAsync("cloudUnsubscribe", fields, cancellationToken);
    }

    public Task CloudServerAddServerAsync(ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudServerAddServer", new JsonObject { ["guid"] = FormatGuid(server) }, cancellationToken);

    public Task CloudServerRemoveServerAsync(ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudServerRemoveServer", new JsonObject { ["guid"] = FormatGuid(server) }, cancellationToken);

    public Task CloudServerSetMaxUploadBytesPerClientAsync(ulong bytes, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudServerSetMaxUploadBytesPerClient", new JsonObject { ["bytes"] = bytes }, cancellationToken);

    public Task CloudServerForceExternalAddressAsync(string host, ushort port, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cloudServerForceExternalAddress", new JsonObject { ["host"] = host, ["port"] = port }, cancellationToken);

    public Task CloudServerSetFilterAsync(string? rejectPostPrimaryKey, ulong? exemptGuid, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["rejectPostPrimaryKey"] = rejectPostPrimaryKey ?? "" };
        if (exemptGuid is { } guid)
            fields["exemptGuid"] = FormatGuid(guid);
        return SendCommandAsync("cloudServerSetFilter", fields, cancellationToken);
    }

    public async Task<CloudInfo> GetCloudInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("cloudInfo", [], cancellationToken).ConfigureAwait(false);
        return new CloudInfo(
            [.. reply["gets"]!.AsArray().Select(n => new CloudGetRecord(
                ParseKeys(n!["keys"]!), (uint)n["startingRowIndex"]!, (uint)n["maxRowsToReturn"]!, (bool)n["querySubscribe"]!, (bool)n["subscribeToResults"]!,
                [.. n["rows"]!.AsArray().Select(r => ParseRow(r!))]))],
            [.. reply["notifications"]!.AsArray().Select(n => new CloudNotificationRecord((bool)n!["wasUpdated"]!, ParseRow(n["row"]!)))],
            [.. reply["filterCalls"]!.AsArray().Select(n => ParseFilterCall(n!.AsObject()))],
            [.. reply["remoteServers"]!.AsArray().Select(ParseGuid)],
            (string)reply["localIp"]!);
    }

    private static JsonObject KeyJson(CloudKey key) => new() { ["primaryKey"] = key.PrimaryKey, ["secondaryKey"] = key.SecondaryKey };

    private static JsonArray KeysJson(IReadOnlyList<CloudKey> keys) => [.. keys.Select(k => (JsonNode)KeyJson(k))];

    private static JsonArray GuidsJson(IReadOnlyList<ulong> guids) => [.. guids.Select(g => (JsonNode)FormatGuid(g))];

    // Records carry the primary key as hex, since a RakString need not be UTF-8.
    private static CloudKey ParseKey(JsonNode n) => new(Convert.FromHexString((string)n["primaryKey"]!), (uint)n["secondaryKey"]!);

    private static List<CloudKey> ParseKeys(JsonNode n) => [.. n.AsArray().Select(k => ParseKey(k!))];

    private static CloudRowRecord ParseRow(JsonNode n) => new(
        Encoding.UTF8.GetString(Convert.FromHexString((string)n["primaryKey"]!)),
        (uint)n["secondaryKey"]!,
        (string)n["data"]!,
        (string)n["serverAddress"]!,
        (string)n["clientAddress"]!,
        ParseGuid(n["serverGuid"]),
        ParseGuid(n["clientGuid"]));

    private static CloudFilterCall ParseFilterCall(JsonObject n) => new(
        (string)n["op"]!,
        ParseGuid(n["clientGuid"]),
        (string)n["clientAddress"]!,
        n["key"] is { } key ? [ParseKey(key)] : ParseKeys(n["keys"]!),
        n["data"] is { } data ? (string)data! : null,
        n["specificSystems"] is { } systems ? [.. systems.AsArray().Select(ParseGuid)] : []);
}
