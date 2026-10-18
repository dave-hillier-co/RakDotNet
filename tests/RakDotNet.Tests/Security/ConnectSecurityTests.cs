using RakDotNet.Security;

namespace RakDotNet.Tests.Security;

public class ConnectSecurityTests
{
    [Fact]
    public void Insecure_has_no_keys()
    {
        Assert.Equal(PublicKeyMode.InsecureConnection, ConnectSecurity.Insecure.Mode);
        Assert.Null(ConnectSecurity.Insecure.ServerPublicKey);
        Assert.Null(ConnectSecurity.Insecure.ClientIdentity);
    }

    [Fact]
    public void AcceptAnyPublicKey_has_no_keys()
    {
        Assert.Equal(PublicKeyMode.AcceptAnyPublicKey, ConnectSecurity.AcceptAnyPublicKey.Mode);
        Assert.Null(ConnectSecurity.AcceptAnyPublicKey.ServerPublicKey);
    }

    [Fact]
    public void KnownServerKey_requires_a_key()
    {
        Assert.Throws<ArgumentNullException>(() => ConnectSecurity.KnownServerKey(null!));
    }
}
