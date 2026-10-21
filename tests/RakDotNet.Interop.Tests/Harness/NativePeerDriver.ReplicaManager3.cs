using System.Globalization;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The rm3* commands.</summary>
public sealed partial class NativePeerDriver : IReplicaManager3Driver
{
    public Task AttachReplicaManager3Async(string topology, int autoSerializeMs = 30, bool groupDownloads = true, CancellationToken cancellationToken = default) =>
        SendCommandAsync("rm3Attach", new JsonObject { ["topology"] = topology, ["autoSerializeMs"] = autoSerializeMs, ["groupDownloads"] = groupDownloads }, cancellationToken);

    public async Task<IReadOnlyList<ulong>> CreateReplicasAsync(IReadOnlyList<string> kinds, string? username = null, ulong? userGuid = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        var fields = new JsonObject { ["kinds"] = new JsonArray([.. kinds.Select(k => (JsonNode)k)]) };
        if (username is not null)
            fields["username"] = username;
        if (userGuid is { } guid)
            fields["userGuid"] = FormatGuid(guid);
        var reply = await SendCommandAsync("rm3Create", fields, cancellationToken).ConfigureAwait(false);
        return [.. reply["networkIds"]!.AsArray().Select(Decimal)];
    }

    public Task SetReplicaAsync(ulong networkId, ReplicaValues values, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var fields = new JsonObject { ["networkId"] = FormatGuid(networkId) };
        if (values.Var1 is { } var1)
            fields["var1"] = var1;
        if (values.Var2 is { } var2)
            fields["var2"] = var2;
        if (values.Var3 is { } var3)
            fields["var3"] = var3;
        if (values.Var4 is { } var4)
            fields["var4"] = var4;
        if (values.Score is { } score)
            fields["score"] = score;
        if (values.Username is { } username)
            fields["username"] = username;
        return SendCommandAsync("rm3Set", fields, cancellationToken);
    }

    public async Task<int> DestroyMyReplicasAsync(CancellationToken cancellationToken = default) =>
        (int)(await SendCommandAsync("rm3DestroyMine", [], cancellationToken).ConfigureAwait(false))["destroyed"]!;

    public Task DestroyReplicaAsync(ulong networkId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("rm3Destroy", new JsonObject { ["networkId"] = FormatGuid(networkId) }, cancellationToken);

    public async Task<ReplicaManager3Info> GetReplicaManager3InfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("rm3Info", [], cancellationToken).ConfigureAwait(false);
        var replicas = reply["replicas"]!.AsArray().Select(r => new ReplicaState(
            Decimal(r!["networkId"]),
            (string)r["kind"]!,
            Decimal(r["creatingGuid"]),
            (int?)r["var1"],
            (int?)r["var2"],
            (int?)r["var3"],
            (int?)r["var4"],
            (string?)r["constructionText"],
            (int?)r["score"],
            (string?)r["username"],
            r["userGuid"] is { } userGuid ? Decimal(userGuid) : null)).ToList();
        var connections = reply["connections"]!.AsArray().Select(c => new ReplicaConnectionState(
            Decimal(c!["guid"]),
            (bool)c["validated"]!,
            (bool)c["downloadComplete"]!,
            [.. c["constructed"]!.AsArray().Select(Decimal)])).ToList();
        var destructions = reply["destructions"]!.AsArray().Select(d => new ReplicaDestruction(
            Decimal(d!["networkId"]),
            (string)d["kind"]!,
            (string)d["text"]!,
            Decimal(d["deletingGuid"]))).ToList();
        return new ReplicaManager3Info(replicas, connections, destructions, (bool)reply["allDownloadsCompleted"]!);
    }

    private static ulong Decimal(JsonNode? node) => ulong.Parse((string)node!, CultureInfo.InvariantCulture);
}
