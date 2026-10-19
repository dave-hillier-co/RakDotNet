using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// The RakNet BitStream encodings used by application and plugin payloads (BitStream.h), as extensions on
/// <see cref="BitStreamWriter"/> and <see cref="BitStreamReader"/> so they work for stack buffers and
/// <see cref="BitStream"/> alike. Each encoding is checked against native output (tools/bitstream-vectors).
/// </summary>
/// <remarks>
/// <para>
/// Readers return false on truncated input and leave the position unchanged. Writers treat RakNet's assertions as
/// programming errors and throw; RakNet's release build clamps instead, which these writers do wherever RakNet's
/// release build gives a defined result.
/// </para>
/// <para>
/// Strings are UTF-8, since RakNet's are C strings of bytes. The length limits count bytes.
/// </para>
/// </remarks>
public static partial class BitStreamEncodings
{
    /// <summary>
    /// BitStream::WriteCompressed for an integer (BitStream.h:1275-1300, BitStream.cpp:489-548). RakNet reverses the
    /// value to big-endian and then drops zero bytes from the end of that array, so on every little-endian host the
    /// low-order zero bytes become single bits, and a high nibble of zero in the last byte left (the most significant)
    /// becomes one bit. Signed values are compressed as unsigned, as the template passes <c>unsignedData = true</c>.
    /// </summary>
    public static void WriteCompressed<T>(this ref BitStreamWriter writer, T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        value.WriteBigEndian(bytes);
        for (var current = bytes.Length - 1; current > 0; current--)
        {
            if (bytes[current] == 0)
            {
                writer.WriteBit(true);
                continue;
            }
            writer.WriteBit(false);
            writer.WriteBytes(bytes[..(current + 1)]);
            return;
        }
        if ((bytes[0] & 0xF0) == 0)
        {
            writer.WriteBit(true);
            writer.WriteBits(bytes[0], 4);
        }
        else
        {
            writer.WriteBit(false);
            writer.WriteByte(bytes[0]);
        }
    }

    /// <summary>BitStream::ReadCompressed for an integer (BitStream.cpp:617-693).</summary>
    public static bool TryReadCompressed<T>(this ref BitStreamReader reader, out T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        value = default;
        var r = reader;
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        bytes.Clear();
        var current = bytes.Length - 1;
        for (; current > 0; current--)
        {
            if (!r.TryReadBit(out var matched))
                return false;
            if (matched)
                continue;
            if (!r.TryReadBytes(bytes[..(current + 1)]))
                return false;
            return Finish(ref reader, r, bytes, out value);
        }

        if (!r.TryReadBit(out var halfByte))
            return false;
        if (halfByte ? !r.TryReadBits(4, out bytes[0]) : !r.TryReadByte(out bytes[0]))
            return false;
        return Finish(ref reader, r, bytes, out value);

        static bool Finish(ref BitStreamReader reader, BitStreamReader advanced, scoped Span<byte> bigEndian, out T result)
        {
            result = FromBytes<T>(bigEndian, bigEndian: true);
            reader = advanced;
            return true;
        }
    }

    /// <summary>
    /// BitStream::WriteBitsFromIntegerRange (BitStream.h:1752-1784): the offset from <paramref name="minimum"/> in the
    /// bits that <c>maximum - minimum</c> needs, its bytes in little-endian order (RakNet writes the host's bytes and
    /// every supported host is little-endian), the final byte's low bits. With <paramref name="allowOutsideRange"/> a flag
    /// bit comes first, and a value outside the range follows it whole and big-endian.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="maximum"/> is less than <paramref name="minimum"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside the range and that is not allowed.</exception>
    public static void WriteBitsFromIntegerRange<T>(this ref BitStreamWriter writer, T value, T minimum, T maximum, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (maximum < minimum)
            throw new ArgumentException("The maximum is less than the minimum.", nameof(maximum));
        writer.WriteBitsFromIntegerRange(value, minimum, maximum, RequiredBits(minimum, maximum), allowOutsideRange);
    }

    /// <summary>
    /// BitStream::WriteBitsFromIntegerRange with the bit count given by the caller (BitStream.h:1758-1784): the offset
    /// from <paramref name="minimum"/> in <paramref name="requiredBits"/> bits, laid out as the other overload lays it out.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="maximum"/> is less than <paramref name="minimum"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside the range and that is not allowed, or <paramref name="requiredBits"/> is negative or wider
    /// than <typeparamref name="T"/>.
    /// </exception>
    public static void WriteBitsFromIntegerRange<T>(this ref BitStreamWriter writer, T value, T minimum, T maximum, int requiredBits, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (maximum < minimum)
            throw new ArgumentException("The maximum is less than the minimum.", nameof(maximum));
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requiredBits, Unsafe.SizeOf<T>() * 8);
        var outside = value < minimum || value > maximum;
        if (allowOutsideRange)
        {
            writer.WriteBit(outside);
            if (outside)
            {
                WriteRaw(ref writer, value);
                return;
            }
        }
        else if (outside)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The value is outside the range.");
        }

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        (value - minimum).WriteLittleEndian(bytes);
        writer.WriteBits(bytes, requiredBits);
    }

    /// <summary>
    /// BitStream::ReadBitsFromIntegerRange (BitStream.h:1880-1904); the range and flag must match the writer's. A range
    /// of one value takes no bits and reads as <paramref name="minimum"/>, where RakNet's ReadBits of 0 bits fails.
    /// </summary>
    public static bool TryReadBitsFromIntegerRange<T>(this ref BitStreamReader reader, T minimum, T maximum, out T value, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (maximum < minimum)
            throw new ArgumentException("The maximum is less than the minimum.", nameof(maximum));
        return reader.TryReadBitsFromIntegerRange(minimum, maximum, RequiredBits(minimum, maximum), out value, allowOutsideRange);
    }

    /// <summary>
    /// BitStream::ReadBitsFromIntegerRange with the bit count given by the caller (BitStream.h:1886-1904); the range, bit
    /// count and flag must match the writer's. A bit count of 0 reads as <paramref name="minimum"/>.
    /// </summary>
    public static bool TryReadBitsFromIntegerRange<T>(this ref BitStreamReader reader, T minimum, T maximum, int requiredBits, out T value, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T>
    {
        value = default;
        if (maximum < minimum)
            throw new ArgumentException("The maximum is less than the minimum.", nameof(maximum));
        ArgumentOutOfRangeException.ThrowIfNegative(requiredBits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(requiredBits, Unsafe.SizeOf<T>() * 8);
        var r = reader;
        if (allowOutsideRange)
        {
            if (!r.TryReadBit(out var outside))
                return false;
            if (outside)
            {
                if (!TryReadRaw(ref r, out value))
                    return false;
                reader = r;
                return true;
            }
        }

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        bytes.Clear();
        if (!r.TryReadBits(bytes, requiredBits))
            return false;
        value = FromBytes<T>(bytes, bigEndian: false) + minimum;
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteAlignedBytesSafe (BitStream.cpp:355-365): the whole length of <paramref name="source"/> as a
    /// compressed u32, then at most <paramref name="maxBytes"/> of its bytes aligned to a byte. An empty source writes the
    /// compressed 0 only.
    /// </summary>
    public static void WriteAlignedBytesSafe(this ref BitStreamWriter writer, scoped ReadOnlySpan<byte> source, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (source.IsEmpty)
        {
            writer.WriteCompressed(0u);
            return;
        }
        writer.WriteCompressed((uint)source.Length);
        writer.WriteAlignedBytes(source[..Math.Min(source.Length, maxBytes)]);
    }

    /// <summary>
    /// BitStream::ReadAlignedBytesSafe (BitStream.cpp:395-404): a compressed u32 length capped at
    /// <paramref name="maxBytes"/>, then that many aligned bytes as a slice of the input. A length of 0 does not align.
    /// </summary>
    public static bool TryReadAlignedBytesSafe(this ref BitStreamReader reader, int maxBytes, out ReadOnlySpan<byte> value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        value = default;
        var r = reader;
        if (!r.TryReadCompressed(out uint length))
            return false;
        var count = (int)Math.Min(length, (uint)maxBytes);
        if (count > 0 && !r.TryReadAlignedBytes(count, out value))
            return false;
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteFloat16 (BitStream.cpp:1159-1175): the position between <paramref name="minimum"/> and
    /// <paramref name="maximum"/> as a u16, clamped.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="maximum"/> is not greater than <paramref name="minimum"/>.</exception>
    public static void WriteFloat16(this ref BitStreamWriter writer, float value, float minimum, float maximum)
    {
        if (!(maximum > minimum))
            throw new ArgumentException("The maximum must be greater than the minimum.", nameof(maximum));
        var percentile = 65535.0f * (value - minimum) / (maximum - minimum);
        if (percentile < 0.0)
            percentile = 0.0f;
        if (percentile > 65535.0f)
            percentile = 65535.0f;
        writer.WriteUInt16((ushort)percentile);
    }

    /// <summary>BitStream::ReadFloat16 (BitStream.cpp:1136-1150).</summary>
    public static bool TryReadFloat16(this ref BitStreamReader reader, float minimum, float maximum, out float value)
    {
        value = default;
        if (!(maximum > minimum))
            throw new ArgumentException("The maximum must be greater than the minimum.", nameof(maximum));
        if (!reader.TryReadUInt16(out var percentile))
            return false;
        value = minimum + (percentile / 65535.0f * (maximum - minimum));
        if (value < minimum)
            value = minimum;
        else if (value > maximum)
            value = maximum;
        return true;
    }

    /// <summary>BitStream::WriteNormVector (BitStream.h:1786-1796): each component as a float16 from -1 to 1.</summary>
    public static void WriteNormVector(this ref BitStreamWriter writer, Vector3 value)
    {
        writer.WriteFloat16(value.X, -1.0f, 1.0f);
        writer.WriteFloat16(value.Y, -1.0f, 1.0f);
        writer.WriteFloat16(value.Z, -1.0f, 1.0f);
    }

    /// <summary>BitStream::ReadNormVector (BitStream.h:1906-1917); false when truncated, where RakNet ignores it.</summary>
    public static bool TryReadNormVector(this ref BitStreamReader reader, out Vector3 value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadFloat16(-1.0f, 1.0f, out var x) || !r.TryReadFloat16(-1.0f, 1.0f, out var y) || !r.TryReadFloat16(-1.0f, 1.0f, out var z))
            return false;
        value = new Vector3(x, y, z);
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteVector (BitStream.h:1798-1812): the magnitude as a float, then, when it exceeds 0.00001, the
    /// direction as three compressed floats.
    /// </summary>
    public static void WriteVector(this ref BitStreamWriter writer, Vector3 value)
    {
        var magnitude = MathF.Sqrt((value.X * value.X) + (value.Y * value.Y) + (value.Z * value.Z));
        writer.WriteSingle(magnitude);
        if (magnitude > 0.00001f)
        {
            writer.WriteCompressed(value.X / magnitude);
            writer.WriteCompressed(value.Y / magnitude);
            writer.WriteCompressed(value.Z / magnitude);
        }
    }

    /// <summary>BitStream::ReadVector (BitStream.h:1919-1954).</summary>
    public static bool TryReadVector(this ref BitStreamReader reader, out Vector3 value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadSingle(out var magnitude))
            return false;
        if (magnitude > 0.00001f)
        {
            if (!r.TryReadCompressed(out float x) || !r.TryReadCompressed(out float y) || !r.TryReadCompressed(out float z))
                return false;
            value = new Vector3(x * magnitude, y * magnitude, z * magnitude);
        }
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteNormQuat (BitStream.h:1814-1825): the signs of w, x, y and z, then |x|, |y| and |z| as u16
    /// fractions; w is recomputed by the reader.
    /// </summary>
    public static void WriteNormQuat(this ref BitStreamWriter writer, Quaternion value) =>
        WriteNormQuat(ref writer, value.W, value.X, value.Y, value.Z);

    /// <summary>BitStream::ReadNormQuat (BitStream.h:1956-1985).</summary>
    public static bool TryReadNormQuat(this ref BitStreamReader reader, out Quaternion value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadBit(out var wNegative) || !r.TryReadBit(out var xNegative) || !r.TryReadBit(out var yNegative) || !r.TryReadBit(out var zNegative)
            || !r.TryReadUInt16(out var cx) || !r.TryReadUInt16(out var cy) || !r.TryReadUInt16(out var cz))
            return false;

        var x = (float)(cx / 65535.0);
        var y = (float)(cy / 65535.0);
        var z = (float)(cz / 65535.0);
        if (xNegative)
            x = -x;
        if (yNegative)
            y = -y;
        if (zNegative)
            z = -z;
        var difference = 1.0f - (x * x) - (y * y) - (z * z);
        if (difference < 0.0f)
            difference = 0.0f;
        var w = MathF.Sqrt(difference);
        if (wNegative)
            w = -w;
        value = new Quaternion(x, y, z, w);
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteOrthMatrix: the upper 3x3 of <paramref name="value"/> (M11 to M33) as RakNet's m00 to m22,
    /// written as a quaternion (BitStream.h:1827-1872).
    /// </summary>
    public static void WriteOrthMatrix(this ref BitStreamWriter writer, in Matrix4x4 value)
    {
        float m00 = value.M11, m01 = value.M12, m02 = value.M13;
        float m10 = value.M21, m11 = value.M22, m12 = value.M23;
        float m20 = value.M31, m21 = value.M32, m22 = value.M33;

        // The sums are floats and the square roots float overloads, as the template computes them for float.
        double qw = HalfRoot(1 + m00 + m11 + m22);
        double qx = HalfRoot(1 + m00 - m11 - m22);
        double qy = HalfRoot(1 - m00 + m11 - m22);
        double qz = HalfRoot(1 - m00 - m11 + m22);
        qx = Math.CopySign(qx, (double)(m21 - m12));
        qy = Math.CopySign(qy, (double)(m02 - m20));
        qz = Math.CopySign(qz, (double)(m10 - m01));
        WriteNormQuat(ref writer, qw, qx, qy, qz);

        static float HalfRoot(float sum) => MathF.Sqrt(sum < 0.0f ? 0.0f : sum) / 2;
    }

    /// <summary>BitStream::ReadOrthMatrix, into the upper 3x3 of an identity matrix (BitStream.h:1987-2022).</summary>
    public static bool TryReadOrthMatrix(this ref BitStreamReader reader, out Matrix4x4 value)
    {
        value = Matrix4x4.Identity;
        if (!reader.TryReadNormQuat(out var q))
            return false;

        double sqw = (double)q.W * q.W;
        double sqx = (double)q.X * q.X;
        double sqy = (double)q.Y * q.Y;
        double sqz = (double)q.Z * q.Z;
        value.M11 = (float)(sqx - sqy - sqz + sqw);
        value.M22 = (float)(-sqx + sqy - sqz + sqw);
        value.M33 = (float)(-sqx - sqy + sqz + sqw);

        double tmp1 = (double)q.X * q.Y;
        double tmp2 = (double)q.Z * q.W;
        value.M21 = (float)(2.0 * (tmp1 + tmp2));
        value.M12 = (float)(2.0 * (tmp1 - tmp2));

        tmp1 = (double)q.X * q.Z;
        tmp2 = (double)q.Y * q.W;
        value.M31 = (float)(2.0 * (tmp1 - tmp2));
        value.M13 = (float)(2.0 * (tmp1 + tmp2));
        tmp1 = (double)q.Y * q.Z;
        tmp2 = (double)q.X * q.W;
        value.M32 = (float)(2.0 * (tmp1 + tmp2));
        value.M23 = (float)(2.0 * (tmp1 - tmp2));
        return true;
    }

    /// <summary>
    /// BitStream::WriteCasted: <paramref name="value"/> written as the smaller <typeparamref name="TWire"/>, truncated
    /// as RakNet's C cast truncates it.
    /// </summary>
    public static void WriteCasted<TWire, TValue>(this ref BitStreamWriter writer, TValue value)
        where TWire : unmanaged, IBinaryInteger<TWire>
        where TValue : unmanaged, IBinaryInteger<TValue> => WriteRaw(ref writer, TWire.CreateTruncating(value));

    /// <summary>BitStream::ReadCasted.</summary>
    public static bool TryReadCasted<TWire, TValue>(this ref BitStreamReader reader, out TValue value)
        where TWire : unmanaged, IBinaryInteger<TWire>
        where TValue : unmanaged, IBinaryInteger<TValue>
    {
        value = default;
        if (!TryReadRaw(ref reader, out TWire wire))
            return false;
        value = TValue.CreateTruncating(wire);
        return true;
    }

    /// <summary>
    /// RakString::Serialize (RakString.cpp:1218-1223): a u16 byte length, then the UTF-8 bytes aligned to a byte.
    /// </summary>
    /// <exception cref="ArgumentException">The string is more than 65535 bytes of UTF-8.</exception>
    public static void WriteRakString(this ref BitStreamWriter writer, ReadOnlySpan<char> value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        if (length > ushort.MaxValue)
            throw new ArgumentException("A RakString holds at most 65535 bytes.", nameof(value));
        var bytes = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.UTF8.GetBytes(value, bytes);
        writer.WriteUInt16((ushort)length);
        writer.WriteAlignedBytes(bytes);
    }

    /// <summary>RakString::Deserialize (RakString.cpp:1234-1253): an empty string still aligns the read.</summary>
    public static bool TryReadRakString(this ref BitStreamReader reader, [NotNullWhen(true)] out string? value)
    {
        value = null;
        var r = reader;
        if (!r.TryReadUInt16(out var length))
            return false;
        if (length == 0)
        {
            r.AlignToByte();
            value = string.Empty;
        }
        else
        {
            if (!r.TryReadAlignedBytes(length, out var bytes))
                return false;
            value = Encoding.UTF8.GetString(bytes);
        }
        reader = r;
        return true;
    }

    /// <summary>
    /// StringCompressor::EncodeString with RakNet's default English Huffman table (StringCompressor.cpp:366-397): at most
    /// <paramref name="maxCharCount"/> - 1 bytes of UTF-8 are written, or all of them when the limit is 0 or more than
    /// their count. RakString::SerializeCompressed passes 0xFFFF.
    /// </summary>
    public static void WriteCompressedString(this ref BitStreamWriter writer, ReadOnlySpan<char> value, int maxCharCount)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var bytes = length <= 256 ? stackalloc byte[length] : new byte[length];
        Encoding.UTF8.GetBytes(value, bytes);
        StringCompressor.English.Write(ref writer, bytes, maxCharCount);
    }

    /// <summary>
    /// StringCompressor::DecodeString (StringCompressor.cpp:399-427): false for a limit that is not positive or a
    /// truncated stream; a string of <paramref name="maxCharCount"/> bytes or more keeps its first
    /// <paramref name="maxCharCount"/> - 1.
    /// </summary>
    public static bool TryReadCompressedString(this ref BitStreamReader reader, int maxCharCount, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!StringCompressor.English.TryRead(ref reader, maxCharCount, out var bytes))
            return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>BitStream::Write of any number type: its bytes reversed to big-endian (BitStream.h:1080-1102).</summary>
    internal static void WriteRaw<T>(ref BitStreamWriter writer, T value)
        where T : unmanaged
    {
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        if (BitConverter.IsLittleEndian)
            bytes.Reverse();
        writer.WriteBytes(bytes);
    }

    /// <summary>BitStream::Read of any number type (BitStream.h:1442-1473).</summary>
    internal static bool TryReadRaw<T>(ref BitStreamReader reader, out T value)
        where T : unmanaged
    {
        value = default;
        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<T>()];
        if (!reader.TryReadBytes(bytes))
            return false;
        if (BitConverter.IsLittleEndian)
            bytes.Reverse();
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    // The value whose two's-complement bytes these are, as RakNet's memcpy gives it.
    private static T FromBytes<T>(Span<byte> bytes, bool bigEndian)
        where T : unmanaged
    {
        if (bigEndian == BitConverter.IsLittleEndian)
            bytes.Reverse();
        return MemoryMarshal.Read<T>(bytes);
    }

    // BYTES_TO_BITS(sizeof(T)) - NumberOfLeadingZeroes(T(maximum - minimum)) (BitStream.h:1754-1757).
    private static int RequiredBits<T>(T minimum, T maximum)
        where T : unmanaged, IBinaryInteger<T> =>
        (Unsafe.SizeOf<T>() * 8) - int.CreateTruncating(T.LeadingZeroCount(maximum - minimum));

    // WriteNormQuat with the template's arithmetic: fabs promotes to double before the multiply.
    private static void WriteNormQuat(ref BitStreamWriter writer, double w, double x, double y, double z)
    {
        writer.WriteBit(w < 0.0);
        writer.WriteBit(x < 0.0);
        writer.WriteBit(y < 0.0);
        writer.WriteBit(z < 0.0);
        writer.WriteUInt16((ushort)(Math.Abs(x) * 65535.0));
        writer.WriteUInt16((ushort)(Math.Abs(y) * 65535.0));
        writer.WriteUInt16((ushort)(Math.Abs(z) * 65535.0));
    }
}
