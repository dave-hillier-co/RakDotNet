using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The twoWayAuth* commands on a <see cref="TwoWayAuthentication"/>, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : ITwoWayAuthenticationDriver
{
    private TwoWayAuthentication? _twoWayAuthentication;

    public async Task AttachTwoWayAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("twoWayAuthAttach");
        if (_twoWayAuthentication is not null)
            return;
        _twoWayAuthentication = new TwoWayAuthentication();
        await AttachAsync(peer, _twoWayAuthentication, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> AddPasswordAsync(string identifier, string password, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireTwoWayAuthentication().AddPassword(identifier, password));

    public Task<bool> ChallengeAsync(string identifier, ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(RequireTwoWayAuthentication().Challenge(identifier, new RakNetGuid(target)));

    public Task ClearTwoWayAuthenticationAsync(CancellationToken cancellationToken = default)
    {
        RequireTwoWayAuthentication().Clear();
        return Task.CompletedTask;
    }

    // interop-peer's RequireTwoWayAuthentication.
    private TwoWayAuthentication RequireTwoWayAuthentication() =>
        _twoWayAuthentication ?? throw new PeerCommandException("twoWayAuth", "no TwoWayAuthentication is attached; send twoWayAuthAttach first");
}
