using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OpenConnectionReply1Tests
{
    // RakPeer.cpp:5164-5186: ID, aligned magic, server GUID, hasSecurity as an unsigned char (0 without
    // LIBCAT_SECURITY), MTU as uint16. 1492 = 0x05d4.
    private const string ReplyHex = "06" + Codec.Magic + Codec.GuidHex + "00" + "05d4";

    [Fact]
    public void Reply_encodes_as_id_magic_guid_security_byte_mtu()
    {
        Assert.Equal(ReplyHex, Codec.EncodeHex(new OpenConnectionReply1(Codec.Guid, 1492)));
    }

    [Fact]
    public void Reply_decodes_every_field()
    {
        var reply = Codec.Decode<OpenConnectionReply1>(ReplyHex);

        Assert.Equal(Codec.Guid, reply.ServerGuid);
        Assert.False(reply.HasSecurity);
        Assert.Equal(1492, reply.MtuSize);
    }

    [Fact]
    public void Reply_round_trips()
    {
        var reply = new OpenConnectionReply1(ulong.MaxValue, 576);

        Assert.True(Codec.TryDecode<OpenConnectionReply1>(Codec.Encode(reply), out var decoded));
        Assert.Equal(reply, decoded);
    }

    [Fact]
    public void Reply_from_a_server_with_security_reports_the_flag_and_fails()
    {
        // RakPeer.cpp:4752-4755: with hasSecurity the cookie and public key follow, which SecureOpenConnectionReply1 reads.
        var bytes = Convert.FromHexString(ReplyHex);
        bytes[25] = 1;

        Assert.False(Codec.TryDecode<OpenConnectionReply1>(bytes, out var reply));
        Assert.True(reply.HasSecurity);
        Assert.Equal(Codec.Guid, reply.ServerGuid);
    }

    [Fact]
    public void Encoding_with_security_is_left_to_SecureOpenConnectionReply1()
    {
        Assert.Throws<NotSupportedException>(() => Codec.Encode(new OpenConnectionReply1(1, 1492, HasSecurity: true)));
    }

    [Fact]
    public void Truncated_reply_fails()
    {
        Codec.AssertAllTruncationsFail<OpenConnectionReply1>(Convert.FromHexString(ReplyHex));
    }

    [Fact]
    public void Reply_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<OpenConnectionReply1>(Convert.FromHexString(ReplyHex), MessageId.OpenConnectionReply1);
    }

    [Fact]
    public void Reply_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<OpenConnectionReply1>(Convert.FromHexString(ReplyHex), 1);
    }
}
