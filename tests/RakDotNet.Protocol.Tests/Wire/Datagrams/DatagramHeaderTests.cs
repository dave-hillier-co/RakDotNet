using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;
using static RakDotNet.Protocol.Tests.Wire.Datagrams.WireWriter;

namespace RakDotNet.Protocol.Tests.Wire.Datagrams;

// Expected bytes are derived by hand from DatagramHeaderFormat::Serialize (ReliabilityLayer.cpp:143-183) in the
// default build, where USE_SLIDING_WINDOW_CONGESTION_CONTROL=1 (RakNetDefines.h:121-122) so
// INCLUDE_TIMESTAMP_WITH_DATAGRAMS=0 (ReliabilityLayer.h:43-49) and no timestamp is written.
public class DatagramHeaderTests
{
    [Fact]
    public void Data_header_writes_valid_bit_three_flags_then_aligned_little_endian_datagram_number()
    {
        // Bits: valid=1, ack=0, nak=0, packetPair=0, continuousSend=0, needsBAndAs=1, pad 2 -> 1000 0100.
        // ReliabilityLayer.cpp:170-181.
        var header = DatagramHeader.ForData(new UInt24(0x123456), needsBAndAs: true);

        var bytes = Write((ref BitStreamWriter w) => header.Write(ref w), out var bits);

        Assert.Equal(Hex("84 563412"), bytes);
        Assert.Equal(32, bits);
    }

    [Fact]
    public void Data_header_with_every_flag_set_packs_them_in_cpp_order()
    {
        // 1 0 0 1 1 1 0 0 = 0x9C
        var header = DatagramHeader.ForData(new UInt24(1), isPacketPair: true, isContinuousSend: true, needsBAndAs: true);

        var bytes = Write((ref BitStreamWriter w) => header.Write(ref w));

        Assert.Equal(Hex("9C 010000"), bytes);
    }

    [Fact]
    public void Ack_header_without_B_and_AS_is_one_aligned_byte()
    {
        // valid=1, ack=1, hasBAndAS=0, then AlignWriteToByteBoundary. ReliabilityLayer.cpp:150-155.
        var bytes = Write((ref BitStreamWriter w) => DatagramHeader.ForAck().Write(ref w), out var bits);

        Assert.Equal(Hex("C0"), bytes);
        Assert.Equal(8, bits);
    }

    [Fact]
    public void Ack_header_with_B_and_AS_appends_the_arrival_rate_as_a_big_endian_float()
    {
        // valid=1, ack=1, hasBAndAS=1 -> 0xE0, then Write(float AS). ReliabilityLayer.cpp:159-163.
        var bytes = Write((ref BitStreamWriter w) => DatagramHeader.ForAck(arrivalRate: 1.0f).Write(ref w));

        Assert.Equal(Hex("E0 3F800000"), bytes);
    }

    [Fact]
    public void Nak_header_is_three_bits_and_is_not_aligned()
    {
        // valid=1, ack=0, nak=1; Serialize does not align for NAKs (ReliabilityLayer.cpp:165-169). The range list that
        // follows aligns itself (DS_RangeList.h:94).
        var bytes = Write((ref BitStreamWriter w) => DatagramHeader.ForNak().Write(ref w), out var bits);

        Assert.Equal(Hex("A0"), bytes);
        Assert.Equal(3, bits);
    }

    public static TheoryData<DatagramHeader> Headers =>
    [
        DatagramHeader.ForData(new UInt24(0)),
        DatagramHeader.ForData(UInt24.MaxValue, isPacketPair: true),
        DatagramHeader.ForData(new UInt24(77), isContinuousSend: true),
        DatagramHeader.ForData(new UInt24(78), needsBAndAs: true),
        DatagramHeader.ForAck(),
        DatagramHeader.ForAck(arrivalRate: 1234.5f),
        DatagramHeader.ForNak(),
    ];

    [Theory]
    [MemberData(nameof(Headers))]
    public void Headers_round_trip(DatagramHeader header)
    {
        var bytes = Write((ref BitStreamWriter w) => header.Write(ref w), out var bits);
        var reader = new BitStreamReader(bytes);

        Assert.True(DatagramHeader.TryRead(ref reader, out var decoded));
        Assert.Equal(header, decoded);
        Assert.Equal(bits, reader.BitPosition);
    }

    [Fact]
    public void Flags_are_parsed_bit_by_bit_not_by_byte_value()
    {
        // The two pad bits after needsBAndAs are ignored by Deserialize (AlignReadToByteBoundary,
        // ReliabilityLayer.cpp:219), so 0x87 is still a data datagram with needsBAndAs set.
        var reader = new BitStreamReader(Hex("87 050000"));

        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Data, header.Kind);
        Assert.True(header.NeedsBAndAs);
        Assert.Equal(new UInt24(5), header.DatagramNumber);
    }

    [Fact]
    public void Ack_bit_takes_precedence_over_the_nak_bit_position()
    {
        // When isACK is set the third bit is hasBAndAS, not isNAK (ReliabilityLayer.cpp:191-196). 0xE0 is an ACK with
        // B and AS, never a NAK.
        var reader = new BitStreamReader(Hex("E0 3F800000"));

        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramKind.Ack, header.Kind);
        Assert.True(header.HasBAndAs);
        Assert.Equal(1.0f, header.ArrivalRate);
    }

    [Fact]
    public void Nak_ignores_the_remaining_flag_bits()
    {
        // Deserialize stops after isNAK (ReliabilityLayer.cpp:209-213); later bits belong to the range list padding.
        var reader = new BitStreamReader(Hex("BF"));

        Assert.True(DatagramHeader.TryRead(ref reader, out var header));
        Assert.Equal(DatagramHeader.ForNak(), header);
        Assert.Equal(3, reader.BitPosition);
    }

    [Fact]
    public void Clear_valid_bit_is_not_a_connected_datagram()
    {
        // ReliabilityLayer.cpp:689 ignores datagrams whose isValid bit is clear.
        var reader = new BitStreamReader(Hex("04 050000"));

        Assert.False(DatagramHeader.TryRead(ref reader, out _));
        Assert.False(DatagramHeader.HasValidFlag(Hex("04 050000")));
        Assert.True(DatagramHeader.HasValidFlag(Hex("84 050000")));
        Assert.False(DatagramHeader.HasValidFlag([]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("84")]
    [InlineData("84 0500")]
    [InlineData("E0")]
    [InlineData("E0 3F8000")]
    public void Truncated_headers_fail(string hex)
    {
        var reader = new BitStreamReader(Hex(hex));

        Assert.False(DatagramHeader.TryRead(ref reader, out _));
    }

    [Fact]
    public void Header_budget_matches_cpp_GetDataHeaderByteLength()
    {
        // ReliabilityLayer.cpp:133-141: 2 + 3 + sizeof(float), no timestamp in the default build.
        Assert.Equal(9, DatagramLimits.HeaderBudgetBytes);
    }

    [Fact]
    public void Max_payload_bits_excludes_the_udp_header_and_the_header_budget()
    {
        // ReliabilityLayer.cpp:361 gives the congestion manager MTUSize - UDP_HEADER_SIZE (28, CCRakNetSlidingWindow.h:51),
        // and GetMaxDatagramSizeExcludingMessageHeaderBytes subtracts the 9 byte header budget from that (3909-3912).
        Assert.Equal(28, DatagramLimits.UdpHeaderBytes);
        Assert.Equal((1492 - 28 - 9) * 8, DatagramLimits.MaxPayloadBits(1492));
        Assert.Equal((400 - 28 - 9) * 8, DatagramLimits.MaxPayloadBits(400));
    }

    [Fact]
    public void Max_payload_bits_rejects_an_mtu_too_small_for_the_udp_header_and_header_budget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DatagramLimits.MaxPayloadBits(28 + 9 - 1));
        Assert.Equal(0, DatagramLimits.MaxPayloadBits(28 + 9));
    }
}
