using System.Globalization;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IMessageFilterDriver
{
    public Task AttachMessageFilterAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterAttach", [], cancellationToken);

    public Task SetAutoAddNewConnectionsToFilterAsync(int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterSetAutoAdd", new JsonObject { ["filterSetId"] = filterSetId }, cancellationToken);

    public Task SetAllowMessageIdAsync(bool allow, byte firstId, byte lastId, int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterAllow", new JsonObject
        {
            ["allow"] = allow,
            ["start"] = firstId,
            ["end"] = lastId,
            ["filterSetId"] = filterSetId,
        }, cancellationToken);

    public Task SetAllowRpc4Async(bool allow, string uniqueId, int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterAllowRpc4", new JsonObject
        {
            ["allow"] = allow,
            ["uniqueId"] = uniqueId,
            ["filterSetId"] = filterSetId,
        }, cancellationToken);

    public Task SetActionOnDisallowedMessageAsync(bool kick, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterSetActions", new JsonObject
        {
            ["kick"] = kick,
            ["ban"] = ban,
            ["banMs"] = (long)banDuration.TotalMilliseconds,
            ["filterSetId"] = filterSetId,
        }, cancellationToken);

    public Task SetFilterMaxTimeAsync(TimeSpan allowedTime, bool ban, TimeSpan banDuration, int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterSetMaxTime", new JsonObject
        {
            ["ms"] = (long)allowedTime.TotalMilliseconds,
            ["ban"] = ban,
            ["banMs"] = (long)banDuration.TotalMilliseconds,
            ["filterSetId"] = filterSetId,
        }, cancellationToken);

    public Task SetSystemFilterSetAsync(ulong target, int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterSetSystem", new JsonObject
        {
            ["target"] = FormatGuid(target),
            ["filterSetId"] = filterSetId,
        }, cancellationToken);

    public async Task<int> GetSystemFilterSetAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("messageFilterGetSystem", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken).ConfigureAwait(false);
        return (int)reply["filterSetId"]!;
    }

    public Task DeleteFilterSetAsync(int filterSetId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("messageFilterDeleteSet", new JsonObject { ["filterSetId"] = filterSetId }, cancellationToken);

    public async Task<MessageFilterInfo> GetMessageFilterInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("messageFilterInfo", [], cancellationToken).ConfigureAwait(false);
        return new MessageFilterInfo(
            [.. reply["filterSetIds"]!.AsArray().Select(id => (int)id!)],
            (int)reply["systemCount"]!,
            ParseFilterEvents(reply["disallowed"]!.AsArray()),
            ParseFilterEvents(reply["timeouts"]!.AsArray()));
    }

    private static List<MessageFilterEvent> ParseFilterEvents(JsonArray events) =>
        [.. events.Select(e => new MessageFilterEvent(
            ulong.Parse((string)e!["guid"]!, CultureInfo.InvariantCulture),
            (string)e["address"]!,
            (int)e["filterSetId"]!,
            (int?)e["messageId"] ?? -1))];
}
