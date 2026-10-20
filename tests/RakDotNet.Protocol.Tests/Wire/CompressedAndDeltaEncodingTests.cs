using System.Net;
using RakDotNet.Protocol.Wire;
using static RakDotNet.Protocol.Tests.Wire.NativeBits;

namespace RakDotNet.Protocol.Tests.Wire;

// Expected bytes are known answers printed by native RakNet 1a169895 on an arm64 host with tools/bitstream-vectors.
public class CompressedAndDeltaEncodingTests
{
    private static readonly SystemAddress Address = new(IPAddress.Parse("10.1.2.3"), 1234);
    private static readonly SystemAddress OtherAddress = new(IPAddress.Parse("10.1.2.3"), 1235);
    private static readonly RakNetGuid Guid = new(0x0102030405060708);
    private static readonly RakNetGuid OtherGuid = new(0x0102030405060709);

    [Theory]
    [InlineData(0.5f, "dfff80")]
    [InlineData(-1f, "800000")]
    [InlineData(1f, "ffff80")]
    [InlineData(1.005f, "ffff80")]
    [InlineData(-0.3f, "accc80")]
    public void WriteCompressed_float_is_a_u16_between_minus_one_and_one(float value, string hex)
    {
        // BitStream.h:1328-1338: clamped to -1..1, then (value + 1) * 32767.5 as a u16.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(value), 17, hex);
        var reader = Reader(hex, 17);
        Assert.True(reader.TryReadCompressed(out float read));
        Assert.Equal(Math.Clamp(value, -1f, 1f), read, 1f / 32767f);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Theory]
    [InlineData(0.5, "e000000000")]
    [InlineData(-1.0, "8000000000")]
    [InlineData(1.0, "ffffffff80")]
    [InlineData(-0.3, "accccccc80")]
    public void WriteCompressed_double_is_a_u32_between_minus_one_and_one(double value, string hex)
    {
        // BitStream.h:1341-1351: (value + 1) * 2^31 as a u32; 1.0 overflows the u32 and saturates, as on arm64.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(value), 33, hex);
        var reader = Reader(hex, 33);
        Assert.True(reader.TryReadCompressed(out double read));
        Assert.Equal(value, read, 1e-9);
    }

    [Fact]
    public void TryReadCompressed_float_fails_when_truncated()
    {
        var reader = Reader("dfff80", 9);
        Assert.False(reader.TryReadCompressed(out float _));
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void The_compressed_forms_of_bool_address_guid_and_uint24_are_the_plain_forms()
    {
        // BitStream.h:1303-1324: WriteCompressed of these types calls Write.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(true), 2, "c0");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(Address), 57, "827aff7efe026900");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(Guid), 65, "808101820283038400");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressed(new UInt24(0x123456)), 32, "80563412");

        var reader = Reader("827aff7efe026900", 57);
        Assert.True(reader.TryReadCompressed(out SystemAddress address));
        Assert.Equal(Address, address);
        reader = Reader("808101820283038400", 65);
        Assert.True(reader.TryReadCompressed(out RakNetGuid guid));
        Assert.Equal(Guid, guid);
        reader = Reader("80563412", 32);
        Assert.True(reader.TryReadCompressed(out UInt24 u24));
        Assert.Equal(0x123456u, u24.Value);
        reader = Reader("c0", 2);
        Assert.True(reader.TryReadCompressed(out bool flag));
        Assert.True(flag);
    }

    [Fact]
    public void WriteRakNetGuid_writes_the_value_big_endian()
    {
        // BitStream.h:1187-1190: Write(RakNetGUID) writes the u64 g.
        AssertWritten((ref BitStreamWriter w) => w.WriteRakNetGuid(Guid), 65, "808101820283038400");
        var reader = Reader("808101820283038400", 65);
        Assert.True(reader.TryReadRakNetGuid(out var read));
        Assert.Equal(Guid, read);
        Assert.False(reader.TryReadRakNetGuid(out _));
    }

    [Fact]
    public void BitStream_WriteRakNetGuid_matches_the_writer()
    {
        using var stream = new BitStream();
        stream.WriteBit(true);
        stream.WriteRakNetGuid(Guid);
        Assert.Equal("808101820283038400", Convert.ToHexStringLower(stream.Written));
    }

    [Fact]
    public void WriteDelta_without_a_last_value_always_writes_the_flag_and_the_value()
    {
        // BitStream.h:1263-1268.
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(7u), 34, "c0000001c0");
        var reader = Reader("c0000001c0", 34);
        var value = 0u;
        Assert.True(reader.TryReadDelta(ref value));
        Assert.Equal(7u, value);
    }

    [Fact]
    public void WriteDelta_of_a_bool_writes_only_the_bool()
    {
        // BitStream.h:1253-1258: the bool specialisation ignores the last value.
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(true, false), 2, "c0");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(false, false), 2, "80");
        // The one-argument form has no bool specialisation: a 1 bit, then the bool.
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(false), 3, "c0");

        // BitStream.h:1602-1605: ReadDelta of a bool reads the one bit.
        var reader = Reader("c0", 2);
        var flag = false;
        Assert.True(reader.TryReadDelta(ref flag));
        Assert.True(flag);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Fact]
    public void WriteDelta_covers_floats_and_doubles()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(1.5f, 1.0f), 34, "cff0000000");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(1.5, 1.5), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(-2.0, 1.5), 66, "f00000000000000000");

        var reader = Reader("cff0000000", 34);
        var single = 1.0f;
        Assert.True(reader.TryReadDelta(ref single));
        Assert.Equal(1.5f, single);
        reader = Reader("f00000000000000000", 66);
        var dbl = 1.5;
        Assert.True(reader.TryReadDelta(ref dbl));
        Assert.Equal(-2.0, dbl);
    }

    [Fact]
    public void WriteDelta_covers_addresses_guids_and_uint24()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(Address, OtherAddress), 58, "c13d7fbf7f013480");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(Address, Address), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(Guid, OtherGuid), 66, "c04080c1014181c200");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(Guid, Guid), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(new UInt24(0x123456), new UInt24(0x123457)), 32, "c0563412");
        AssertWritten((ref BitStreamWriter w) => w.WriteDelta(new UInt24(0x123456), new UInt24(0x123456)), 2, "80");

        var reader = Reader("c13d7fbf7f013480", 58);
        var address = OtherAddress;
        Assert.True(reader.TryReadDelta(ref address));
        Assert.Equal(Address, address);
        reader = Reader("c04080c1014181c200", 66);
        var guid = OtherGuid;
        Assert.True(reader.TryReadDelta(ref guid));
        Assert.Equal(Guid, guid);
        reader = Reader("c0563412", 32);
        var u24 = new UInt24(1);
        Assert.True(reader.TryReadDelta(ref u24));
        Assert.Equal(0x123456u, u24.Value);
        reader = Reader("80", 2);
        Assert.True(reader.TryReadDelta(ref u24));
        Assert.Equal(0x123456u, u24.Value);
    }

    [Fact]
    public void TryReadDelta_fails_without_moving_when_the_value_is_missing()
    {
        var reader = Reader("c13d7fbf7f013480", 20);
        var address = OtherAddress;
        Assert.False(reader.TryReadDelta(ref address));
        Assert.Equal(1, reader.BitPosition);
        Assert.Equal(OtherAddress, address);
    }

    [Fact]
    public void WriteCompressedDelta_writes_a_flag_then_the_compressed_value()
    {
        // BitStream.h:1399-1438.
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(5u, 5u), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(6u, 5u), 35, "c0000000c0");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(6u), 35, "c0000000c0");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(0.5f, 0.25f), 18, "efffc0");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(-0.3, 0.0), 34, "d666666640");

        var reader = Reader("c0000000c0", 35);
        var value = 5u;
        Assert.True(reader.TryReadCompressedDelta(ref value));
        Assert.Equal(6u, value);
        reader = Reader("80", 2);
        Assert.True(reader.TryReadCompressedDelta(ref value));
        Assert.Equal(6u, value);
        reader = Reader("efffc0", 18);
        var single = 0.25f;
        Assert.True(reader.TryReadCompressedDelta(ref single));
        Assert.Equal(0.5f, single, 1f / 32767f);
        reader = Reader("d666666640", 34);
        var dbl = 0.0;
        Assert.True(reader.TryReadCompressedDelta(ref dbl));
        Assert.Equal(-0.3, dbl, 1e-9);
    }

    [Fact]
    public void WriteCompressedDelta_of_a_bool_writes_only_the_bool()
    {
        // BitStream.h:1416-1420 and 1435-1438: both bool forms are Write(bool).
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(true, true), 2, "c0");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(true), 2, "c0");
        var reader = Reader("c0", 2);
        var flag = false;
        Assert.True(reader.TryReadCompressedDelta(ref flag));
        Assert.True(flag);
    }

    [Fact]
    public void WriteCompressedDelta_of_an_address_guid_or_uint24_matches_WriteDelta()
    {
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(Address, OtherAddress), 58, "c13d7fbf7f013480");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(Guid, Guid), 2, "80");
        AssertWritten((ref BitStreamWriter w) => w.WriteCompressedDelta(new UInt24(0x123456)), 32, "c0563412");

        var reader = Reader("c13d7fbf7f013480", 58);
        var address = OtherAddress;
        Assert.True(reader.TryReadCompressedDelta(ref address));
        Assert.Equal(Address, address);
        reader = Reader("c0563412", 32);
        var u24 = default(UInt24);
        Assert.True(reader.TryReadCompressedDelta(ref u24));
        Assert.Equal(0x123456u, u24.Value);
        reader = Reader("c04080c1014181c200", 66);
        var guid = OtherGuid;
        Assert.True(reader.TryReadCompressedDelta(ref guid));
        Assert.Equal(Guid, guid);
    }
}
