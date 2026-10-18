using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class UnconnectedPongTests
{
    // RakPeer.cpp:4650-4657: ID, echoed ping time (uint64), server GUID, aligned magic, then the raw bytes
    // of the offline ping response.
    private const string Header = "1c" + Codec.TimeHex + Codec.GuidHex + Codec.Magic;
    private const string HelloHex = "68656c6c6f";

    [Fact]
    public void Pong_encodes_as_id_time_guid_magic_response()
    {
        var pong = new UnconnectedPong(Codec.Time, Codec.Guid, "hello"u8.ToArray());

        Assert.Equal(Header + HelloHex, Codec.EncodeHex(pong));
    }

    [Fact]
    public void Pong_decodes_every_field()
    {
        var pong = Codec.Decode<UnconnectedPong>(Header + HelloHex);

        Assert.Equal(Codec.Time, pong.SendPingTime);
        Assert.Equal(Codec.Guid, pong.ServerGuid);
        Assert.Equal("hello"u8.ToArray(), pong.ResponseData.ToArray());
    }

    [Fact]
    public void Pong_with_no_response_data_is_33_bytes()
    {
        var bytes = Codec.Encode(new UnconnectedPong(Codec.Time, Codec.Guid, ReadOnlyMemory<byte>.Empty));

        Assert.Equal(Header, Convert.ToHexStringLower(bytes));
        Assert.True(Codec.TryDecode<UnconnectedPong>(bytes, out var pong));
        Assert.True(pong.ResponseData.IsEmpty);
    }

    [Fact]
    public void Pong_round_trips()
    {
        var pong = new UnconnectedPong(7, 8, new byte[] { 0, 1, 2, 255 });

        Assert.True(Codec.TryDecode<UnconnectedPong>(Codec.Encode(pong), out var decoded));
        Assert.Equal(pong.SendPingTime, decoded.SendPingTime);
        Assert.Equal(pong.ServerGuid, decoded.ServerGuid);
        Assert.Equal(pong.ResponseData.ToArray(), decoded.ResponseData.ToArray());
    }

    [Fact]
    public void Pong_with_399_bytes_of_response_data_is_accepted()
    {
        // RakPeer.cpp:4682 accepts length < 33 + MAX_OFFLINE_DATA_LENGTH (400, RakPeer.cpp:127).
        var bytes = Codec.Encode(new UnconnectedPong(1, 2, new byte[399]));

        Assert.True(Codec.TryDecode<UnconnectedPong>(bytes, out _));
    }

    [Fact]
    public void Pong_with_400_bytes_of_response_data_is_rejected_as_the_cpp_client_does()
    {
        var bytes = Codec.Encode(new UnconnectedPong(1, 2, new byte[400]));

        Assert.False(Codec.TryDecode<UnconnectedPong>(bytes, out _));
    }

    [Fact]
    public void Pong_response_limit_can_be_raised_for_other_protocol_profiles()
    {
        var bytes = Codec.Encode(new UnconnectedPong(1, 2, new byte[1000]));
        var reader = new BitStreamReader(bytes);

        Assert.True(UnconnectedPong.TryRead(ref reader, out var pong, maxResponseDataLength: 1000));
        Assert.Equal(1000, pong.ResponseData.Length);
    }

    [Fact]
    public void Pong_shorter_than_its_header_fails()
    {
        Codec.AssertPrefixesShorterThanFail<UnconnectedPong>(Convert.FromHexString(Header + HelloHex), 33);
    }

    [Fact]
    public void Pong_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<UnconnectedPong>(Convert.FromHexString(Header + HelloHex), MessageId.UnconnectedPong);
    }

    [Fact]
    public void Pong_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<UnconnectedPong>(Convert.FromHexString(Header + HelloHex), 17);
    }
}
