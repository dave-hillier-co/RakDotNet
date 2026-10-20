using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : ITwoWayAuthenticationDriver
{
    public Task AttachTwoWayAuthenticationAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("twoWayAuthAttach", [], cancellationToken);

    public async Task<bool> AddPasswordAsync(string identifier, string password, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("twoWayAuthAddPassword", new JsonObject { ["identifier"] = identifier, ["password"] = password }, cancellationToken).ConfigureAwait(false))["result"]!;

    public async Task<bool> ChallengeAsync(string identifier, ulong target, CancellationToken cancellationToken = default) =>
        (bool)(await SendCommandAsync("twoWayAuthChallenge", new JsonObject { ["identifier"] = identifier, ["target"] = FormatGuid(target) }, cancellationToken).ConfigureAwait(false))["result"]!;

    public Task ClearTwoWayAuthenticationAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("twoWayAuthClear", [], cancellationToken);
}
