using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OpenConnectionReply2Tests
{
    // RakPeer.cpp:5300-5305: ID, aligned magic, server GUID, the client's address as the server sees it, MTU,
    // then requiresSecurity written as a bool, which BitStream.h:1130-1136 encodes as a single bit. The
    // datagram is 34 bytes plus one byte holding that bit.
    private const string ReplyHex = "08" + Codec.Magic + Codec.GuidHex + "0480fffffe4abc" + "04b0" + "00";
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 19132);

    [Fact]
    public void Reply_encodes_as_id_magic_guid_address_mtu_and_a_security_bit()
    {
        var bytes = Codec.Encode(new OpenConnectionReply2(Codec.Guid, Client, 1200));

        Assert.Equal(ReplyHex, Convert.ToHexStringLower(bytes));
        Assert.Equal(35, bytes.Length);
    }

    [Fact]
    public void Reply_decodes_every_field()
    {
        var reply = Codec.Decode<OpenConnectionReply2>(ReplyHex);

        Assert.Equal(Codec.Guid, reply.ServerGuid);
        Assert.Equal(Client, reply.ClientAddress);
        Assert.Equal(1200, reply.MtuSize);
        Assert.False(reply.RequiresSecurity);
    }

    [Fact]
    public void Security_flag_is_the_top_bit_of_the_last_byte()
    {
        // A port that writes the flag as a whole 0x01 byte is still read as false here, as in C++.
        var bytes = Convert.FromHexString(ReplyHex);
        bytes[34] = 0x01;

        Assert.True(Codec.TryDecode<OpenConnectionReply2>(bytes, out var reply));
        Assert.False(reply.RequiresSecurity);
    }

    [Fact]
    public void Reply_requiring_security_carries_the_handshake_answer_after_the_flag_byte()
    {
        // RakPeer.cpp:5300-5305 and 5405: the flag bit, then WriteAlignedBytes of the 128-byte answer, which starts at
        // the next byte boundary.
        var answer = Enumerable.Range(0, OpenConnectionReply2.AnswerLength).Select(i => (byte)(i + 3)).ToArray();

        var bytes = Codec.Encode(new OpenConnectionReply2(Codec.Guid, Client, 1200, RequiresSecurity: true, Answer: answer));

        Assert.Equal(ReplyHex[..^2] + "80" + Convert.ToHexStringLower(answer), Convert.ToHexStringLower(bytes));
    }

    [Fact]
    public void Reply_requiring_security_decodes_the_answer()
    {
        var answer = Enumerable.Range(0, OpenConnectionReply2.AnswerLength).Select(i => (byte)(255 - i)).ToArray();

        var reply = Codec.Decode<OpenConnectionReply2>(ReplyHex[..^2] + "80" + Convert.ToHexStringLower(answer));

        Assert.True(reply.RequiresSecurity);
        Assert.Equal(answer, reply.Answer.ToArray());
        Assert.Equal(1200, reply.MtuSize);
    }

    [Fact]
    public void Reply_requiring_security_without_a_whole_answer_reports_the_flag_and_fails()
    {
        // RakPeer.cpp:4894-4898 reads the answer with ReadAlignedBytes, which fails when it is cut short.
        var bytes = Convert.FromHexString(ReplyHex[..^2] + "80" + new string('a', 2 * 127));

        Assert.False(Codec.TryDecode<OpenConnectionReply2>(bytes, out var reply));
        Assert.True(reply.RequiresSecurity);
        Assert.Equal(Codec.Guid, reply.ServerGuid);
        Assert.True(reply.Answer.IsEmpty);
    }

    [Fact]
    public void Encoding_with_security_needs_a_whole_answer()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(new OpenConnectionReply2(1, Client, 1200, RequiresSecurity: true)));
    }

    [Fact]
    public void Reply_round_trips()
    {
        var reply = new OpenConnectionReply2(5, new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1), 576);

        Assert.True(Codec.TryDecode<OpenConnectionReply2>(Codec.Encode(reply), out var decoded));
        Assert.Equal(reply, decoded);
    }

    [Fact]
    public void Truncated_reply_fails()
    {
        Codec.AssertAllTruncationsFail<OpenConnectionReply2>(Convert.FromHexString(ReplyHex));
    }

    [Fact]
    public void Reply_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<OpenConnectionReply2>(Convert.FromHexString(ReplyHex), MessageId.OpenConnectionReply2);
    }

    [Fact]
    public void Reply_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<OpenConnectionReply2>(Convert.FromHexString(ReplyHex), 1);
    }
}
