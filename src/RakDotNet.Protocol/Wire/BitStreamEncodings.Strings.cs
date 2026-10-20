using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RakDotNet.Protocol.Wire;

// RakString, RakWString and StringCompressor wire forms (RakString.cpp:1214-1285, RakWString.cpp:285-385,
// StringCompressor.cpp:366-427). A RakString is a C string of bytes: it ends at its first NUL and need not be UTF-8, so
// the byte forms carry it unchanged and the string forms are UTF-8 conveniences over them.
public static partial class BitStreamEncodings
{
    /// <summary>RakString::SerializeCompressed and DeserializeCompressed pass this limit (RakString.cpp:1232, 1275).</summary>
    private const int RakStringCompressedLimit = 0xFFFF;

    /// <summary>
    /// RakString::Serialize (RakString.cpp:1218-1223): the bytes before the first NUL, as a u16 length then the bytes
    /// aligned to a byte.
    /// </summary>
    /// <exception cref="ArgumentException">The string is more than 65535 bytes.</exception>
    public static void WriteRakString(this ref BitStreamWriter writer, scoped ReadOnlySpan<byte> value)
    {
        value = StringCompressor.ToCString(value);
        if (value.Length > ushort.MaxValue)
            throw new ArgumentException("A RakString holds at most 65535 bytes.", nameof(value));
        writer.WriteUInt16((ushort)value.Length);
        writer.WriteAlignedBytes(value);
    }

    /// <summary>RakString::Serialize of <paramref name="value"/> encoded as UTF-8.</summary>
    /// <exception cref="ArgumentException">The string is more than 65535 bytes of UTF-8.</exception>
    public static void WriteRakString(this ref BitStreamWriter writer, scoped ReadOnlySpan<char> value)
    {
        var rented = RentUtf8(value, out var bytes);
        try
        {
            writer.WriteRakString(bytes);
        }
        finally
        {
            Return(rented);
        }
    }

    /// <summary>
    /// RakString::Deserialize (RakString.cpp:1234-1253): the bytes as a slice of the input, up to the first NUL, as the
    /// RakString's length is strlen (RakString.cpp:385-388); every byte of the length is consumed. An empty string still
    /// aligns the read.
    /// </summary>
    public static bool TryReadRakStringBytes(this ref BitStreamReader reader, out ReadOnlySpan<byte> value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadUInt16(out var length))
            return false;
        if (length == 0)
            r.AlignToByte();
        else if (!r.TryReadAlignedBytes(length, out value))
            return false;
        value = StringCompressor.ToCString(value);
        reader = r;
        return true;
    }

    /// <summary>RakString::Deserialize, decoding the bytes as UTF-8.</summary>
    public static bool TryReadRakString(this ref BitStreamReader reader, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (!reader.TryReadRakStringBytes(out var bytes))
            return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// StringCompressor::EncodeString with <see cref="StringCompressor.Instance"/> (StringCompressor.cpp:366-397): at most
    /// <paramref name="maxCharCount"/> - 1 bytes before the first NUL, or all of them when the limit is 0 or more than
    /// their count; nothing for a language without a tree.
    /// </summary>
    public static void WriteCompressedString(this ref BitStreamWriter writer, scoped ReadOnlySpan<byte> value, int maxCharCount, byte languageId = 0) =>
        StringCompressor.Instance.EncodeString(ref writer, value, maxCharCount, languageId);

    /// <summary>StringCompressor::EncodeString of <paramref name="value"/> encoded as UTF-8.</summary>
    public static void WriteCompressedString(this ref BitStreamWriter writer, scoped ReadOnlySpan<char> value, int maxCharCount, byte languageId = 0)
    {
        var rented = RentUtf8(value, out var bytes);
        try
        {
            writer.WriteCompressedString(bytes, maxCharCount, languageId);
        }
        finally
        {
            Return(rented);
        }
    }

    /// <summary>
    /// StringCompressor::DecodeString into a RakString (StringCompressor.cpp:475-503): a limit that is not positive
    /// reads nothing and gives an empty string; otherwise false for a language without a tree or a truncated stream. A
    /// string of <paramref name="maxCharCount"/> bytes or more keeps its first <paramref name="maxCharCount"/> - 1, and a
    /// decoded NUL ends it.
    /// </summary>
    public static bool TryReadCompressedStringBytes(this ref BitStreamReader reader, int maxCharCount, out byte[] value, byte languageId = 0)
    {
        value = [];
        if (maxCharCount <= 0)
            return true;
        if (!StringCompressor.Instance.TryDecodeString(ref reader, maxCharCount, out var decoded, languageId))
            return false;
        var text = StringCompressor.ToCString(decoded);
        value = text.Length == decoded.Length ? decoded : text.ToArray();
        return true;
    }

    /// <summary>StringCompressor::DecodeString into a RakString, decoding the bytes as UTF-8.</summary>
    public static bool TryReadCompressedString(this ref BitStreamReader reader, int maxCharCount, [NotNullWhen(true)] out string? value, byte languageId = 0)
    {
        value = null;
        if (!reader.TryReadCompressedStringBytes(maxCharCount, out var bytes, languageId))
            return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// RakString::SerializeCompressed (RakString.cpp:1228-1232): with <paramref name="writeLanguageId"/>, the language id
    /// as a compressed u8 first, then the string compressed with that language and a limit of 65535.
    /// </summary>
    public static void WriteCompressedRakString(this ref BitStreamWriter writer, scoped ReadOnlySpan<byte> value, byte languageId = 0, bool writeLanguageId = false)
    {
        if (writeLanguageId)
            writer.WriteCompressed(languageId);
        writer.WriteCompressedString(value, RakStringCompressedLimit, languageId);
    }

    /// <summary>RakString::SerializeCompressed of <paramref name="value"/> encoded as UTF-8.</summary>
    public static void WriteCompressedRakString(this ref BitStreamWriter writer, scoped ReadOnlySpan<char> value, byte languageId = 0, bool writeLanguageId = false)
    {
        if (writeLanguageId)
            writer.WriteCompressed(languageId);
        writer.WriteCompressedString(value, RakStringCompressedLimit, languageId);
    }

    /// <summary>
    /// RakString::DeserializeCompressed (RakString.cpp:1268-1276): with <paramref name="readLanguageId"/>, a compressed u8
    /// language id first, otherwise language 0, then the compressed string.
    /// </summary>
    public static bool TryReadCompressedRakStringBytes(this ref BitStreamReader reader, out byte[] value, bool readLanguageId = false)
    {
        value = [];
        var r = reader;
        byte languageId = 0;
        if (readLanguageId && !r.TryReadCompressed(out languageId))
            return false;
        if (!r.TryReadCompressedStringBytes(RakStringCompressedLimit, out value, languageId))
            return false;
        reader = r;
        return true;
    }

    /// <summary>RakString::DeserializeCompressed, decoding the bytes as UTF-8.</summary>
    public static bool TryReadCompressedRakString(this ref BitStreamReader reader, [NotNullWhen(true)] out string? value, bool readLanguageId = false)
    {
        value = null;
        if (!reader.TryReadCompressedRakStringBytes(out var bytes, readLanguageId))
            return false;
        value = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>
    /// RakWString::Serialize (RakWString.cpp:289-310): the UTF-16 code units before the first NUL, as a u16 count then
    /// each unit as a big-endian u16, not aligned. BitStream::WriteCompressed of a RakWString is the same
    /// (BitStream.h:1359-1362). RakNet writes each wchar_t truncated to 16 bits, which is these units for a Windows
    /// build and for any character in the Basic Multilingual Plane on others.
    /// </summary>
    /// <exception cref="ArgumentException">The string is more than 65535 units.</exception>
    public static void WriteRakWString(this ref BitStreamWriter writer, scoped ReadOnlySpan<char> value)
    {
        var end = value.IndexOf('\0');
        if (end >= 0)
            value = value[..end];
        if (value.Length > ushort.MaxValue)
            throw new ArgumentException("A RakWString holds at most 65535 characters.", nameof(value));
        writer.WriteUInt16((ushort)value.Length);
        foreach (var c in value)
            writer.WriteUInt16(c);
    }

    /// <summary>
    /// RakWString::Deserialize (RakWString.cpp:311-350): a u16 count then that many big-endian u16 units. False, without
    /// moving, when truncated, where RakNet keeps what it read and returns true.
    /// </summary>
    public static bool TryReadRakWString(this ref BitStreamReader reader, [NotNullWhen(true)] out string? value)
    {
        value = null;
        var r = reader;
        if (!r.TryReadUInt16(out var count) || r.BitsRemaining < count * 16)
            return false;
        value = string.Create(count, r, static (chars, source) =>
        {
            for (var i = 0; i < chars.Length; i++)
            {
                source.TryReadUInt16(out var unit);
                chars[i] = (char)unit;
            }
        });
        r.IgnoreBits(count * 16);
        reader = r;
        return true;
    }

    private static byte[] RentUtf8(scoped ReadOnlySpan<char> value, out Span<byte> bytes)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, Encoding.UTF8.GetMaxByteCount(value.Length)));
        var length = Encoding.UTF8.GetBytes(value, rented);
        bytes = rented.AsSpan(0, length);
        return rented;
    }

    private static void Return(byte[] rented) => ArrayPool<byte>.Shared.Return(rented);
}
