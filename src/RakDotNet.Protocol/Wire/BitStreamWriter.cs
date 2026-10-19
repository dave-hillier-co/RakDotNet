using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// Writes bits into a fixed buffer using RakNet BitStream encoding: bits are packed MSB-first
/// within each byte, multi-byte values are big-endian, and <see cref="UInt24"/> is byte-aligned
/// little-endian.
/// </summary>
/// <remarks>
/// Writing past the end of the buffer is a programming error (the buffer was sized wrongly) and throws.
/// </remarks>
public ref struct BitStreamWriter
{
    private readonly Span<byte> _buffer;
    private int _bitPosition;

    public BitStreamWriter(Span<byte> buffer)
    {
        _buffer = buffer;
    }

    /// <summary>
    /// Resumes writing into <paramref name="buffer"/> at <paramref name="bitPosition"/>, keeping the bits already
    /// written before it, so a writer can be handed out, stored as a position and recreated (<see cref="BitStream"/>).
    /// </summary>
    public BitStreamWriter(Span<byte> buffer, int bitPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitPosition, buffer.Length * 8);
        _buffer = buffer;
        _bitPosition = bitPosition;
    }

    public readonly int BitsWritten => _bitPosition;

    public readonly int BytesWritten => (_bitPosition + 7) >> 3;

    public readonly ReadOnlySpan<byte> Written => _buffer[..BytesWritten];

    public void WriteBit(bool value)
    {
        EnsureCapacity(1);
        var byteIndex = _bitPosition >> 3;
        var bitInByte = _bitPosition & 7;
        if (bitInByte == 0)
            _buffer[byteIndex] = 0;
        if (value)
            _buffer[byteIndex] |= (byte)(0x80 >> bitInByte);
        _bitPosition++;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, most significant first.</summary>
    public void WriteBits(byte value, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 8);
        EnsureCapacity(count);
        var shifted = (byte)(value << (8 - count));
        var byteIndex = _bitPosition >> 3;
        var bitInByte = _bitPosition & 7;
        if (bitInByte == 0)
            _buffer[byteIndex] = shifted;
        else
        {
            _buffer[byteIndex] |= (byte)(shifted >> bitInByte);
            if (bitInByte + count > 8)
                _buffer[byteIndex + 1] = (byte)(shifted << (8 - bitInByte));
        }
        _bitPosition += count;
    }

    /// <summary>
    /// Writes <paramref name="bitCount"/> bits from <paramref name="source"/>: whole bytes in order, then the final
    /// partial byte's low bits, or with <paramref name="rightAlignedBits"/> false its high bits. Matches
    /// BitStream::WriteBits (BitStream.cpp:424-475).
    /// </summary>
    /// <remarks>
    /// Left-aligned, RakNet copies the whole final byte unshifted (BitStream.cpp:446-455), so bits of it past
    /// <paramref name="bitCount"/> land in the stream and later writes OR into them; this writer does the same. RakNet's
    /// own left-aligned callers pass bytes whose unused bits are zero.
    /// </remarks>
    public void WriteBits(scoped ReadOnlySpan<byte> source, int bitCount, bool rightAlignedBits = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, source.Length * 8);
        var wholeBytes = bitCount >> 3;
        WriteBytes(source[..wholeBytes]);
        var remainder = bitCount & 7;
        if (remainder == 0)
            return;
        if (rightAlignedBits)
        {
            WriteBits(source[wholeBytes], remainder);
            return;
        }

        EnsureCapacity(remainder);
        var dataByte = source[wholeBytes];
        var byteIndex = _bitPosition >> 3;
        var bitInByte = _bitPosition & 7;
        if (bitInByte == 0)
            _buffer[byteIndex] = dataByte;
        else
        {
            _buffer[byteIndex] |= (byte)(dataByte >> bitInByte);
            if (8 - bitInByte < remainder)
                _buffer[byteIndex + 1] = (byte)(dataByte << (8 - bitInByte));
        }
        _bitPosition += remainder;
    }

    /// <summary>
    /// Copies up to <paramref name="bitCount"/> bits from <paramref name="source"/>'s position, stopping early when the
    /// source runs out, and returns how many were copied. BitStream::Write(BitStream*, numberOfBits)
    /// (BitStream.cpp:193-243) without the advance of the source: the reader is taken by value so that a writer over a
    /// stack buffer can copy from a reader over any memory. <see cref="BitStream.Write(ref BitStreamReader, int)"/>
    /// advances its source.
    /// </summary>
    public int Write(scoped BitStreamReader source, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        var copied = Math.Min(bitCount, source.BitsRemaining);
        EnsureCapacity(copied);
        var remaining = copied;
        for (; remaining >= 8; remaining -= 8)
        {
            source.TryReadByte(out var b);
            WriteByte(b);
        }
        if (remaining > 0)
        {
            source.TryReadBits(remaining, out var bits);
            WriteBits(bits, remaining);
        }
        return copied;
    }

    /// <summary>
    /// Copies every unread bit of <paramref name="source"/> and returns how many. BitStream::Write(BitStream*)
    /// (BitStream.cpp:189-192) without the advance of the source.
    /// </summary>
    public int Write(scoped BitStreamReader source) => Write(source, source.BitsRemaining);

    /// <summary>
    /// Aligns to a byte, then writes zero bytes until <paramref name="byteLength"/> bytes are written; nothing when the
    /// writer already holds that many. Matches BitStream::PadWithZeroToByteLength (BitStream.cpp:744-754).
    /// </summary>
    public void PadWithZeroToByteLength(int byteLength)
    {
        if (BytesWritten >= byteLength)
            return;
        AlignToByte();
        var count = byteLength - BytesWritten;
        EnsureCapacity(count * 8);
        _buffer.Slice(_bitPosition >> 3, count).Clear();
        _bitPosition += count * 8;
    }

    /// <summary>Writes bytes at the current bit position without aligning first.</summary>
    public void WriteBytes(scoped ReadOnlySpan<byte> source)
    {
        if ((_bitPosition & 7) == 0)
        {
            EnsureCapacity(source.Length * 8);
            source.CopyTo(_buffer[(_bitPosition >> 3)..]);
            _bitPosition += source.Length * 8;
            return;
        }
        foreach (var b in source)
            WriteBits(b, 8);
    }

    /// <summary>Aligns to the next byte boundary, then writes bytes. Matches BitStream::WriteAlignedBytes.</summary>
    public void WriteAlignedBytes(scoped ReadOnlySpan<byte> source)
    {
        AlignToByte();
        WriteBytes(source);
    }

    /// <summary>Pads with zero bits to the next byte boundary. Matches BitStream::AlignWriteToByteBoundary.</summary>
    public void AlignToByte()
    {
        var pad = (8 - (_bitPosition & 7)) & 7;
        if (pad == 0)
            return;
        EnsureCapacity(pad);
        _bitPosition += pad;
    }

    public void WriteByte(byte value) => WriteBits(value, 8);

    public void WriteBool(bool value) => WriteBit(value);

    public void WriteUInt16(ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        WriteBytes(bytes);
    }

    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    public void WriteUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        WriteBytes(bytes);
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        WriteBytes(bytes);
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    /// <summary>Aligns to a byte boundary, then writes three bytes little-endian. Matches BitStream::Write(uint24_t).</summary>
    public void WriteUInt24(UInt24 value)
    {
        AlignToByte();
        Span<byte> bytes = stackalloc byte[3];
        bytes[0] = (byte)value.Value;
        bytes[1] = (byte)(value.Value >> 8);
        bytes[2] = (byte)(value.Value >> 16);
        WriteBytes(bytes);
    }

    /// <summary>Writes the GUID's u64 big-endian. Matches BitStream::Write(RakNetGUID) (BitStream.h:1220-1224).</summary>
    public void WriteRakNetGuid(RakNetGuid value) => WriteUInt64(value.Value);

    /// <summary>
    /// Writes an IPv4 SystemAddress: version byte 4, the bitwise-NOT of each address byte in network order,
    /// then the port big-endian. Matches BitStream::Write(SystemAddress).
    /// </summary>
    /// <remarks>
    /// RakNet serialises IPv6 addresses as a raw platform sockaddr_in6, whose layout differs between
    /// operating systems, so IPv6 is not supported here.
    /// </remarks>
    public void WriteAddress(IPEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        if (endPoint.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Only IPv4 SystemAddress serialisation is supported.");
        WriteByte(4);
        Span<byte> address = stackalloc byte[4];
        endPoint.Address.TryWriteBytes(address, out _);
        foreach (var b in address)
            WriteByte((byte)~b);
        WriteUInt16((ushort)endPoint.Port);
    }

    /// <summary>
    /// Writes a SystemAddress as BitStream::Write(SystemAddress) does. IPv6 follows the platform sockaddr_in6 caveat in
    /// docs/plan.md.
    /// </summary>
    /// <exception cref="NotSupportedException">The address is IPv6.</exception>
    public void WriteAddress(SystemAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new NotSupportedException("Only IPv4 SystemAddress serialisation is supported.");
        WriteByte(4);
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteAddressBytes(bytes, out _);
        foreach (var b in bytes)
            WriteByte((byte)~b);
        WriteUInt16(address.Port);
    }

    private readonly void EnsureCapacity(int bits)
    {
        if (_bitPosition + bits > _buffer.Length * 8)
            throw new InvalidOperationException(
                $"BitStreamWriter buffer of {_buffer.Length} bytes cannot hold {bits} more bits at bit {_bitPosition}."
            );
    }
}
