namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Attaches and drives a TwoWayAuthentication on a peer: the twoWayAuth* commands of docs/interop-protocol.md. Both
/// drivers implement it; every command but <see cref="AttachTwoWayAuthenticationAsync"/> fails until one is attached.
/// The results arrive as packets.
/// </summary>
public interface ITwoWayAuthenticationDriver
{
    /// <summary>AttachPlugin of a TwoWayAuthentication.</summary>
    Task AttachTwoWayAuthenticationAsync(CancellationToken cancellationToken = default);

    /// <summary>AddPassword</summary>
    Task<bool> AddPasswordAsync(string identifier, string password, CancellationToken cancellationToken = default);

    /// <summary>Challenge of the system with the GUID.</summary>
    Task<bool> ChallengeAsync(string identifier, ulong target, CancellationToken cancellationToken = default);

    /// <summary>Clear</summary>
    Task ClearTwoWayAuthenticationAsync(CancellationToken cancellationToken = default);
}
