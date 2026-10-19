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

    /// <summary>
    /// The read position in bits; BitStream::GetReadOffset and SetReadOffset (BitStream.h:508-511). Setting 0 is
    /// ResetReadPointer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Set outside 0 to the reader's bit length.</exception>
    public int BitPosition
    {
        readonly get => _bitPosition;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, _bitLength);
            _bitPosition = value;
        }
    }

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
    /// Reads <paramref name="bitCount"/> bits into <paramref name="destination"/>: whole bytes in order, then the final
    /// partial byte right-aligned, or with <paramref name="alignBitsToRight"/> false left as it sits in the stream.
    /// Matches BitStream::ReadBits (BitStream.cpp:551-617).
    /// </summary>
    /// <remarks>
    /// Left-aligned, RakNet does not mask the final byte, so its low bits hold the bits of the stream byte that follow
    /// the ones read (BitStream.cpp:579-581); this reader does the same.
    /// </remarks>
    public bool TryReadBits(scoped Span<byte> destination, int bitCount, bool alignBitsToRight = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, destination.Length * 8);
        if (BitsRemaining < bitCount)
            return false;
        var wholeBytes = bitCount >> 3;
        for (var i = 0; i < wholeBytes; i++)
            TryReadBits(8, out destination[i]);
        var remainder = bitCount & 7;
        if (remainder == 0)
            return true;
        if (alignBitsToRight)
        {
            TryReadBits(remainder, out destination[wholeBytes]);
            return true;
        }

        var byteIndex = _bitPosition >> 3;
        var bitInByte = _bitPosition & 7;
        var value = (byte)(_buffer[byteIndex] << bitInByte);
        if (bitInByte > 0 && remainder > 8 - bitInByte)
            value |= (byte)(_buffer[byteIndex + 1] >> (8 - bitInByte));
        destination[wholeBytes] = value;
        _bitPosition += remainder;
        return true;
    }

    /// <summary>
    /// Copies <paramref name="bitCount"/> bits into <paramref name="destination"/>; false, without moving, when fewer
    /// are unread. Matches BitStream::Read(BitStream*, numberOfBits) (BitStream.cpp:244-250).
    /// </summary>
    public bool TryReadBits(BitStream destination, int bitCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (BitsRemaining < bitCount)
            return false;
        destination.Write(ref this, bitCount);
        return true;
    }

    /// <summary>
    /// Skips <paramref name="bitCount"/> bits; false, without moving, when fewer are unread. BitStream::IgnoreBits
    /// (BitStream.cpp:912-915) moves regardless and lets the next read fail.
    /// </summary>
    public bool IgnoreBits(int bitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bitCount);
        if (BitsRemaining < bitCount)
            return false;
        _bitPosition += bitCount;
        return true;
    }

    /// <summary>Skips <paramref name="byteCount"/> bytes' worth of bits. See <see cref="IgnoreBits"/>.</summary>
    public bool IgnoreBytes(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        return IgnoreBits(checked(byteCount * 8));
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

    /// <summary>Reads a GUID as BitStream::Read(RakNetGUID) does (BitStream.h:1562-1566): a big-endian u64.</summary>
    public bool TryReadRakNetGuid(out RakNetGuid value)
    {
        var ok = TryReadUInt64(out var raw);
        value = new RakNetGuid(raw);
        return ok;
    }

    /// <summary>
    /// Reads a SystemAddress as BitStream::Read(SystemAddress) does; false on a truncated or unsupported address.
    /// </summary>
    /// <remarks>
    /// Not an overload of <see cref="TryReadAddress"/>, because <c>TryReadAddress(out var a)</c> would then be ambiguous.
    /// </remarks>
    public bool TryReadSystemAddress(out SystemAddress address)
    {
        address = SystemAddress.Unassigned;
        var start = _bitPosition;
        if (!TryReadByte(out var version) || version != 4 || !TryReadUInt32(out var inverted) || !TryReadUInt16(out var port))
        {
            _bitPosition = start;
            return false;
        }
        address = new SystemAddress(~inverted, port);
        return true;
    }

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
