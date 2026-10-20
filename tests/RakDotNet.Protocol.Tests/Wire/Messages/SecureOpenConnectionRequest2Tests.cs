using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class SecureOpenConnectionRequest2Tests
{
    // RakPeer.cpp:4758-4853 (client) and 5208-5253 (server) when the server reported hasSecurity: ID, aligned magic,
    // the cookie as a big-endian uint32, clientWroteChallenge as an unsigned char, the 64-byte challenge aligned when
    // that byte is 1, then the server's address as the client addressed it, MTU and client GUID.
    private const string CookieHex = "cafebabe";
    private const string Tail = "0480fffffe4abc" + "05d4" + Codec.GuidHex;
    private static readonly byte[] Challenge = Enumerable.Range(0, SecureOpenConnectionRequest2.ChallengeLength).Select(i => (byte)(i * 3)).ToArray();
    private static readonly string WithChallengeHex = "07" + Codec.Magic + CookieHex + "01" + Convert.ToHexStringLower(Challenge) + Tail;
    private const string WithoutChallengeHex = "07" + Codec.Magic + CookieHex + "00" + Tail;
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 19132);

    [Fact]
    public void Request_with_a_challenge_encodes_cookie_flag_and_challenge_after_the_magic()
    {
        Assert.Equal(WithChallengeHex, Codec.EncodeHex(new SecureOpenConnectionRequest2(0xcafebabe, Challenge, Server, 1492, Codec.Guid)));
    }

    [Fact]
    public void Request_without_a_challenge_writes_a_zero_flag()
    {
        // RakPeer.cpp:4803-4808: no client handshake, so the flag is 0 and no challenge follows.
        Assert.Equal(WithoutChallengeHex, Codec.EncodeHex(new SecureOpenConnectionRequest2(0xcafebabe, ReadOnlyMemory<byte>.Empty, Server, 1492, Codec.Guid)));
    }

    [Fact]
    public void Request_decodes_every_field()
    {
        var request = Codec.Decode<SecureOpenConnectionRequest2>(WithChallengeHex);

        Assert.Equal(0xcafebabeu, request.Cookie);
        Assert.Equal(Challenge, request.Challenge.ToArray());
        Assert.Equal(Server, request.ServerAddress);
        Assert.Equal(1492, request.MtuSize);
        Assert.Equal(Codec.Guid, request.ClientGuid);
    }

    [Fact]
    public void Any_non_zero_flag_means_a_challenge_follows()
    {
        // RakPeer.cpp:5229: if (clientWroteChallenge).
        var bytes = Convert.FromHexString(WithChallengeHex);
        bytes[21] = 0x7f;

        Assert.True(Codec.TryDecode<SecureOpenConnectionRequest2>(bytes, out var request));
        Assert.Equal(Challenge, request.Challenge.ToArray());
    }

    [Fact]
    public void Request_without_a_challenge_decodes_an_empty_one()
    {
        var request = Codec.Decode<SecureOpenConnectionRequest2>(WithoutChallengeHex);

        Assert.True(request.Challenge.IsEmpty);
        Assert.Equal(Codec.Guid, request.ClientGuid);
    }

    [Fact]
    public void A_challenge_of_the_wrong_length_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(new SecureOpenConnectionRequest2(1, new byte[10], Server, 1492, 2)));
    }

    [Fact]
    public void Truncated_request_fails()
    {
        Codec.AssertAllTruncationsFail<SecureOpenConnectionRequest2>(Convert.FromHexString(WithChallengeHex));
        Codec.AssertAllTruncationsFail<SecureOpenConnectionRequest2>(Convert.FromHexString(WithoutChallengeHex));
    }

    [Fact]
    public void Request_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<SecureOpenConnectionRequest2>(Convert.FromHexString(WithChallengeHex), MessageId.OpenConnectionRequest2);
    }

    [Fact]
    public void Request_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<SecureOpenConnectionRequest2>(Convert.FromHexString(WithChallengeHex), 1);
    }
}
