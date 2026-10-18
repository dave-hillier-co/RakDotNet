using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Tests.Wire.Messages;

public class AdvertiseSystemTests
{
    // RakPeer.cpp:2686-2688 wraps ID_ADVERTISE_SYSTEM + data in SendOutOfBand, whose header
    // (WriteOutOfBandHeader, RakPeer.cpp:2981-2983) is ID_OUT_OF_BAND_INTERNAL, sender GUID, aligned magic.
    private const string Header = "0d" + Codec.GuidHex + Codec.Magic + "1d";

    [Fact]
    public void Advertise_encodes_inside_an_out_of_band_header()
    {
        var advertise = new AdvertiseSystem(Codec.Guid, new byte[] { 0xCA, 0xFE });

        Assert.Equal(Header + "cafe", Codec.EncodeHex(advertise));
    }

    [Fact]
    public void Advertise_decodes_every_field()
    {
        var advertise = Codec.Decode<AdvertiseSystem>(Header + "cafe");

        Assert.Equal(Codec.Guid, advertise.SenderGuid);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, advertise.Data.ToArray());
    }

    [Fact]
    public void Advertise_with_no_data_round_trips()
    {
        var bytes = Codec.Encode(new AdvertiseSystem(Codec.Guid, ReadOnlyMemory<byte>.Empty));

        Assert.Equal(Header, Convert.ToHexStringLower(bytes));
        Assert.True(Codec.TryDecode<AdvertiseSystem>(bytes, out var decoded));
        Assert.True(decoded.Data.IsEmpty);
    }

    [Fact]
    public void Advertise_round_trips()
    {
        var advertise = new AdvertiseSystem(99, "lan game"u8.ToArray());

        Assert.True(Codec.TryDecode<AdvertiseSystem>(Codec.Encode(advertise), out var decoded));
        Assert.Equal(advertise.SenderGuid, decoded.SenderGuid);
        Assert.Equal(advertise.Data.ToArray(), decoded.Data.ToArray());
    }

    [Fact]
    public void Advertise_data_is_limited_as_the_cpp_receiver_limits_it()
    {
        // RakPeer.cpp:4708: length < MAX_OFFLINE_DATA_LENGTH + 25, and the out-of-band payload includes the
        // ID_ADVERTISE_SYSTEM byte, so at most 398 bytes of advertised data are accepted.
        Assert.True(Codec.TryDecode<AdvertiseSystem>(Codec.Encode(new AdvertiseSystem(1, new byte[398])), out _));
        Assert.False(Codec.TryDecode<AdvertiseSystem>(Codec.Encode(new AdvertiseSystem(1, new byte[399])), out _));
    }

    [Fact]
    public void Out_of_band_message_that_is_not_an_advertisement_fails()
    {
        var bytes = Convert.FromHexString(Header + "cafe");
        bytes[25] = (byte)MessageId.UserPacketEnum;

        Assert.False(Codec.TryDecode<AdvertiseSystem>(bytes, out _));
    }

    [Fact]
    public void Advertise_shorter_than_its_header_fails()
    {
        Codec.AssertPrefixesShorterThanFail<AdvertiseSystem>(Convert.FromHexString(Header + "cafe"), 26);
    }

    [Fact]
    public void Advertise_with_another_id_fails()
    {
        Codec.AssertOtherIdsFail<AdvertiseSystem>(Convert.FromHexString(Header + "cafe"), MessageId.OutOfBandInternal);
    }

    [Fact]
    public void Advertise_with_corrupt_magic_fails()
    {
        Codec.AssertCorruptMagicFails<AdvertiseSystem>(Convert.FromHexString(Header + "cafe"), 9);
    }
}
