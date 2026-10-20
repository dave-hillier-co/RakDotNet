using System.Globalization;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IReadyEventDriver
{
    public Task AttachReadyEventAsync(byte channel = 0, CancellationToken cancellationToken = default) =>
        SendCommandAsync("readyEventAttach", new JsonObject { ["channel"] = channel }, cancellationToken);

    public async Task<bool> SetReadyEventAsync(int eventId, bool isReady, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("readyEventSet", new JsonObject { ["eventId"] = eventId, ["isReady"] = isReady }, cancellationToken).ConfigureAwait(false))["result"]!;

    public Task ForceReadyEventCompletionAsync(int eventId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("readyEventForceCompletion", new JsonObject { ["eventId"] = eventId }, cancellationToken);

    public async Task<bool> DeleteReadyEventAsync(int eventId, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("readyEventDelete", new JsonObject { ["eventId"] = eventId }, cancellationToken).ConfigureAwait(false))["result"]!;

    public async Task<bool> AddToReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("readyEventAddToWaitList", WaitListFields(eventId, target), cancellationToken).ConfigureAwait(false))["result"]!;

    public async Task<bool> RemoveFromReadyEventWaitListAsync(int eventId, ulong? target, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("readyEventRemoveFromWaitList", WaitListFields(eventId, target), cancellationToken).ConfigureAwait(false))["result"]!;

    public async Task<ReadyEventInfo> GetReadyEventInfoAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("readyEventInfo", new JsonObject { ["eventId"] = eventId }, cancellationToken).ConfigureAwait(false);
        return new ReadyEventInfo(
            [.. reply["eventIds"]!.AsArray().Select(id => (int)id!)],
            (bool)reply["hasEvent"]!,
            (bool)reply["isSet"]!,
            (bool)reply["isCompleted"]!,
            (bool)reply["isCompletionProcessing"]!,
            [.. reply["waitList"]!.AsArray().Select(w => new ReadyEventWaiter(
                ulong.Parse((string)w!["guid"]!, CultureInfo.InvariantCulture), (string)w["status"]!))]);
    }

    private static JsonObject WaitListFields(int eventId, ulong? target)
    {
        var fields = new JsonObject { ["eventId"] = eventId };
        if (target is { } guid)
            fields["target"] = FormatGuid(guid);
        return fields;
    }
}
