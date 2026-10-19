using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class OutOfBandMessageTests
{
    // SendOutOfBand (RakPeer.cpp:2997-3040) writes WriteOutOfBandHeader (RakPeer.cpp:2979-2984): ID_OUT_OF_BAND_INTERNAL,
    // the sender GUID, the aligned magic, then the caller's bytes.
    private const string Header = "0d" + Codec.GuidHex + Codec.Magic;

    [Fact]
    public void Message_encodes_as_header_then_payload()
    {
        Assert.Equal(Header + "86cafe", Codec.EncodeHex(new OutOfBandMessage(Codec.Guid, new byte[] { 0x86, 0xCA, 0xFE })));
    }

    [Fact]
    public void Message_decodes_every_field()
    {
        var message = Codec.Decode<OutOfBandMessage>(Header + "86cafe");

        Assert.Equal(Codec.Guid, message.SenderGuid);
        Assert.Equal(new byte[] { 0x86, 0xCA, 0xFE }, message.Payload.ToArray());
    }

    [Fact]
    public void An_advertisement_is_an_out_of_band_message_whose_payload_starts_with_id_advertise_system()
    {
        var bytes = Codec.Encode(new AdvertiseSystem(Codec.Guid, new byte[] { 0xCA, 0xFE }));

        var message = Codec.Decode<OutOfBandMessage>(Convert.ToHexStringLower(bytes));

        Assert.Equal(new byte[] { (byte)MessageId.AdvertiseSystem, 0xCA, 0xFE }, message.Payload.ToArray());
    }

    [Fact]
    public void The_payload_is_limited_as_the_cpp_receiver_limits_it()
    {
        // RakPeer.cpp:4706-4708: longer than the 25-byte header and shorter than MAX_OFFLINE_DATA_LENGTH + 25.
        Assert.False(Codec.TryDecode<OutOfBandMessage>(Convert.FromHexString(Header), out _));
        Assert.True(Codec.TryDecode<OutOfBandMessage>(Codec.Encode(new OutOfBandMessage(1, new byte[1])), out _));
        Assert.True(Codec.TryDecode<OutOfBandMessage>(Codec.Encode(new OutOfBandMessage(1, new byte[399])), out _));
        Assert.False(Codec.TryDecode<OutOfBandMessage>(Codec.Encode(new OutOfBandMessage(1, new byte[400])), out _));
    }

    [Fact]
    public void Message_shorter_than_its_header_and_one_byte_fails()
    {
        Codec.AssertPrefixesShorterThanFail<OutOfBandMessage>(Convert.FromHexString(Header + "86cafe"), 26);
    }

    [Fact]
    public void Other_ids_and_corrupt_magic_fail()
    {
        var bytes = Convert.FromHexString(Header + "86cafe");
        Codec.AssertOtherIdsFail<OutOfBandMessage>(bytes, MessageId.OutOfBandInternal);
        Codec.AssertCorruptMagicFails<OutOfBandMessage>(bytes, 9);
    }
}
