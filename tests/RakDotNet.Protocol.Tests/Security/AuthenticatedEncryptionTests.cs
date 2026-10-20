using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

public class AuthenticatedEncryptionTests
{
    [Fact]
    public void Encrypting_and_decrypting_a_datagram_allocates_nothing()
    {
        var (client, server) = Keyed();
        var buffer = new byte[1400 + AuthenticatedEncryption.OverheadBytes];
        for (var i = 0; i < 10; i++)
            RoundTrip(client, server, buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            RoundTrip(client, server, buffer);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Each_datagram_takes_the_next_iv()
    {
        var (client, server) = Keyed();
        var first = client.LocalIv;
        var buffer = new byte[AuthenticatedEncryption.OverheadBytes];
        Assert.True(client.Encrypt(buffer, 0, out _));
        Assert.Equal(first + 1, client.LocalIv);
        Assert.True(server.Decrypt(buffer, out var length));
        Assert.Equal(0, length);
        Assert.Equal(first + 1, server.RemoteIv);
    }

    private static void RoundTrip(AuthenticatedEncryption client, AuthenticatedEncryption server, byte[] buffer)
    {
        Assert.True(client.Encrypt(buffer, 1400, out var length));
        Assert.True(server.Decrypt(buffer.AsSpan(0, length), out _));
    }

    private static (AuthenticatedEncryption Client, AuthenticatedEncryption Server) Keyed()
    {
        using var maker = new EasyHandshake(SystemSecureRandom.Instance);
        var publicKey = new byte[EasyHandshake.PublicKeyBytes];
        var privateKey = new byte[EasyHandshake.PrivateKeyBytes];
        maker.GenerateServerKey(publicKey, privateKey);
        using var serverHandshake = new ServerEasyHandshake(SystemSecureRandom.Instance);
        using var clientHandshake = new ClientEasyHandshake(SystemSecureRandom.Instance);
        serverHandshake.Initialize(publicKey, privateKey);
        clientHandshake.Initialize(publicKey);
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        clientHandshake.GenerateChallenge(challenge);
        var answer = new byte[EasyHandshake.AnswerBytes];
        var server = new AuthenticatedEncryption();
        var client = new AuthenticatedEncryption();
        serverHandshake.ProcessChallenge(challenge, answer, server);
        clientHandshake.ProcessAnswer(answer, client);
        return (client, server);
    }
}
