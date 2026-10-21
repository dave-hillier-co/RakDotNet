using System.Globalization;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The randSync*, rakNetRandom, networkId*, grid* and delta* commands.</summary>
public sealed partial class NativePeerDriver : IRandSyncDriver, INetworkIdDriver, IGridSectorizerDriver, IDeltaSerializerDriver
{
    public async Task<RakNetRandomOutput> DrawRakNetRandomAsync(uint seed, int count, int floatCount = 0, int? fillBytes = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["seed"] = seed, ["count"] = count, ["floatCount"] = floatCount };
        if (fillBytes is { } bytes)
            fields["fillBytes"] = bytes;
        var reply = await SendCommandAsync("rakNetRandom", fields, cancellationToken).ConfigureAwait(false);
        return new RakNetRandomOutput(
            UInts(reply["values"]!),
            UInts(reply["floatBits"]!),
            reply["fill"] is { } fill ? Convert.FromHexString((string)fill!) : null,
            reply["afterFill"] is { } after ? (uint)after : null);
    }

    public Task SeedRandSyncAsync(uint seed, uint? skip = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["seed"] = seed };
        if (skip is { } values)
            fields["skip"] = values;
        return SendCommandAsync("randSyncSeed", fields, cancellationToken);
    }

    public async Task<IReadOnlyList<uint>> NextRandSyncAsync(int count, bool asFloat = false, CancellationToken cancellationToken = default) =>
        UInts((await SendCommandAsync("randSyncNext", new JsonObject { ["count"] = count, ["float"] = asFloat }, cancellationToken).ConfigureAwait(false))["values"]!);

    public async Task<byte[]> SerializeRandSyncConstructionAsync(CancellationToken cancellationToken = default) =>
        Convert.FromHexString((string)(await SendCommandAsync("randSyncSerializeConstruction", [], cancellationToken).ConfigureAwait(false))["data"]!);

    public async Task<bool> DeserializeRandSyncConstructionAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("randSyncDeserializeConstruction", new JsonObject { ["data"] = Convert.ToHexStringLower(data.Span) }, cancellationToken).ConfigureAwait(false))["result"]!;

    public async Task<byte[]> SerializeRandSyncAsync(CancellationToken cancellationToken = default) =>
        Convert.FromHexString((string)(await SendCommandAsync("randSyncSerialize", [], cancellationToken).ConfigureAwait(false))["data"]!);

    public Task DeserializeRandSyncAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendCommandAsync("randSyncDeserialize", new JsonObject { ["data"] = Convert.ToHexStringLower(data.Span) }, cancellationToken);

    public async Task<(uint Seed, uint CallCount)> GetRandSyncInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("randSyncInfo", [], cancellationToken).ConfigureAwait(false);
        return ((uint)reply["seed"]!, (uint)reply["callCount"]!);
    }

    public Task NewNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default) =>
        SendCommandAsync("networkIdObjectNew", new JsonObject { ["handle"] = handle }, cancellationToken);

    public Task SetNetworkIdManagerAsync(int handle, bool attach = true, CancellationToken cancellationToken = default) =>
        SendCommandAsync("networkIdObjectSetManager", new JsonObject { ["handle"] = handle, ["attach"] = attach }, cancellationToken);

    public Task SetNetworkIdAsync(int handle, ulong networkId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("networkIdObjectSetId", new JsonObject { ["handle"] = handle, ["networkId"] = FormatGuid(networkId) }, cancellationToken);

    public Task SetNetworkIdParentAsync(int handle, int? parent, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["handle"] = handle };
        if (parent is { } p)
            fields["parent"] = p;
        return SendCommandAsync("networkIdObjectSetParent", fields, cancellationToken);
    }

    public Task DeleteNetworkIdObjectAsync(int handle, CancellationToken cancellationToken = default) =>
        SendCommandAsync("networkIdObjectDelete", new JsonObject { ["handle"] = handle }, cancellationToken);

    public async Task<(ulong NetworkId, bool HasManager)> GetNetworkIdObjectInfoAsync(int handle, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("networkIdObjectInfo", new JsonObject { ["handle"] = handle }, cancellationToken).ConfigureAwait(false);
        return (ulong.Parse((string)reply["networkId"]!, CultureInfo.InvariantCulture), (bool)reply["hasManager"]!);
    }

    public async Task<(int? Handle, int? Object)> LookupNetworkIdAsync(ulong networkId, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("networkIdLookup", new JsonObject { ["networkId"] = FormatGuid(networkId) }, cancellationToken).ConfigureAwait(false);
        return ((int?)reply["handle"], (int?)reply["object"]);
    }

    public Task ClearNetworkIdsAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("networkIdClear", [], cancellationToken);

    public Task InitGridAsync(float maxCellWidth, float maxCellHeight, GridRectangle world, CancellationToken cancellationToken = default)
    {
        var fields = Rectangle(world);
        fields["maxCellWidth"] = maxCellWidth;
        fields["maxCellHeight"] = maxCellHeight;
        return SendCommandAsync("gridInit", fields, cancellationToken);
    }

    public Task AddGridEntryAsync(int entry, GridRectangle bounds, CancellationToken cancellationToken = default)
    {
        var fields = Rectangle(bounds);
        fields["entry"] = entry;
        return SendCommandAsync("gridAdd", fields, cancellationToken);
    }

    public async Task<IReadOnlyList<int>> GetGridEntriesAsync(GridRectangle bounds, CancellationToken cancellationToken = default) =>
        [.. (await SendCommandAsync("gridGet", Rectangle(bounds), cancellationToken).ConfigureAwait(false))["entries"]!.AsArray().Select(e => (int)e!)];

    public Task ClearGridAsync(CancellationToken cancellationToken = default) => SendCommandAsync("gridClear", [], cancellationToken);

    public async Task<IReadOnlyList<DeltaGroupResult>> SerializeDeltaAsync(IReadOnlyList<DeltaGroup> groups, bool preSerializeTick = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var array = new JsonArray();
        foreach (var group in groups)
        {
            var item = new JsonObject
            {
                ["mode"] = group.Mode,
                ["values"] = new JsonArray([.. group.Values.Select(v => (JsonNode)v)]),
                ["receipt"] = group.Receipt,
                ["first"] = group.First,
                ["messageId"] = group.MessageId,
            };
            if (group.Target is { } target)
                item["target"] = FormatGuid(target);
            if (group.SendTo is { } sendTo)
                item["sendTo"] = FormatGuid(sendTo);
            if (group.Reliability is { } reliability)
                item["reliability"] = ToProtocolName(reliability);
            array.Add(item);
        }
        var reply = await SendCommandAsync("deltaSerialize", new JsonObject { ["groups"] = array, ["preSerializeTick"] = preSerializeTick }, cancellationToken).ConfigureAwait(false);
        return [.. reply["groups"]!.AsArray().Select(g => new DeltaGroupResult(
            Convert.FromHexString((string)g!["data"]!), (int)g["bits"]!, (uint)g["receipt"]!, (bool)g["sent"]!))];
    }

    public Task DeltaPreSerializeTickAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("deltaPreSerializeTick", [], cancellationToken);

    public Task DeltaReceiptAsync(ulong target, uint receipt, bool arrived, CancellationToken cancellationToken = default) =>
        SendCommandAsync("deltaReceipt", new JsonObject { ["target"] = FormatGuid(target), ["receipt"] = receipt, ["arrived"] = arrived }, cancellationToken);

    public Task RemoveDeltaSystemAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("deltaRemoveSystem", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public async Task<IReadOnlyList<int?>> DeserializeDeltaAsync(ReadOnlyMemory<byte> data, int count, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("deltaDeserialize", new JsonObject { ["data"] = Convert.ToHexStringLower(data.Span), ["count"] = count }, cancellationToken).ConfigureAwait(false);
        return [.. reply["variables"]!.AsArray().Select(v => (bool)v!["changed"]! ? (int?)(int)v["value"]! : null)];
    }

    private static uint[] UInts(JsonNode array) => [.. array.AsArray().Select(v => (uint)v!)];

    private static JsonObject Rectangle(GridRectangle rectangle) => new()
    {
        ["minX"] = rectangle.MinX,
        ["minY"] = rectangle.MinY,
        ["maxX"] = rectangle.MaxX,
        ["maxY"] = rectangle.MaxY,
    };
}
