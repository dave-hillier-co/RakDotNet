using System.Text.Json;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/tunnel.json): what cat's tunnel accepts and refuses.
// Key pairs offered to the server, the client and as a client identity; identity proofs tampered with field by
// field; tampered answers; and datagrams that are corrupted, truncated, extended, reflected, swapped, replayed
// or sent to another session. Every run uses recorded CSPRNG draws, so the accepted results match byte for byte.
public class TunnelTests
{
    private static readonly JsonElement Vectors = CatVectors.Load("tunnel");

    public static TheoryData<string> Keys() =>
        new(Vectors.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(Keys))]
    public void Key_pairs_are_accepted_and_refused_as_cat_does(string name)
    {
        var c = Find("keys", name);
        var publicKey = c.Hex("publicKey");
        var privateKey = c.Hex("privateKey");

        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        using (var server = new ServerEasyHandshake(serverRandom))
            Assert.Equal(c.GetProperty("serverInitialized").GetBoolean(), server.Initialize(publicKey, privateKey));
        Assert.Equal(0, serverRandom.Remaining);

        using (var client = new ClientEasyHandshake(SystemSecureRandom.Instance))
            Assert.Equal(c.GetProperty("clientInitialized").GetBoolean(), client.Initialize(publicKey));
        Assert.Equal(c.GetProperty("clientInitialized").GetBoolean(), EasyHandshake.IsValidPublicKey(publicKey));

        using var identity = new ClientEasyHandshake(SystemSecureRandom.Instance);
        Assert.True(identity.Initialize(Find("keys", "valid").Hex("publicKey")));
        Assert.Equal(c.GetProperty("identitySet").GetBoolean(), identity.SetIdentity(publicKey, privateKey));
    }

    public static TheoryData<string> Identities() =>
        new(Vectors.GetProperty("identities").EnumerateArray().Select(k => k.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(Identities))]
    public void Identity_proofs_are_verified_as_cat_does(string name)
    {
        var c = Find("identities", name);
        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        var clientRandom = new ReplayRandom(c.Hex("clientRandom"));
        using var server = new ServerEasyHandshake(serverRandom);
        using var client = new ClientEasyHandshake(clientRandom);
        Assert.True(server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey")));
        Assert.True(client.Initialize(c.Hex("serverPublicKey")));
        Assert.True(client.SetIdentity(c.Hex("clientPublicKey"), c.Hex("clientPrivateKey")));

        var challenge = new byte[EasyHandshake.ChallengeBytes];
        Assert.True(client.GenerateChallenge(challenge));
        CatTraffic.AssertHex(c.Hex("challenge"), challenge);
        var answer = new byte[EasyHandshake.AnswerBytes];
        Assert.True(server.ProcessChallenge(challenge, answer, new AuthenticatedEncryption()));
        CatTraffic.AssertHex(c.Hex("answer"), answer);
        var identity = new byte[EasyHandshake.IdentityBytes];
        Assert.Equal(c.GetProperty("answerProcessed").GetBoolean(),
                     client.ProcessAnswerWithIdentity(answer, identity, new AuthenticatedEncryption()));
        CatTraffic.AssertHex(c.Hex("identity"), identity);

        foreach (var v in c.GetProperty("verifications").EnumerateArray())
        {
            var verifiedKey = new byte[EasyHandshake.PublicKeyBytes];
            var verified = server.VerifyInitiatorIdentity(answer, v.Hex("identity"), verifiedKey);
            Assert.True(v.GetProperty("verified").GetBoolean() == verified, v.GetProperty("name").GetString());
            if (verified)
                CatTraffic.AssertHex(v.Hex("verifiedPublicKey"), verifiedKey);
        }
        Assert.Equal(0, serverRandom.Remaining);
        Assert.Equal(0, clientRandom.Remaining);
    }

    [Fact]
    public void A_tampered_answer_is_refused_and_the_genuine_one_still_keys_the_client()
    {
        var c = Vectors.GetProperty("answers");
        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        var clientRandom = new ReplayRandom(c.Hex("clientRandom"));
        using var server = new ServerEasyHandshake(serverRandom);
        using var client = new ClientEasyHandshake(clientRandom);
        Assert.True(server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey")));
        Assert.True(client.Initialize(c.Hex("serverPublicKey")));
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        Assert.True(client.GenerateChallenge(challenge));
        CatTraffic.AssertHex(c.Hex("challenge"), challenge);
        var answer = new byte[EasyHandshake.AnswerBytes];
        Assert.True(server.ProcessChallenge(challenge, answer, new AuthenticatedEncryption()));
        CatTraffic.AssertHex(c.Hex("answer"), answer);

        foreach (var attempt in c.GetProperty("attempts").EnumerateArray())
            Assert.True(attempt.GetProperty("processed").GetBoolean() == client.ProcessAnswer(attempt.Hex("answer"), new AuthenticatedEncryption()),
                        $"offset {attempt.GetProperty("offset").GetInt32()}");
        Assert.Equal(0, serverRandom.Remaining);
        Assert.Equal(0, clientRandom.Remaining);
    }

    [Fact]
    public void Corrupted_truncated_extended_reflected_swapped_replayed_and_foreign_datagrams_are_refused_as_cat_does()
    {
        var c = Vectors.GetProperty("datagrams");
        var (client, server) = Keyed(c);
        CatTraffic.Replay(c.GetProperty("traffic"), client, server);

        var other = c.GetProperty("otherSession");
        var (otherClient, otherServer) = Keyed(other);
        CatTraffic.Replay(other.GetProperty("traffic"), otherClient, otherServer);
    }

    [Fact]
    public void A_datagram_shorter_than_the_overhead_is_refused_without_reading_it()
    {
        var (client, server) = Keyed(Vectors.GetProperty("datagrams"));
        for (var length = 0; length < AuthenticatedEncryption.OverheadBytes; length++)
        {
            Assert.False(server.Decrypt(new byte[length], out var plainLength));
            Assert.Equal(0, plainLength);
            Assert.False(client.Decrypt(new byte[length], out _));
        }
    }

    private static (AuthenticatedEncryption Client, AuthenticatedEncryption Server) Keyed(JsonElement c)
    {
        var serverRandom = new ReplayRandom(c.Hex("serverRandom"));
        var clientRandom = new ReplayRandom(c.Hex("clientRandom"));
        using var server = new ServerEasyHandshake(serverRandom);
        using var client = new ClientEasyHandshake(clientRandom);
        Assert.True(server.Initialize(c.Hex("serverPublicKey"), c.Hex("serverPrivateKey")));
        Assert.True(client.Initialize(c.Hex("serverPublicKey")));
        var challenge = new byte[EasyHandshake.ChallengeBytes];
        Assert.True(client.GenerateChallenge(challenge));
        var answer = new byte[EasyHandshake.AnswerBytes];
        var serverAuth = new AuthenticatedEncryption();
        var clientAuth = new AuthenticatedEncryption();
        Assert.True(server.ProcessChallenge(challenge, answer, serverAuth));
        Assert.True(client.ProcessAnswer(answer, clientAuth));
        Assert.Equal(0, serverRandom.Remaining);
        Assert.Equal(0, clientRandom.Remaining);
        return (clientAuth, serverAuth);
    }

    private static JsonElement Find(string section, string name) =>
        Vectors.GetProperty(section).EnumerateArray().Single(e => e.GetProperty("name").GetString() == name);
}
