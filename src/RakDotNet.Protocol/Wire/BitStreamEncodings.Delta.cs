using System.Numerics;

namespace RakDotNet.Protocol.Wire;

// The compressed forms of non-integer types, and the delta and compressed delta encodings (BitStream.h:1236-1432,
// 1585-1724). RakNet's templates accept any type with Write and operator==, so each type it specialises or that has a
// Write gets an overload here; integers and floating point go through the generic INumber forms.
public static partial class BitStreamEncodings
{
    /// <summary>
    /// BitStream::WriteCompressed(float) (BitStream.h:1327-1338): clamped to -1..1, then <c>(value + 1) * 32767.5</c>
    /// as a u16.
    /// </summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, float value)
    {
        if (value < -1.0f)
            value = -1.0f;
        if (value > 1.0f)
            value = 1.0f;
        writer.WriteUInt16((ushort)((value + 1.0f) * 32767.5f));
    }

    /// <summary>BitStream::ReadCompressed(float) (BitStream.h:1665-1676).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out float value)
    {
        value = default;
        if (!reader.TryReadUInt16(out var compressed))
            return false;
        value = (compressed / 32767.5f) - 1.0f;
        return true;
    }

    /// <summary>
    /// BitStream::WriteCompressed(double) (BitStream.h:1341-1351): clamped to -1..1, then <c>(value + 1) * 2^31</c> as a
    /// u32. 1.0 overflows the u32; .NET saturates the conversion to 0xFFFFFFFF, as RakNet built for arm64 does (an x64
    /// build truncates it to 0).
    /// </summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, double value)
    {
        if (value < -1.0)
            value = -1.0;
        if (value > 1.0)
            value = 1.0;
        writer.WriteUInt32((uint)((value + 1.0) * 2147483648.0));
    }

    /// <summary>BitStream::ReadCompressed(double) (BitStream.h:1678-1689).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out double value)
    {
        value = default;
        if (!reader.TryReadUInt32(out var compressed))
            return false;
        value = (compressed / 2147483648.0) - 1.0;
        return true;
    }

    /// <summary>BitStream::WriteCompressed(bool) is Write(bool), one bit (BitStream.h:1320-1324).</summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, bool value) => writer.WriteBit(value);

    /// <summary>BitStream::ReadCompressed(bool) is Read(bool) (BitStream.h:1659-1663).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out bool value) => reader.TryReadBit(out value);

    /// <summary>BitStream::WriteCompressed(SystemAddress) is Write(SystemAddress) (BitStream.h:1305-1308).</summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, SystemAddress value) => writer.WriteAddress(value);

    /// <summary>BitStream::ReadCompressed(SystemAddress) is Read(SystemAddress) (BitStream.h:1641-1645).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out SystemAddress value) => reader.TryReadSystemAddress(out value);

    /// <summary>BitStream::WriteCompressed(RakNetGUID) is Write(RakNetGUID) (BitStream.h:1310-1314).</summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, RakNetGuid value) => writer.WriteRakNetGuid(value);

    /// <summary>BitStream::ReadCompressed(RakNetGUID) is Read(RakNetGUID) (BitStream.h:1653-1657).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out RakNetGuid value) => reader.TryReadRakNetGuid(out value);

    /// <summary>BitStream::WriteCompressed(uint24_t) is Write(uint24_t) (BitStream.h:1315-1319).</summary>
    public static void WriteCompressed(this ref BitStreamWriter writer, UInt24 value) => writer.WriteUInt24(value);

    /// <summary>BitStream::ReadCompressed(uint24_t) is Read(uint24_t) (BitStream.h:1647-1651).</summary>
    public static bool TryReadCompressed(this ref BitStreamReader reader, out UInt24 value) => reader.TryReadUInt24(out value);

    /// <summary>
    /// BitStream::WriteDelta (BitStream.h:1235-1247): a 0 bit if <paramref name="value"/> equals <paramref name="last"/>,
    /// else a 1 bit then the value big-endian. Floating point compares as C++ <c>==</c> does, so NaN is always written.
    /// </summary>
    public static void WriteDelta<T>(this ref BitStreamWriter writer, T value, T last)
        where T : unmanaged, INumber<T>
    {
        writer.WriteBit(value != last);
        if (value != last)
            WriteRaw(ref writer, value);
    }

    /// <summary>BitStream::WriteDelta with no last value (BitStream.h:1260-1266): a 1 bit, then the value.</summary>
    public static void WriteDelta<T>(this ref BitStreamWriter writer, T value)
        where T : unmanaged, INumber<T>
    {
        writer.WriteBit(true);
        WriteRaw(ref writer, value);
    }

    /// <summary>
    /// BitStream::ReadDelta (BitStream.h:1576-1586): leaves <paramref name="value"/> unchanged when the sender wrote
    /// the same value; false, without moving, when truncated.
    /// </summary>
    public static bool TryReadDelta<T>(this ref BitStreamReader reader, ref T value)
        where T : unmanaged, INumber<T>
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!TryReadRaw(ref r, out T read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteDelta(bool, bool) (BitStream.h:1252-1258): writes <paramref name="value"/> only, as one bit.
    /// </summary>
    public static void WriteDelta(this ref BitStreamWriter writer, bool value, bool last) => writer.WriteBit(value);

    /// <summary>
    /// BitStream::WriteDelta(bool) with no last value. It has no bool specialisation, so it writes a 1 bit then the bool,
    /// which <see cref="TryReadDelta(ref BitStreamReader, ref bool)"/> does not read back; RakNet has the same asymmetry.
    /// </summary>
    public static void WriteDelta(this ref BitStreamWriter writer, bool value)
    {
        writer.WriteBit(true);
        writer.WriteBit(value);
    }

    /// <summary>BitStream::ReadDelta(bool) (BitStream.h:1593-1597): reads the one bit.</summary>
    public static bool TryReadDelta(this ref BitStreamReader reader, ref bool value)
    {
        if (!reader.TryReadBit(out var read))
            return false;
        value = read;
        return true;
    }

    /// <summary>BitStream::WriteDelta of a SystemAddress: the flag, then the address when it differs.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, SystemAddress value, SystemAddress last)
    {
        writer.WriteBit(value != last);
        if (value != last)
            writer.WriteAddress(value);
    }

    /// <summary>BitStream::WriteDelta of a SystemAddress with no last value: a 1 bit, then the address.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, SystemAddress value)
    {
        writer.WriteBit(true);
        writer.WriteAddress(value);
    }

    /// <summary>BitStream::ReadDelta of a SystemAddress.</summary>
    public static bool TryReadDelta(this ref BitStreamReader reader, ref SystemAddress value)
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!r.TryReadSystemAddress(out var read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>BitStream::WriteDelta of a RakNetGUID: the flag, then the GUID when it differs.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, RakNetGuid value, RakNetGuid last) =>
        writer.WriteDelta(value.Value, last.Value);

    /// <summary>BitStream::WriteDelta of a RakNetGUID with no last value: a 1 bit, then the GUID.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, RakNetGuid value) => writer.WriteDelta(value.Value);

    /// <summary>BitStream::ReadDelta of a RakNetGUID.</summary>
    public static bool TryReadDelta(this ref BitStreamReader reader, ref RakNetGuid value)
    {
        var raw = value.Value;
        if (!reader.TryReadDelta(ref raw))
            return false;
        value = new RakNetGuid(raw);
        return true;
    }

    /// <summary>BitStream::WriteDelta of a uint24_t: the flag, then the byte-aligned value when it differs.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, UInt24 value, UInt24 last)
    {
        writer.WriteBit(value != last);
        if (value != last)
            writer.WriteUInt24(value);
    }

    /// <summary>BitStream::WriteDelta of a uint24_t with no last value: a 1 bit, then the value.</summary>
    public static void WriteDelta(this ref BitStreamWriter writer, UInt24 value)
    {
        writer.WriteBit(true);
        writer.WriteUInt24(value);
    }

    /// <summary>BitStream::ReadDelta of a uint24_t.</summary>
    public static bool TryReadDelta(this ref BitStreamReader reader, ref UInt24 value)
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!r.TryReadUInt24(out var read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>
    /// BitStream::WriteCompressedDelta (BitStream.h:1395-1410): a 0 bit if <paramref name="value"/> equals
    /// <paramref name="last"/>, else a 1 bit then the compressed value.
    /// </summary>
    public static void WriteCompressedDelta<T>(this ref BitStreamWriter writer, T value, T last)
        where T : unmanaged, IBinaryInteger<T>
    {
        writer.WriteBit(value != last);
        if (value != last)
            writer.WriteCompressed(value);
    }

    /// <summary>BitStream::WriteCompressedDelta with no last value (BitStream.h:1420-1426): a 1 bit, then the value.</summary>
    public static void WriteCompressedDelta<T>(this ref BitStreamWriter writer, T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        writer.WriteBit(true);
        writer.WriteCompressed(value);
    }

    /// <summary>BitStream::ReadCompressedDelta (BitStream.h:1707-1716).</summary>
    public static bool TryReadCompressedDelta<T>(this ref BitStreamReader reader, ref T value)
        where T : unmanaged, IBinaryInteger<T>
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!r.TryReadCompressed(out T read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>BitStream::WriteCompressedDelta of a float: the flag, then the compressed float when it differs.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, float value, float last)
    {
        writer.WriteBit(value != last);
        if (value != last)
            writer.WriteCompressed(value);
    }

    /// <summary>BitStream::WriteCompressedDelta of a float with no last value.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, float value)
    {
        writer.WriteBit(true);
        writer.WriteCompressed(value);
    }

    /// <summary>BitStream::ReadCompressedDelta of a float.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref float value)
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!r.TryReadCompressed(out float read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>BitStream::WriteCompressedDelta of a double: the flag, then the compressed double when it differs.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, double value, double last)
    {
        writer.WriteBit(value != last);
        if (value != last)
            writer.WriteCompressed(value);
    }

    /// <summary>BitStream::WriteCompressedDelta of a double with no last value.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, double value)
    {
        writer.WriteBit(true);
        writer.WriteCompressed(value);
    }

    /// <summary>BitStream::ReadCompressedDelta of a double.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref double value)
    {
        var r = reader;
        if (!r.TryReadBit(out var changed))
            return false;
        if (changed)
        {
            if (!r.TryReadCompressed(out double read))
                return false;
            value = read;
        }
        reader = r;
        return true;
    }

    /// <summary>BitStream::WriteCompressedDelta(bool, bool) (BitStream.h:1412-1418): writes the bool only.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, bool value, bool last) => writer.WriteBit(value);

    /// <summary>BitStream::WriteCompressedDelta(bool) (BitStream.h:1428-1432): writes the bool only.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, bool value) => writer.WriteBit(value);

    /// <summary>BitStream::ReadCompressedDelta(bool) (BitStream.h:1720-1724): reads the one bit.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref bool value) => reader.TryReadDelta(ref value);

    /// <summary>BitStream::WriteCompressedDelta of a SystemAddress, whose compressed form is the plain one.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, SystemAddress value, SystemAddress last) =>
        writer.WriteDelta(value, last);

    /// <summary>BitStream::WriteCompressedDelta of a SystemAddress with no last value.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, SystemAddress value) => writer.WriteDelta(value);

    /// <summary>BitStream::ReadCompressedDelta of a SystemAddress.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref SystemAddress value) => reader.TryReadDelta(ref value);

    /// <summary>BitStream::WriteCompressedDelta of a RakNetGUID, whose compressed form is the plain one.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, RakNetGuid value, RakNetGuid last) =>
        writer.WriteDelta(value, last);

    /// <summary>BitStream::WriteCompressedDelta of a RakNetGUID with no last value.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, RakNetGuid value) => writer.WriteDelta(value);

    /// <summary>BitStream::ReadCompressedDelta of a RakNetGUID.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref RakNetGuid value) => reader.TryReadDelta(ref value);

    /// <summary>BitStream::WriteCompressedDelta of a uint24_t, whose compressed form is the plain one.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, UInt24 value, UInt24 last) =>
        writer.WriteDelta(value, last);

    /// <summary>BitStream::WriteCompressedDelta of a uint24_t with no last value.</summary>
    public static void WriteCompressedDelta(this ref BitStreamWriter writer, UInt24 value) => writer.WriteDelta(value);

    /// <summary>BitStream::ReadCompressedDelta of a uint24_t.</summary>
    public static bool TryReadCompressedDelta(this ref BitStreamReader reader, ref UInt24 value) => reader.TryReadDelta(ref value);
}
