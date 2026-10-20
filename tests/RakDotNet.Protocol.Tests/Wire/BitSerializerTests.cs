using System.Net;
using System.Numerics;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// BitStream::Serialize* (BitStream.h:938-1078) write with the matching Write* and read with the matching Read*. Each
// test serializes a set of fields, checks the bytes against known answers from native RakNet (tools/bitstream-vectors,
// without the leading 1 bit those cases start with, or the writer encodings those answers check), then reads them back
// with the same method body.
public class BitSerializerTests
{
    private static readonly SystemAddress Address = new(IPAddress.Parse("10.1.2.3"), 1234);

    [Fact]
    public void SerializeDelta_writes_and_reads_each_delta_form()
    {
        var fields = new DeltaFields(7u, 7u, 1.5f, true, Address, new RakNetGuid(9), new UInt24(5));
        var (bytes, bits) = Write(fields, (ref BitSerializer s, ref DeltaFields f) => SerializeDeltas(ref s, ref f));

        // 0 (unchanged u32), 1 + u32 7, 1 + 1.5f, the bool alone, 1 + address, 1 + guid, 1 + aligned uint24.
        Span<byte> expected = stackalloc byte[64];
        var writer = new BitStreamWriter(expected);
        writer.WriteDelta(7u, 7u);
        writer.WriteDelta(7u);
        writer.WriteDelta(1.5f, 1.0f);
        writer.WriteDelta(true, false);
        writer.WriteDelta(Address, SystemAddress.Unassigned);
        writer.WriteDelta(new RakNetGuid(9), RakNetGuid.Unassigned);
        writer.WriteDelta(new UInt24(5), default);
        Assert.Equal(writer.BitsWritten, bits);
        Assert.Equal(writer.Written.ToArray(), bytes);

        var read = new DeltaFields(7u, 0u, 1.0f, false, SystemAddress.Unassigned, RakNetGuid.Unassigned, default);
        Read(bytes, bits, ref read, (ref BitSerializer s, ref DeltaFields f) => SerializeDeltas(ref s, ref f));
        Assert.Equal(fields, read);
    }

    [Fact]
    public void SerializeCompressedDelta_writes_and_reads_each_compressed_delta_form()
    {
        var (u, single, dbl, flag) = (6u, 0.5f, -0.3, true);
        Span<byte> buffer = stackalloc byte[32];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        Assert.True(writing.SerializeCompressedDelta(ref u, 5u));
        Assert.True(writing.SerializeCompressedDelta(ref single, 0.25f));
        Assert.True(writing.SerializeCompressedDelta(ref dbl, 0.0));
        Assert.True(writing.SerializeCompressedDelta(ref flag, true));
        Assert.True(writing.SerializeCompressedDelta(ref u));
        var written = writing.Writer;

        // Native: cdelta_u32_6_5, cdelta_f_0.5_0.25, cdelta_d_m0.3_0, cdelta_bool_true_true, cdelta1_u32_6.
        Span<byte> expected = stackalloc byte[32];
        var writer = new BitStreamWriter(expected);
        writer.WriteCompressedDelta(6u, 5u);
        writer.WriteCompressedDelta(0.5f, 0.25f);
        writer.WriteCompressedDelta(-0.3, 0.0);
        writer.WriteCompressedDelta(true, true);
        writer.WriteCompressedDelta(6u);
        Assert.Equal(34 + 17 + 33 + 1 + 34, written.BitsWritten);
        Assert.Equal(writer.Written.ToArray(), written.Written.ToArray());

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var (ru, rs, rd, rf, ru2) = (0u, 0f, 0.0, false, 0u);
        Assert.True(reading.SerializeCompressedDelta(ref ru, 0u));
        Assert.True(reading.SerializeCompressedDelta(ref rs, 0f));
        Assert.True(reading.SerializeCompressedDelta(ref rd, 0.0));
        Assert.True(reading.SerializeCompressedDelta(ref rf, false));
        Assert.True(reading.SerializeCompressedDelta(ref ru2));
        Assert.Equal(6u, ru);
        Assert.Equal(0.5f, rs, 1f / 32767f);
        Assert.Equal(-0.3, rd, 1e-9);
        Assert.True(rf);
        Assert.Equal(6u, ru2);
        Assert.Equal(0, reading.Reader.BitsRemaining);
    }

    [Fact]
    public void SerializeFloat16_SerializeCasted_and_the_ranges_match_the_writer_encodings()
    {
        Span<byte> buffer = stackalloc byte[32];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var (half, casted, ranged, explicitBits, compressedFloat, compressedDouble) = (0.5f, 53, (ushort)500, 300u, 0.5f, 0.5);
        Assert.True(writing.SerializeFloat16(ref half, -1f, 1f));
        Assert.True(writing.SerializeCasted<byte, int>(ref casted));
        Assert.True(writing.SerializeBitsFromIntegerRange(ref ranged, (ushort)0, (ushort)1000));
        Assert.True(writing.SerializeBitsFromIntegerRange(ref explicitBits, 0u, 1000u, 9));
        Assert.True(writing.SerializeCompressed(ref compressedFloat));
        Assert.True(writing.SerializeCompressed(ref compressedDouble));
        var written = writing.Writer;

        // Native: f16_0.5_m1_1 (bfff), cast_u8_53 (35), r16_500_0_1000 (11 bits), rbits_u32_300_0_1000_9, cf_0.5, cd_0.5.
        Span<byte> expected = stackalloc byte[32];
        var writer = new BitStreamWriter(expected);
        writer.WriteFloat16(0.5f, -1f, 1f);
        writer.WriteCasted<byte, int>(53);
        writer.WriteBitsFromIntegerRange((ushort)500, (ushort)0, (ushort)1000);
        writer.WriteBitsFromIntegerRange(300u, 0u, 1000u, 9);
        writer.WriteCompressed(0.5f);
        writer.WriteCompressed(0.5);
        Assert.Equal(16 + 8 + 10 + 9 + 16 + 32, written.BitsWritten);
        Assert.Equal(writer.Written.ToArray(), written.Written.ToArray());
        Assert.Equal("bfff35", Convert.ToHexStringLower(written.Written[..3]));

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var (rh, rc, rr, re, rcf, rcd) = (0f, 0, (ushort)0, 0u, 0f, 0.0);
        Assert.True(reading.SerializeFloat16(ref rh, -1f, 1f));
        Assert.True(reading.SerializeCasted<byte, int>(ref rc));
        Assert.True(reading.SerializeBitsFromIntegerRange(ref rr, (ushort)0, (ushort)1000));
        Assert.True(reading.SerializeBitsFromIntegerRange(ref re, 0u, 1000u, 9));
        Assert.True(reading.SerializeCompressed(ref rcf));
        Assert.True(reading.SerializeCompressed(ref rcd));
        Assert.Equal(0.5f, rh, 1f / 32767f);
        Assert.Equal(53, rc);
        Assert.Equal(500, rr);
        Assert.Equal(300u, re);
        Assert.Equal(0.5f, rcf, 1f / 32767f);
        Assert.Equal(0.5, rcd, 1e-9);
    }

    [Fact]
    public void The_vector_and_rotation_serializers_match_the_writer_encodings()
    {
        Span<byte> buffer = stackalloc byte[64];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var normal = new Vector3(0.6f, -0.8f, 0f);
        var vector = new Vector3(3f, -4f, 12f);
        var quat = new Quaternion(0.5f, -0.5f, 0.5f, -0.5f);
        var matrix = new Matrix4x4(0f, -1f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
        Assert.True(writing.SerializeNormVector(ref normal));
        Assert.True(writing.SerializeVector(ref vector));
        Assert.True(writing.SerializeNormQuat(ref quat));
        Assert.True(writing.SerializeOrthMatrix(ref matrix));
        var written = writing.Writer;

        Span<byte> expected = stackalloc byte[64];
        var writer = new BitStreamWriter(expected);
        writer.WriteNormVector(normal);
        writer.WriteVector(vector);
        writer.WriteNormQuat(quat);
        writer.WriteOrthMatrix(matrix);
        Assert.Equal(48 + 80 + 52 + 52, written.BitsWritten);
        Assert.Equal(writer.Written.ToArray(), written.Written.ToArray());

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var (rn, rv, rq, rm) = (Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity);
        Assert.True(reading.SerializeNormVector(ref rn));
        Assert.True(reading.SerializeVector(ref rv));
        Assert.True(reading.SerializeNormQuat(ref rq));
        Assert.True(reading.SerializeOrthMatrix(ref rm));
        Assert.Equal(-0.8f, rn.Y, 0.0001f);
        Assert.Equal(12f, rv.Z, 0.001f);
        Assert.Equal(-0.5f, rq.W, 0.0001f);
        Assert.Equal(-1f, rm.M12, 0.0001f);
        Assert.False(reading.SerializeNormVector(ref rn));
    }

    [Fact]
    public void SerializeOrthMatrix_reading_leaves_everything_outside_the_upper_3x3_alone()
    {
        // BitStream.h:1059-1070 hands ReadOrthMatrix only m00..m22, so the translation and the fourth column keep their values.
        Span<byte> buffer = stackalloc byte[16];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var sent = Matrix4x4.CreateTranslation(5, 6, 7);
        Assert.True(writing.SerializeOrthMatrix(ref sent));
        var written = writing.Writer;

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var target = Matrix4x4.CreateTranslation(1, 2, 3);
        target.M11 = 0;
        target.M14 = 4;
        target.M44 = 9;
        Assert.True(reading.SerializeOrthMatrix(ref target));
        Assert.Equal(1f, target.M11, 0.0001f);
        Assert.Equal(new Vector3(1, 2, 3), target.Translation);
        Assert.Equal(4f, target.M14);
        Assert.Equal(9f, target.M44);
    }

    [Fact]
    public void SerializeBits_and_Serialize_bytes_copy_raw_data()
    {
        // BitStream.h:997-1004 and 1072-1078: Serialize(char*, n) is Write(char*, n), unaligned; SerializeBits is WriteBits.
        Span<byte> buffer = stackalloc byte[16];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var flag = true;
        byte[] raw = [0x12, 0x34];
        byte[] bits = [0xab, 0xe0];
        Assert.True(writing.Serialize(ref flag));
        Assert.True(writing.Serialize(raw));
        Assert.True(writing.SerializeBits(bits, 11, rightAlignedBits: false));
        var written = writing.Writer;
        Assert.Equal(1 + 16 + 11, written.BitsWritten);
        // 1, 0x1234, then native bits_left_11 (0xab then the high three bits of 0xe0).
        Assert.Equal("891a55f0", Convert.ToHexStringLower(written.Written));

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var readFlag = false;
        var readRaw = new byte[2];
        var readBits = new byte[2];
        Assert.True(reading.Serialize(ref readFlag));
        Assert.True(reading.Serialize(readRaw));
        Assert.True(reading.SerializeBits(readBits, 11, rightAlignedBits: false));
        Assert.Equal(raw, readRaw);
        Assert.Equal(bits, readBits);
        Assert.False(reading.Serialize(readRaw));
    }

    [Fact]
    public void Serialize_handles_addresses_guids_and_uint24()
    {
        Span<byte> buffer = stackalloc byte[32];
        var writing = new BitSerializer(new BitStreamWriter(buffer));
        var (address, guid, u24) = (Address, new RakNetGuid(0x0102030405060708), new UInt24(0x123456));
        Assert.True(writing.Serialize(ref address));
        Assert.True(writing.Serialize(ref guid));
        Assert.True(writing.Serialize(ref u24));
        var written = writing.Writer;
        // Native compressed_sa, write_guid and compressed_u24 without their leading bit.
        Assert.Equal("04f5fefdfc04d20102030405060708563412", Convert.ToHexStringLower(written.Written));

        var reading = new BitSerializer(new BitStreamReader(written.Written, written.BitsWritten));
        var (ra, rg, ru) = (SystemAddress.Unassigned, RakNetGuid.Unassigned, default(UInt24));
        Assert.True(reading.Serialize(ref ra));
        Assert.True(reading.Serialize(ref rg));
        Assert.True(reading.Serialize(ref ru));
        Assert.Equal((address, guid, u24), (ra, rg, ru));
    }

    private record struct DeltaFields(uint Unchanged, uint Always, float Single, bool Flag, SystemAddress Address, RakNetGuid Guid, UInt24 Small);

    private static bool SerializeDeltas(ref BitSerializer s, ref DeltaFields f)
    {
        var unchanged = f.Unchanged;
        var always = f.Always;
        var single = f.Single;
        var flag = f.Flag;
        var address = f.Address;
        var guid = f.Guid;
        var small = f.Small;
        var ok = s.SerializeDelta(ref unchanged, 7u)
            && s.SerializeDelta(ref always)
            && s.SerializeDelta(ref single, 1.0f)
            && s.SerializeDelta(ref flag, false)
            && s.SerializeDelta(ref address, SystemAddress.Unassigned)
            && s.SerializeDelta(ref guid, RakNetGuid.Unassigned)
            && s.SerializeDelta(ref small, default);
        f = new DeltaFields(unchanged, always, single, flag, address, guid, small);
        return ok;
    }

    private delegate bool SerializeAction(ref BitSerializer serializer, ref DeltaFields fields);

    private static (byte[] Bytes, int Bits) Write(DeltaFields fields, SerializeAction serialize)
    {
        var buffer = new byte[64];
        var serializer = new BitSerializer(new BitStreamWriter(buffer));
        Assert.True(serialize(ref serializer, ref fields));
        return (serializer.Writer.Written.ToArray(), serializer.Writer.BitsWritten);
    }

    private static void Read(byte[] bytes, int bits, ref DeltaFields fields, SerializeAction serialize)
    {
        var serializer = new BitSerializer(new BitStreamReader(bytes, bits));
        Assert.True(serialize(ref serializer, ref fields));
        Assert.Equal(0, serializer.Reader.BitsRemaining);
    }
}
