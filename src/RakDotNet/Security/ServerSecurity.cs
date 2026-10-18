namespace RakDotNet.Security;

/// <summary>InitializeSecurity, through <see cref="RakPeerOptions.Security"/>.</summary>
public sealed record ServerSecurity(RakKeyPair Keys)
{
    /// <summary>bRequireClientKey: clients must use two-way authentication.</summary>
    public bool RequireClientKey { get; init; }
}
