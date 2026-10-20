using System.Text.Json;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/handshake.json): cat's ServerEasyHandshake and
// ClientEasyHandshake run with recorded CSPRNG draws, so the C# port must produce the same challenge, answer,
// identity proof, derived keys and encrypted traffic byte for byte.
public class EasyHandshakeTests
{
    private static readonly JsonElement Vectors = CatVectors.Load("handshake");

    public static TheoryData<int> Handshakes() => new(Enumerable.Range(0, Vectors.GetProperty("handshakes").GetArrayLength()));

    [Theory]
    [MemberData(nameof(Handshakes))]
    public void The_handshake_and_its_traffic_match_cat(int index)
    {
        var c = Vectors.GetProperty("handshakes")[index];
        var twoWay = c.GetProperty("twoWayAuthentication").GetBoolean();

        AssertKeyPair(c.Hex("serverKeyRandom"), c.Hex("serverPublicKey"), c.Hex("serverPrivateKey"));
        if (twoWay)
            AssertKeyPair(c.Hex("clientKeyRandom"), c.Hex("clientPublicKey"), c.Hex("clientPrivateKey"));

        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        var clientRandom = new ReplayRandom(c.Hex("clientRandom"));
        using var server = new ServerEasyHandshake(serverRandom);
        using var client = new ClientEasyHandshake(clientRandom);

        Assert.True(server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey")));
        server.FillCookieJar(new CookieJar());
        Assert.True(client.Initialize(c.Hex("serverPublicKey")));
        if (twoWay)
            Assert.True(client.SetIdentity(c.Hex("clientPublicKey"), c.Hex("clientPrivateKey")));

        var challenge = new byte[EasyHandshake.ChallengeBytes];
        Assert.True(client.GenerateChallenge(challenge));
        CatTraffic.AssertHex(c.Hex("challenge"), challenge);

        var serverAuth = new AuthenticatedEncryption();
        var answer = new byte[EasyHandshake.AnswerBytes];
        Assert.True(server.ProcessChallenge(challenge, answer, serverAuth));
        CatTraffic.AssertHex(c.Hex("answer"), answer);

        var clientAuth = new AuthenticatedEncryption();
        if (twoWay)
        {
            var identity = new byte[EasyHandshake.IdentityBytes];
            Assert.True(client.ProcessAnswerWithIdentity(answer, identity, clientAuth));
            CatTraffic.AssertHex(c.Hex("identity"), identity);

            var verifiedKey = new byte[EasyHandshake.PublicKeyBytes];
            Assert.True(server.VerifyInitiatorIdentity(answer, identity, verifiedKey));
            CatTraffic.AssertHex(c.Hex("verifiedPublicKey"), verifiedKey);

            var tampered = (byte[])identity.Clone();
            tampered[EasyHandshake.Bytes * 3] ^= 1;
            Assert.False(server.VerifyInitiatorIdentity(answer, tampered, new byte[EasyHandshake.PublicKeyBytes]));
        }
        else
        {
            Assert.True(client.ProcessAnswer(answer, clientAuth));
        }

        AssertKeyState(c.GetProperty("serverAuth"), serverAuth);
        AssertKeyState(c.GetProperty("clientAuth"), clientAuth);
        Assert.Equal(0, serverRandom.Remaining);
        Assert.Equal(0, clientRandom.Remaining);

        CatTraffic.Replay(c.GetProperty("traffic"), clientAuth, serverAuth);
    }

    [Fact]
    public void The_server_rekeys_its_ephemeral_key_after_every_hundred_challenges_and_rejects_bad_input_as_cat_does()
    {
        var c = Vectors.GetProperty("serverChallenges")[0];
        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        var clientRandom = new ReplayRandom(c.Hex("clientRandom"));
        using var server = new ServerEasyHandshake(serverRandom);
        using var client = new ClientEasyHandshake(clientRandom);
        Assert.True(server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey")));
        Assert.True(client.Initialize(c.Hex("serverPublicKey")));
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        client.GenerateChallenge(challenge);
        CatTraffic.AssertHex(c.Hex("challenge"), challenge);

        var answer = new byte[EasyHandshake.AnswerBytes];
        foreach (var expected in c.GetProperty("answers").EnumerateArray())
        {
            Assert.Equal(expected.GetProperty("ok").GetBoolean(), server.ProcessChallenge(challenge, answer, new AuthenticatedEncryption()));
            CatTraffic.AssertHex(expected.Hex("answer"), answer);
        }

        var scratch = new byte[EasyHandshake.AnswerBytes];
        Assert.Equal(c.GetProperty("invalidChallengeAnswered").GetBoolean(), server.ProcessChallenge(c.Hex("invalidChallenge"), scratch, new AuthenticatedEncryption()));
        Assert.Equal(c.GetProperty("identityChallengeAnswered").GetBoolean(), server.ProcessChallenge(c.Hex("identityChallenge"), scratch, new AuthenticatedEncryption()));

        var lastAnswer = new byte[EasyHandshake.AnswerBytes];
        Assert.True(server.ProcessChallenge(challenge, lastAnswer, new AuthenticatedEncryption()));
        CatTraffic.AssertHex(c.Hex("lastAnswer"), lastAnswer);

        var tampered = (byte[])lastAnswer.Clone();
        tampered[^1] ^= 1;
        Assert.Equal(c.GetProperty("tamperedMacProcessed").GetBoolean(), client.ProcessAnswer(tampered, new AuthenticatedEncryption()));
        tampered = (byte[])lastAnswer.Clone();
        tampered[EasyHandshake.Bytes * 2] ^= 1;
        Assert.Equal(c.GetProperty("tamperedNonceProcessed").GetBoolean(), client.ProcessAnswer(tampered, new AuthenticatedEncryption()));
        Assert.Equal(c.GetProperty("lastAnswerProcessed").GetBoolean(), client.ProcessAnswer(lastAnswer, new AuthenticatedEncryption()));
        Assert.Equal(0, serverRandom.Remaining);
        Assert.Equal(0, clientRandom.Remaining);
    }

    public static TheoryData<int> Counters() => new(Enumerable.Range(0, Vectors.GetProperty("reconstructCounter").GetArrayLength()));

    [Theory]
    [MemberData(nameof(Counters))]
    public void Iv_reconstruction_matches_cat(int index)
    {
        var c = Vectors.GetProperty("reconstructCounter")[index];
        Assert.Equal(c.UInt64("result"), AuthenticatedEncryption.ReconstructCounter(c.UInt64("last"), c.GetProperty("partial").GetUInt32()));
    }

    [Fact]
    public void A_server_key_pair_from_the_system_csprng_completes_a_handshake()
    {
        using var maker = new EasyHandshake(SystemSecureRandom.Instance);
        var publicKey = new byte[EasyHandshake.PublicKeyBytes];
        var privateKey = new byte[EasyHandshake.PrivateKeyBytes];
        Assert.True(maker.GenerateServerKey(publicKey, privateKey));

        using var server = new ServerEasyHandshake(SystemSecureRandom.Instance);
        using var client = new ClientEasyHandshake(SystemSecureRandom.Instance);
        Assert.True(server.Initialize(publicKey, privateKey));
        Assert.True(client.Initialize(publicKey));
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        Assert.True(client.GenerateChallenge(challenge));
        var answer = new byte[EasyHandshake.AnswerBytes];
        var serverAuth = new AuthenticatedEncryption();
        var clientAuth = new AuthenticatedEncryption();
        Assert.True(server.ProcessChallenge(challenge, answer, serverAuth));
        Assert.True(client.ProcessAnswer(answer, clientAuth));

        var proof = new byte[EasyHandshake.ProofBytes];
        clientAuth.GenerateProof(proof);
        Assert.True(serverAuth.ValidateProof(proof));

        var buffer = new byte[5 + AuthenticatedEncryption.OverheadBytes];
        "hello"u8.CopyTo(buffer);
        Assert.True(clientAuth.Encrypt(buffer, 5, out var length));
        Assert.True(serverAuth.Decrypt(buffer.AsSpan(0, length), out var plainLength));
        Assert.Equal("hello"u8.ToArray(), buffer[..plainLength]);
    }

    [Fact]
    public void A_client_rejects_a_server_key_that_is_not_on_the_curve()
    {
        using var client = new ClientEasyHandshake(SystemSecureRandom.Instance);
        var key = Vectors.GetProperty("handshakes")[0].Hex("serverPublicKey");
        key[0] ^= 1;
        Assert.False(client.Initialize(key));
    }

    [Fact]
    public void Encrypt_needs_room_for_the_overhead()
    {
        var (clientAuth, _) = KeyedPair();
        var buffer = new byte[10 + AuthenticatedEncryption.OverheadBytes - 1];
        Assert.False(clientAuth.Encrypt(buffer, 10, out _));
    }

    private static (AuthenticatedEncryption Client, AuthenticatedEncryption Server) KeyedPair()
    {
        var c = Vectors.GetProperty("handshakes")[0];
        using var server = new ServerEasyHandshake(new ReplayRandom(c.Hex("serverRandom")));
        using var client = new ClientEasyHandshake(new ReplayRandom(c.Hex("clientRandom")));
        server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey"));
        server.FillCookieJar(new CookieJar());
        client.Initialize(c.Hex("serverPublicKey"));
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        client.GenerateChallenge(challenge);
        var answer = new byte[EasyHandshake.AnswerBytes];
        var serverAuth = new AuthenticatedEncryption();
        var clientAuth = new AuthenticatedEncryption();
        server.ProcessChallenge(challenge, answer, serverAuth);
        client.ProcessAnswer(answer, clientAuth);
        return (clientAuth, serverAuth);
    }

    private static void AssertKeyPair(byte[] random, byte[] publicKey, byte[] privateKey)
    {
        using var maker = new EasyHandshake(new ReplayRandom(random));
        var pub = new byte[EasyHandshake.PublicKeyBytes];
        var priv = new byte[EasyHandshake.PrivateKeyBytes];
        Assert.True(maker.GenerateServerKey(pub, priv));
        CatTraffic.AssertHex(publicKey, pub);
        CatTraffic.AssertHex(privateKey, priv);
    }

    private static void AssertKeyState(JsonElement expected, AuthenticatedEncryption auth)
    {
        var state = auth.ExportKeyState();
        Assert.Equal(expected.GetProperty("isInitiator").GetBoolean(), auth.IsInitiator);
        Assert.Equal(expected.UInt64("localIv"), auth.LocalIv);
        Assert.Equal(expected.UInt64("remoteIv"), auth.RemoteIv);
        CatTraffic.AssertHex(expected.Hex("localCipherState"), state.LocalCipherWords);
        CatTraffic.AssertHex(expected.Hex("remoteCipherState"), state.RemoteCipherWords);
        CatTraffic.AssertHex(expected.Hex("localMacInitial"), state.LocalMacInitial);
        CatTraffic.AssertHex(expected.Hex("localMacFinal"), state.LocalMacFinal);
        CatTraffic.AssertHex(expected.Hex("remoteMacInitial"), state.RemoteMacInitial);
        CatTraffic.AssertHex(expected.Hex("remoteMacFinal"), state.RemoteMacFinal);
    }
}
