using System.Buffers.Binary;
using System.Net;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// Reads bits using RakNet BitStream encoding. Every read returns false on insufficient or invalid data
/// and leaves the position unchanged; nothing throws on malformed input.
/// </summary>
public ref struct BitStreamReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private readonly int _bitLength;
    private int _bitPosition;

    public BitStreamReader(ReadOnlySpan<byte> buffer)
        : this(buffer, buffer.Length * 8) { }

    /// <summary>Reads at most <paramref name="bitLength"/> bits of <paramref name="buffer"/>.</summary>
    public BitStreamReader(ReadOnlySpan<byte> buffer, int bitLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitLength, buffer.Length * 8);
        _buffer = buffer;
        _bitLength = bitLength;
    }

    public readonly int BitPosition => _bitPosition;

    public readonly int BitsRemaining => _bitLength - _bitPosition;

    /// <summary>The unread bytes from the next byte boundary onward.</summary>
    public readonly ReadOnlySpan<byte> RemainingAlignedBytes => _buffer[Math.Min(_buffer.Length, (_bitPosition + 7) >> 3)..];

    public bool TryReadBit(out bool value)
    {
        if (BitsRemaining < 1)
        {
            value = false;
            return false;
        }
        value = (_buffer[_bitPosition >> 3] & (0x80 >> (_bitPosition & 7))) != 0;
        _bitPosition++;
        return true;
    }

    /// <summary>Reads <paramref name="count"/> bits (1 to 8), returned right-aligned.</summary>
    public bool TryReadBits(int count, out byte value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 8);
        if (BitsRemaining < count)
        {
            value = 0;
            return false;
        }
        var byteIndex = _bitPosition >> 3;
        var bitInByte = _bitPosition & 7;
        var combined = _buffer[byteIndex] << 8;
        if (bitInByte + count > 8)
            combined |= _buffer[byteIndex + 1];
        value = (byte)((combined >> (16 - bitInByte - count)) & ((1 << count) - 1));
        _bitPosition += count;
        return true;
    }

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits into <paramref name="destination"/>: whole bytes in order, then
    /// a right-aligned final partial byte. Matches BitStream::ReadBits with alignBitsToRight=true.
    /// </summary>
    public bool TryReadBits(scoped Span<byte> destination, int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, destination.Length * 8);
        if (BitsRemaining < bitCount)
            return false;
        var wholeBytes = bitCount >> 3;
        for (var i = 0; i < wholeBytes; i++)
            TryReadBits(8, out destination[i]);
        var remainder = bitCount & 7;
        if (remainder > 0)
            TryReadBits(remainder, out destination[wholeBytes]);
        return true;
    }

    /// <summary>Reads bytes at the current bit position without aligning first.</summary>
    public bool TryReadBytes(scoped Span<byte> destination) => TryReadBits(destination, destination.Length * 8);

    /// <summary>
    /// Aligns to the next byte boundary, then returns a slice of <paramref name="count"/> bytes without copying.
    /// Matches BitStream::ReadAlignedBytes.
    /// </summary>
    public bool TryReadAlignedBytes(int count, out ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var start = AlignedPosition();
        if (start + count * 8 > _bitLength)
        {
            value = default;
            return false;
        }
        value = _buffer.Slice(start >> 3, count);
        _bitPosition = start + count * 8;
        return true;
    }

    /// <summary>Skips to the next byte boundary. Matches BitStream::AlignReadToByteBoundary.</summary>
    public void AlignToByte() => _bitPosition = Math.Min(AlignedPosition(), _bitLength);

    public bool TryReadByte(out byte value) => TryReadBits(8, out value);

    public bool TryReadBool(out bool value) => TryReadBit(out value);

    public bool TryReadUInt16(out ushort value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ushort)];
        var ok = TryReadBytes(bytes);
        value = ok ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : default;
        return ok;
    }

    public bool TryReadInt16(out short value)
    {
        var ok = TryReadUInt16(out var raw);
        value = unchecked((short)raw);
        return ok;
    }

    public bool TryReadUInt32(out uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        var ok = TryReadBytes(bytes);
        value = ok ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : default;
        return ok;
    }

    public bool TryReadInt32(out int value)
    {
        var ok = TryReadUInt32(out var raw);
        value = unchecked((int)raw);
        return ok;
    }

    public bool TryReadUInt64(out ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        var ok = TryReadBytes(bytes);
        value = ok ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : default;
        return ok;
    }

    public bool TryReadInt64(out long value)
    {
        var ok = TryReadUInt64(out var raw);
        value = unchecked((long)raw);
        return ok;
    }

    public bool TryReadSingle(out float value)
    {
        var ok = TryReadUInt32(out var raw);
        value = BitConverter.UInt32BitsToSingle(raw);
        return ok;
    }

    public bool TryReadDouble(out double value)
    {
        var ok = TryReadUInt64(out var raw);
        value = BitConverter.UInt64BitsToDouble(raw);
        return ok;
    }

    /// <summary>Aligns to a byte boundary, then reads three bytes little-endian. Matches BitStream::Read(uint24_t).</summary>
    public bool TryReadUInt24(out UInt24 value)
    {
        if (!TryReadAlignedBytes(3, out var bytes))
        {
            value = default;
            return false;
        }
        value = new UInt24((uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16)));
        return true;
    }

    /// <summary>
    /// Reads a SystemAddress as BitStream::Read(SystemAddress) does; false on a truncated or unsupported address.
    /// </summary>
    /// <remarks>
    /// Not an overload of <see cref="TryReadAddress"/>, because <c>TryReadAddress(out var a)</c> would then be ambiguous.
    /// </remarks>
    public bool TryReadSystemAddress(out SystemAddress address) => throw new NotImplementedException();

    /// <summary>Reads an IPv4 SystemAddress. See <see cref="BitStreamWriter.WriteAddress(IPEndPoint)"/>.</summary>
    public bool TryReadAddress(out IPEndPoint? endPoint)
    {
        endPoint = null;
        var start = _bitPosition;
        Span<byte> address = stackalloc byte[4];
        if (!TryReadByte(out var version) || version != 4 || !TryReadBytes(address) || !TryReadUInt16(out var port))
        {
            _bitPosition = start;
            return false;
        }
        for (var i = 0; i < address.Length; i++)
            address[i] = (byte)~address[i];
        endPoint = new IPEndPoint(new IPAddress(address), port);
        return true;
    }

    private readonly int AlignedPosition() => (_bitPosition + 7) & ~7;
}
