using System.Numerics;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet's <c>Serialize(bool writeToBitstream, ...)</c> idiom: one method body that either writes or reads
/// (BitStream.h:90-224, 938-1078). Each member writes with the matching <see cref="BitStreamWriter"/> or
/// <see cref="BitStreamEncodings"/> method, or reads with the matching one, and returns false when a read fails.
/// </summary>
/// <remarks>
/// A ref struct cannot hold a <c>ref</c> to another ref struct, so the serializer owns its writer or reader by value;
/// <see cref="Writer"/> and <see cref="Reader"/> return the advanced copy.
/// </remarks>
public ref struct BitSerializer
{
    private BitStreamWriter _writer;
    private BitStreamReader _reader;

    public BitSerializer(BitStreamWriter writer)
    {
        _writer = writer;
        IsWriting = true;
    }

    public BitSerializer(BitStreamReader reader) => _reader = reader;

    public readonly bool IsWriting { get; }

    /// <summary>The writer after the calls so far. Throws when reading.</summary>
    public readonly BitStreamWriter Writer =>
        IsWriting ? _writer : throw new InvalidOperationException("The serializer is reading.");

    /// <summary>The reader after the calls so far. Throws when writing.</summary>
    public readonly BitStreamReader Reader =>
        IsWriting ? throw new InvalidOperationException("The serializer is writing.") : _reader;

    /// <summary>Writes <paramref name="value"/>, or reads into it; false when a read fails.</summary>
    /// <remarks>BitStream::Serialize: the value's bytes big-endian (BitStream.h:1080-1102).</remarks>
    public bool Serialize<T>(ref T value)
        where T : unmanaged, INumber<T>
    {
        if (IsWriting)
        {
            BitStreamEncodings.WriteRaw(ref _writer, value);
            return true;
        }
        return Assign(BitStreamEncodings.TryReadRaw(ref _reader, out T read), read, ref value);
    }

    /// <summary>BitStream::Serialize of a bool: one bit.</summary>
    public bool Serialize(ref bool value)
    {
        if (IsWriting)
        {
            _writer.WriteBit(value);
            return true;
        }
        return Assign(_reader.TryReadBit(out var read), read, ref value);
    }

    /// <summary>BitStream::Serialize of a SystemAddress (BitStream.h:1142-1162, 1494-1520).</summary>
    public bool Serialize(ref SystemAddress value)
    {
        if (IsWriting)
        {
            _writer.WriteAddress(value);
            return true;
        }
        return Assign(_reader.TryReadSystemAddress(out var read), read, ref value);
    }

    /// <summary>BitStream::Serialize of a RakNetGUID: its u64 big-endian.</summary>
    public bool Serialize(ref RakNetGuid value)
    {
        if (IsWriting)
        {
            _writer.WriteRakNetGuid(value);
            return true;
        }
        return Assign(_reader.TryReadRakNetGuid(out var read), read, ref value);
    }

    /// <summary>BitStream::Serialize of a uint24_t: byte-aligned, three bytes little-endian.</summary>
    public bool Serialize(ref UInt24 value)
    {
        if (IsWriting)
        {
            _writer.WriteUInt24(value);
            return true;
        }
        return Assign(_reader.TryReadUInt24(out var read), read, ref value);
    }

    /// <summary>
    /// BitStream::Serialize(char*, numberOfBytes) (BitStream.h:997-1004): the bytes at the current bit position, not
    /// aligned. Reading fills <paramref name="data"/>.
    /// </summary>
    public bool Serialize(scoped Span<byte> data)
    {
        if (IsWriting)
        {
            _writer.WriteBytes(data);
            return true;
        }
        return _reader.TryReadBytes(data);
    }

    /// <summary>
    /// BitStream::SerializeBits (BitStream.h:1072-1078): <paramref name="bitCount"/> bits of <paramref name="data"/>,
    /// the final partial byte right-aligned or, with <paramref name="rightAlignedBits"/> false, left-aligned.
    /// </summary>
    public bool SerializeBits(scoped Span<byte> data, int bitCount, bool rightAlignedBits = true)
    {
        if (IsWriting)
        {
            _writer.WriteBits(data, bitCount, rightAlignedBits);
            return true;
        }
        return _reader.TryReadBits(data, bitCount, rightAlignedBits);
    }

    /// <summary>BitStream::SerializeCompressed.</summary>
    public bool SerializeCompressed<T>(ref T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (IsWriting)
        {
            _writer.WriteCompressed(value);
            return true;
        }
        return Assign(_reader.TryReadCompressed(out T read), read, ref value);
    }

    /// <summary>BitStream::SerializeCompressed of a float between -1 and 1: a u16.</summary>
    public bool SerializeCompressed(ref float value)
    {
        if (IsWriting)
        {
            _writer.WriteCompressed(value);
            return true;
        }
        return Assign(_reader.TryReadCompressed(out float read), read, ref value);
    }

    /// <summary>BitStream::SerializeCompressed of a double between -1 and 1: a u32.</summary>
    public bool SerializeCompressed(ref double value)
    {
        if (IsWriting)
        {
            _writer.WriteCompressed(value);
            return true;
        }
        return Assign(_reader.TryReadCompressed(out double read), read, ref value);
    }

    /// <summary>BitStream::SerializeDelta (BitStream.h:947-955): <see cref="BitStreamEncodings.WriteDelta{T}(ref BitStreamWriter, T, T)"/>.</summary>
    public bool SerializeDelta<T>(ref T value, T last)
        where T : unmanaged, INumber<T>
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value, last);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta with no last value (BitStream.h:957-965).</summary>
    public bool SerializeDelta<T>(ref T value)
        where T : unmanaged, INumber<T>
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a bool: the bool alone.</summary>
    public bool SerializeDelta(ref bool value, bool last)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value, last);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>
    /// BitStream::SerializeDelta of a bool with no last value: writes a 1 bit then the bool, reads one bit, as RakNet does.
    /// </summary>
    public bool SerializeDelta(ref bool value)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a SystemAddress.</summary>
    public bool SerializeDelta(ref SystemAddress value, SystemAddress last)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value, last);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a SystemAddress with no last value.</summary>
    public bool SerializeDelta(ref SystemAddress value)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a RakNetGUID.</summary>
    public bool SerializeDelta(ref RakNetGuid value, RakNetGuid last)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value, last);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a RakNetGUID with no last value.</summary>
    public bool SerializeDelta(ref RakNetGuid value)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a uint24_t.</summary>
    public bool SerializeDelta(ref UInt24 value, UInt24 last)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value, last);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeDelta of a uint24_t with no last value.</summary>
    public bool SerializeDelta(ref UInt24 value)
    {
        if (IsWriting)
        {
            _writer.WriteDelta(value);
            return true;
        }
        return _reader.TryReadDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta (BitStream.h:977-985).</summary>
    public bool SerializeCompressedDelta<T>(ref T value, T last)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value, last);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta with no last value (BitStream.h:987-995).</summary>
    public bool SerializeCompressedDelta<T>(ref T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a float.</summary>
    public bool SerializeCompressedDelta(ref float value, float last)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value, last);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a float with no last value.</summary>
    public bool SerializeCompressedDelta(ref float value)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a double.</summary>
    public bool SerializeCompressedDelta(ref double value, double last)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value, last);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a double with no last value.</summary>
    public bool SerializeCompressedDelta(ref double value)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a bool: the bool alone.</summary>
    public bool SerializeCompressedDelta(ref bool value, bool last)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value, last);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeCompressedDelta of a bool with no last value: the bool alone.</summary>
    public bool SerializeCompressedDelta(ref bool value)
    {
        if (IsWriting)
        {
            _writer.WriteCompressedDelta(value);
            return true;
        }
        return _reader.TryReadCompressedDelta(ref value);
    }

    /// <summary>BitStream::SerializeFloat16 (BitStream.cpp:1151-1158).</summary>
    public bool SerializeFloat16(ref float value, float minimum, float maximum)
    {
        if (IsWriting)
        {
            _writer.WriteFloat16(value, minimum, maximum);
            return true;
        }
        return Assign(_reader.TryReadFloat16(minimum, maximum, out var read), read, ref value);
    }

    /// <summary>BitStream::SerializeCasted (BitStream.h:1006-1012).</summary>
    public bool SerializeCasted<TWire, TValue>(ref TValue value)
        where TWire : unmanaged, IBinaryInteger<TWire>
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        if (IsWriting)
        {
            _writer.WriteCasted<TWire, TValue>(value);
            return true;
        }
        return Assign(_reader.TryReadCasted<TWire, TValue>(out var read), read, ref value);
    }

    /// <summary>BitStream::SerializeBitsFromIntegerRange (BitStream.h:1014-1019).</summary>
    public bool SerializeBitsFromIntegerRange<T>(ref T value, T minimum, T maximum, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (IsWriting)
        {
            _writer.WriteBitsFromIntegerRange(value, minimum, maximum, allowOutsideRange);
            return true;
        }
        return Assign(_reader.TryReadBitsFromIntegerRange(minimum, maximum, out T read, allowOutsideRange), read, ref value);
    }

    /// <summary>BitStream::SerializeBitsFromIntegerRange with an explicit bit count (BitStream.h:1020-1026).</summary>
    public bool SerializeBitsFromIntegerRange<T>(ref T value, T minimum, T maximum, int requiredBits, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (IsWriting)
        {
            _writer.WriteBitsFromIntegerRange(value, minimum, maximum, requiredBits, allowOutsideRange);
            return true;
        }
        return Assign(_reader.TryReadBitsFromIntegerRange(minimum, maximum, requiredBits, out T read, allowOutsideRange), read, ref value);
    }

    /// <summary>BitStream::SerializeNormVector (BitStream.h:1028-1036).</summary>
    public bool SerializeNormVector(ref Vector3 value)
    {
        if (IsWriting)
        {
            _writer.WriteNormVector(value);
            return true;
        }
        return Assign(_reader.TryReadNormVector(out var read), read, ref value);
    }

    /// <summary>BitStream::SerializeVector (BitStream.h:1038-1046).</summary>
    public bool SerializeVector(ref Vector3 value)
    {
        if (IsWriting)
        {
            _writer.WriteVector(value);
            return true;
        }
        return Assign(_reader.TryReadVector(out var read), read, ref value);
    }

    /// <summary>BitStream::SerializeNormQuat (BitStream.h:1048-1056).</summary>
    public bool SerializeNormQuat(ref Quaternion value)
    {
        if (IsWriting)
        {
            _writer.WriteNormQuat(value);
            return true;
        }
        return Assign(_reader.TryReadNormQuat(out var read), read, ref value);
    }

    /// <summary>
    /// BitStream::SerializeOrthMatrix (BitStream.h:1059-1070): the upper 3x3 of <paramref name="value"/>. Reading sets
    /// only M11 to M33 and leaves the other elements as they were.
    /// </summary>
    public bool SerializeOrthMatrix(ref Matrix4x4 value)
    {
        if (IsWriting)
        {
            _writer.WriteOrthMatrix(value);
            return true;
        }
        if (!_reader.TryReadOrthMatrix(out var read))
            return false;
        (value.M11, value.M12, value.M13) = (read.M11, read.M12, read.M13);
        (value.M21, value.M22, value.M23) = (read.M21, read.M22, read.M23);
        (value.M31, value.M32, value.M33) = (read.M31, read.M32, read.M33);
        return true;
    }

    private static bool Assign<T>(bool ok, T read, ref T value)
    {
        if (ok)
            value = read;
        return ok;
    }
}
