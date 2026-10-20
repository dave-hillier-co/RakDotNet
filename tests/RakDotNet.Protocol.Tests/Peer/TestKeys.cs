using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Peer;

/// <summary>Key pairs from cat's own generator (EasyHandshake::GenerateServerKey), for handshakes that run for real.</summary>
internal sealed record TestKeyPair(byte[] Public, byte[] Private)
{
    public static TestKeyPair Generate()
    {
        var publicKey = new byte[EasyHandshake.PublicKeyBytes];
        var privateKey = new byte[EasyHandshake.PrivateKeyBytes];
        using var maker = new EasyHandshake(SystemSecureRandom.Instance);
        Assert.True(maker.GenerateServerKey(publicKey, privateKey));
        return new TestKeyPair(publicKey, privateKey);
    }

    public ServerSecurityOptions Server(bool requireClientKey = false) => new(Public, Private, requireClientKey);

    /// <summary>PKM_USE_KNOWN_PUBLIC_KEY with this key as the server's.</summary>
    public ClientSecurity Known() =>
        ClientSecurity.Create(ClientKeyMode.UseKnownPublicKey, Public, default, default, SystemSecureRandom.Instance)
        ?? throw new InvalidOperationException("GenerateConnectionRequestChallenge failed.");

    /// <summary>PKM_USE_TWO_WAY_AUTHENTICATION with this key as the server's and <paramref name="identity"/> as the client's.</summary>
    public ClientSecurity TwoWay(TestKeyPair identity) => TwoWay(identity.Public, identity.Private);

    public ClientSecurity TwoWay(byte[] identityPublic, byte[] identityPrivate) =>
        ClientSecurity.Create(ClientKeyMode.UseTwoWayAuthentication, Public, identityPublic, identityPrivate, SystemSecureRandom.Instance)
        ?? throw new InvalidOperationException("GenerateConnectionRequestChallenge failed.");

    public static ClientSecurity AcceptAny() =>
        ClientSecurity.Create(ClientKeyMode.AcceptAnyPublicKey, default, default, default, SystemSecureRandom.Instance)!;
}
