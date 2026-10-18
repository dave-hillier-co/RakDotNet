namespace RakDotNet.Security;

/// <summary>RakNet's PublicKeyMode.</summary>
public enum PublicKeyMode : byte
{
    /// <summary>PKM_INSECURE_CONNECTION</summary>
    InsecureConnection,

    /// <summary>PKM_ACCEPT_ANY_PUBLIC_KEY</summary>
    AcceptAnyPublicKey,

    /// <summary>PKM_USE_KNOWN_PUBLIC_KEY</summary>
    UseKnownPublicKey,

    /// <summary>PKM_USE_TWO_WAY_AUTHENTICATION</summary>
    UseTwoWayAuthentication,
}

/// <summary>
/// Client security for <see cref="ConnectOptions.Security"/>: RakNet's PublicKey argument to Connect. Handshake
/// rejections arrive as RemoteSystemRequiresPublicKey, OurSystemRequiresSecurity or PublicKeyMismatch.
/// </summary>
public sealed class ConnectSecurity
{
    private ConnectSecurity(PublicKeyMode mode, RakPublicKey? serverPublicKey, RakKeyPair? clientIdentity)
    {
        Mode = mode;
        ServerPublicKey = serverPublicKey;
        ClientIdentity = clientIdentity;
    }

    /// <summary>PKM_INSECURE_CONNECTION</summary>
    public static ConnectSecurity Insecure { get; } = new(PublicKeyMode.InsecureConnection, null, null);

    /// <summary>PKM_ACCEPT_ANY_PUBLIC_KEY</summary>
    public static ConnectSecurity AcceptAnyPublicKey { get; } = new(PublicKeyMode.AcceptAnyPublicKey, null, null);

    /// <summary>PKM_USE_KNOWN_PUBLIC_KEY</summary>
    public static ConnectSecurity KnownServerKey(RakPublicKey serverPublicKey)
    {
        ArgumentNullException.ThrowIfNull(serverPublicKey);
        return new(PublicKeyMode.UseKnownPublicKey, serverPublicKey, null);
    }

    /// <summary>PKM_USE_TWO_WAY_AUTHENTICATION</summary>
    public static ConnectSecurity TwoWayAuthentication(RakPublicKey serverPublicKey, RakKeyPair clientIdentity)
    {
        ArgumentNullException.ThrowIfNull(serverPublicKey);
        ArgumentNullException.ThrowIfNull(clientIdentity);
        return new(PublicKeyMode.UseTwoWayAuthentication, serverPublicKey, clientIdentity);
    }

    public PublicKeyMode Mode { get; }

    public RakPublicKey? ServerPublicKey { get; }

    public RakKeyPair? ClientIdentity { get; }
}
