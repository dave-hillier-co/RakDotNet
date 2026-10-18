using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OpenConnectionRequest1Tests
{
    // RakPeer.cpp:5758-5761: ID, aligned magic, protocol version, then PadWithZeroToByteLength(mtu - UDP_HEADER_SIZE)
    // (BitStream.cpp:744-753), so the whole UDP payload is mtu - 28 bytes.
    private const string Header = "05" + Codec.Magic + "06";

    [Theory]
    [InlineData(1492, 1464)]
    [InlineData(1200, 1172)]
    [InlineData(576, 548)]
    public void Request_is_padded_so_payload_plus_udp_header_equals_the_mtu(int mtu, int expectedLength)
    {
        var bytes = Codec.Encode(new OpenConnectionRequest1(6, mtu));

        Assert.Equal(expectedLength, bytes.Length);
        Assert.Equal(Header, Convert.ToHexStringLower(bytes.AsSpan(0, 18)));
        Assert.All(bytes[18..], b => Assert.Equal(0, b));
    }

    [Theory]
    [InlineData(1492)]
    [InlineData(1200)]
    [InlineData(576)]
    public void Decoder_recovers_the_mtu_from_the_datagram_length_as_the_cpp_server_does(int mtu)
    {
        // RakPeer.cpp:5186: the server replies with length + UDP_HEADER_SIZE.
        var bytes = new byte[mtu - 28];
        Convert.FromHexString(Header).CopyTo(bytes, 0);

        Assert.True(Codec.TryDecode<OpenConnectionRequest1>(bytes, out var request));
        Assert.Equal(mtu, request.MtuSize);
        Assert.Equal(6, request.ProtocolVersion);
    }

    [Fact]
    public void Decoder_consumes_the_padding()
    {
        var bytes = Codec.Encode(new OpenConnectionRequest1(6, 576));
        var reader = new BitStreamReader(bytes);

        Assert.True(OpenConnectionRequest1.TryRead(ref reader, out _));
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void Decoder_reports_a_foreign_protocol_version_rather_than_rejecting_it()
    {
        // RakPeer.cpp:5137-5138 reads the version and answers ID_INCOMPATIBLE_PROTOCOL_VERSION; that is
        // peer logic, so the codec just decodes it.
        var bytes = Codec.Encode(new OpenConnectionRequest1(11, 1200));

        Assert.True(Codec.TryDecode<OpenConnectionRequest1>(bytes, out var request));
        Assert.Equal(11, request.ProtocolVersion);
        Assert.Equal(1200, request.MtuSize);
    }

    [Theory]
    [InlineData(1492)]
    [InlineData(576)]
    [InlineData(46)]
    public void Request_round_trips(int mtu)
    {
        var request = new OpenConnectionRequest1(6, mtu);

        Assert.True(Codec.TryDecode<OpenConnectionRequest1>(Codec.Encode(request), out var decoded));
        Assert.Equal(request, decoded);
    }

    [Fact]
    public void Mtu_too_small_to_hold_the_header_is_a_programming_error()
    {
        // 18 header bytes + 28 = 46 is the smallest MTU whose padding rule can be honoured exactly.
        Assert.Throws<ArgumentOutOfRangeException>(() => Codec.Encode(new OpenConnectionRequest1(6, 45)));
    }

    [Fact]
    public void Request_without_a_protocol_version_fails()
    {
        // RakPeer.cpp:5127 requires length > 1 + sizeof(OFFLINE_MESSAGE_DATA_ID).
        Codec.AssertPrefixesShorterThanFail<OpenConnectionRequest1>(Codec.Encode(new OpenConnectionRequest1(6, 576)), 18);
    }

    [Fact]
    public void Request_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<OpenConnectionRequest1>(
            Codec.Encode(new OpenConnectionRequest1(6, 576)),
            MessageId.OpenConnectionRequest1
        );
    }

    [Fact]
    public void Request_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<OpenConnectionRequest1>(Codec.Encode(new OpenConnectionRequest1(6, 576)), 1);
    }
}
