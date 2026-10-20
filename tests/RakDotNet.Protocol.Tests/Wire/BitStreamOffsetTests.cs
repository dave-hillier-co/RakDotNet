using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

// Multi-byte fields and runs of bytes are encoded the same way at every bit offset: the bits of each byte in order,
// most significant first, as BitStream::WriteBits does. The reference here is the single-bit writer and reader, so the
// whole-byte paths are checked against the plainest encoding at each of the eight offsets.
public class BitStreamOffsetTests
{
    private static readonly byte[] Pattern = [0xA5, 0x3C, 0xFF, 0x00, 0x81, 0x7E, 0x12, 0xED, 0x96, 0x4B];

    public static TheoryData<int> Offsets => [0, 1, 2, 3, 4, 5, 6, 7];

    [Theory]
    [MemberData(nameof(Offsets))]
    public void Integers_are_written_as_their_big_endian_bytes_bit_by_bit(int offset)
    {
        var fast = new byte[32];
        var writer = new BitStreamWriter(fast);
        WritePrefix(ref writer, offset);
        writer.WriteUInt16(0xA53C);
        writer.WriteUInt32(0xFF00817E);
        writer.WriteUInt64(0x12ED964BA53CFF00);
        writer.WriteInt16(-2);

        var expected = new byte[32];
        var reference = new BitStreamWriter(expected);
        WritePrefix(ref reference, offset);
        WriteBitByBit(ref reference, [0xA5, 0x3C, 0xFF, 0x00, 0x81, 0x7E, 0x12, 0xED, 0x96, 0x4B, 0xA5, 0x3C, 0xFF, 0x00, 0xFF, 0xFE]);

        Assert.Equal(reference.BitsWritten, writer.BitsWritten);
        Assert.Equal(expected.AsSpan(0, reference.BytesWritten).ToArray(), fast.AsSpan(0, writer.BytesWritten).ToArray());
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void Byte_runs_of_every_length_are_written_bit_by_bit(int offset)
    {
        for (var length = 0; length <= Pattern.Length; length++)
        {
            var fast = new byte[32];
            var writer = new BitStreamWriter(fast);
            WritePrefix(ref writer, offset);
            writer.WriteBytes(Pattern.AsSpan(0, length));
            writer.WriteBit(true);

            var expected = new byte[32];
            var reference = new BitStreamWriter(expected);
            WritePrefix(ref reference, offset);
            WriteBitByBit(ref reference, Pattern.AsSpan(0, length));
            reference.WriteBit(true);

            Assert.Equal(expected.AsSpan(0, reference.BytesWritten).ToArray(), fast.AsSpan(0, writer.BytesWritten).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void Integers_and_byte_runs_read_back_what_the_bits_say(int offset)
    {
        var buffer = new byte[64];
        var writer = new BitStreamWriter(buffer);
        WritePrefix(ref writer, offset);
        WriteBitByBit(ref writer, [0xA5, 0x3C, 0xFF, 0x00, 0x81, 0x7E, 0x12, 0xED, 0x96, 0x4B, 0xA5, 0x3C, 0xFF, 0x00]);
        WriteBitByBit(ref writer, Pattern);

        var reader = new BitStreamReader(buffer.AsSpan(0, writer.BytesWritten), writer.BitsWritten);
        Assert.True(reader.IgnoreBits(offset));
        Assert.True(reader.TryReadUInt16(out var u16));
        Assert.True(reader.TryReadUInt32(out var u32));
        Assert.True(reader.TryReadUInt64(out var u64));
        var bytes = new byte[Pattern.Length];
        Assert.True(reader.TryReadBytes(bytes));

        Assert.Equal(0xA53C, u16);
        Assert.Equal(0xFF00817Eu, u32);
        Assert.Equal(0x12ED964BA53CFF00ul, u64);
        Assert.Equal(Pattern, bytes);
        Assert.Equal(0, reader.BitsRemaining);
    }

    [Theory]
    [MemberData(nameof(Offsets))]
    public void A_read_one_bit_short_fails_without_moving(int offset)
    {
        var buffer = new byte[16];
        var bitLength = offset + 64 - 1;
        foreach (var size in new[] { 2, 4, 8 })
        {
            var reader = new BitStreamReader(buffer, offset + size * 8 - 1);
            Assert.True(reader.IgnoreBits(offset));
            var ok = size switch
            {
                2 => reader.TryReadUInt16(out _),
                4 => reader.TryReadUInt32(out _),
                _ => reader.TryReadUInt64(out _),
            };
            Assert.False(ok);
            Assert.Equal(offset, reader.BitPosition);
        }
        var bytesReader = new BitStreamReader(buffer, bitLength);
        Assert.True(bytesReader.IgnoreBits(offset));
        Assert.False(bytesReader.TryReadBytes(new byte[8]));
        Assert.Equal(offset, bytesReader.BitPosition);
    }

    // A recognisable lead-in of offset bits.
    private static void WritePrefix(ref BitStreamWriter writer, int offset)
    {
        for (var i = 0; i < offset; i++)
            writer.WriteBit(i % 2 == 0);
    }

    private static void WriteBitByBit(ref BitStreamWriter writer, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
                writer.WriteBit(((b >> bit) & 1) != 0);
        }
    }
}
