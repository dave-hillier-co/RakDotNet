using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class SecureOpenConnectionReply1Tests
{
    // RakPeer.cpp:5164-5186 with _using_security: ID, aligned magic, server GUID, hasSecurity 1 as an unsigned char,
    // the cookie as a big-endian uint32, the 64-byte public key aligned, then the MTU as uint16. 1200 = 0x04b0.
    private const string CookieHex = "cafebabe";
    private static readonly byte[] PublicKey = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
    private static readonly string ReplyHex = "06" + Codec.Magic + Codec.GuidHex + "01" + CookieHex + Convert.ToHexStringLower(PublicKey) + "04b0";

    [Fact]
    public void Reply_encodes_as_id_magic_guid_security_byte_cookie_key_mtu()
    {
        Assert.Equal(ReplyHex, Codec.EncodeHex(new SecureOpenConnectionReply1(Codec.Guid, 0xcafebabe, PublicKey, 1200)));
    }

    [Fact]
    public void Reply_decodes_every_field()
    {
        var reply = Codec.Decode<SecureOpenConnectionReply1>(ReplyHex);

        Assert.Equal(Codec.Guid, reply.ServerGuid);
        Assert.Equal(0xcafebabeu, reply.Cookie);
        Assert.Equal(PublicKey, reply.ServerPublicKey.ToArray());
        Assert.Equal(1200, reply.MtuSize);
    }

    [Fact]
    public void Any_non_zero_security_byte_means_security()
    {
        // RakPeer.cpp:4752: if (serverHasSecurity).
        var bytes = Convert.FromHexString(ReplyHex);
        bytes[25] = 0x80;

        Assert.True(Codec.TryDecode<SecureOpenConnectionReply1>(bytes, out _));
    }

    [Fact]
    public void A_reply_without_security_is_not_a_secure_reply()
    {
        var insecure = Codec.Encode(new OpenConnectionReply1(Codec.Guid, 1492));

        Assert.False(Codec.TryDecode<SecureOpenConnectionReply1>(insecure, out _));
    }

    [Fact]
    public void A_public_key_of_the_wrong_length_cannot_be_encoded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(new SecureOpenConnectionReply1(1, 1, new byte[63], 576)));
    }

    [Fact]
    public void Truncated_reply_fails()
    {
        Codec.AssertAllTruncationsFail<SecureOpenConnectionReply1>(Convert.FromHexString(ReplyHex));
    }

    [Fact]
    public void Other_ids_and_corrupt_magic_fail()
    {
        var bytes = Convert.FromHexString(ReplyHex);
        Codec.AssertOtherIdsFail<SecureOpenConnectionReply1>(bytes, MessageId.OpenConnectionReply1);
        Codec.AssertCorruptMagicFails<SecureOpenConnectionReply1>(bytes, 1);
    }
}
