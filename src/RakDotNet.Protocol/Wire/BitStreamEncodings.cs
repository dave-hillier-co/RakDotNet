using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// The RakNet BitStream encodings used by application and plugin payloads (BitStream.h), as extensions on
/// <see cref="BitStreamWriter"/> and <see cref="BitStreamReader"/> so they work for stack buffers and
/// <see cref="BitStream"/> alike. Each encoding is checked against native output with golden vectors.
/// </summary>
public static class BitStreamEncodings
{
    /// <summary>BitStream::WriteCompressed for an integer: leading zero (or 0xFF) bytes become single bits.</summary>
    public static void WriteCompressed<T>(this ref BitStreamWriter writer, T value)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::ReadCompressed for an integer.</summary>
    public static bool TryReadCompressed<T>(this ref BitStreamReader reader, out T value)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::WriteBitsFromIntegerRange: only the bits needed for <c>maximum - minimum</c>.</summary>
    public static void WriteBitsFromIntegerRange<T>(this ref BitStreamWriter writer, T value, T minimum, T maximum, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::ReadBitsFromIntegerRange; the range and flag must match the writer's.</summary>
    public static bool TryReadBitsFromIntegerRange<T>(this ref BitStreamReader reader, T minimum, T maximum, out T value, bool allowOutsideRange = false)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::WriteFloat16: a float in 2 bytes spanning <paramref name="minimum"/> to <paramref name="maximum"/>.</summary>
    public static void WriteFloat16(this ref BitStreamWriter writer, float value, float minimum, float maximum) =>
        throw new NotImplementedException();

    /// <summary>BitStream::ReadFloat16.</summary>
    public static bool TryReadFloat16(this ref BitStreamReader reader, float minimum, float maximum, out float value) =>
        throw new NotImplementedException();

    /// <summary>BitStream::WriteNormVector: a normalised vector in at most 4 bytes and 3 bits.</summary>
    public static void WriteNormVector(this ref BitStreamWriter writer, Vector3 value) => throw new NotImplementedException();

    /// <summary>BitStream::ReadNormVector.</summary>
    public static bool TryReadNormVector(this ref BitStreamReader reader, out Vector3 value) => throw new NotImplementedException();

    /// <summary>BitStream::WriteVector: magnitude then a normalised direction, 10 bytes.</summary>
    public static void WriteVector(this ref BitStreamWriter writer, Vector3 value) => throw new NotImplementedException();

    /// <summary>BitStream::ReadVector.</summary>
    public static bool TryReadVector(this ref BitStreamReader reader, out Vector3 value) => throw new NotImplementedException();

    /// <summary>BitStream::WriteNormQuat: a normalised quaternion in 6 bytes and 4 bits.</summary>
    public static void WriteNormQuat(this ref BitStreamWriter writer, Quaternion value) => throw new NotImplementedException();

    /// <summary>BitStream::ReadNormQuat.</summary>
    public static bool TryReadNormQuat(this ref BitStreamReader reader, out Quaternion value) => throw new NotImplementedException();

    /// <summary>
    /// BitStream::WriteOrthMatrix: the upper 3x3 of <paramref name="value"/> (M11 to M33) as RakNet's m00 to m22,
    /// written as a quaternion.
    /// </summary>
    public static void WriteOrthMatrix(this ref BitStreamWriter writer, in Matrix4x4 value) => throw new NotImplementedException();

    /// <summary>BitStream::ReadOrthMatrix, into the upper 3x3 of an identity matrix.</summary>
    public static bool TryReadOrthMatrix(this ref BitStreamReader reader, out Matrix4x4 value) => throw new NotImplementedException();

    /// <summary>BitStream::WriteDelta: one bit if <paramref name="value"/> equals <paramref name="last"/>, else a bit then the value.</summary>
    public static void WriteDelta<T>(this ref BitStreamWriter writer, T value, T last)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::ReadDelta: leaves <paramref name="value"/> unchanged when the sender wrote the same value.</summary>
    public static bool TryReadDelta<T>(this ref BitStreamReader reader, ref T value)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();

    /// <summary>BitStream::WriteCasted: <paramref name="value"/> written as the smaller <typeparamref name="TWire"/>.</summary>
    public static void WriteCasted<TWire, TValue>(this ref BitStreamWriter writer, TValue value)
        where TWire : unmanaged, IBinaryInteger<TWire>
        where TValue : unmanaged, IBinaryInteger<TValue> => throw new NotImplementedException();

    /// <summary>BitStream::ReadCasted.</summary>
    public static bool TryReadCasted<TWire, TValue>(this ref BitStreamReader reader, out TValue value)
        where TWire : unmanaged, IBinaryInteger<TWire>
        where TValue : unmanaged, IBinaryInteger<TValue> => throw new NotImplementedException();

    /// <summary>RakString::Serialize: a u16 length then the bytes.</summary>
    public static void WriteRakString(this ref BitStreamWriter writer, ReadOnlySpan<char> value) => throw new NotImplementedException();

    /// <summary>RakString::Deserialize.</summary>
    public static bool TryReadRakString(this ref BitStreamReader reader, [NotNullWhen(true)] out string? value) =>
        throw new NotImplementedException();

    /// <summary>StringCompressor::EncodeString: Huffman-coded with RakNet's English frequency table.</summary>
    public static void WriteCompressedString(this ref BitStreamWriter writer, ReadOnlySpan<char> value, int maxCharCount) =>
        throw new NotImplementedException();

    /// <summary>StringCompressor::DecodeString.</summary>
    public static bool TryReadCompressedString(this ref BitStreamReader reader, int maxCharCount, [NotNullWhen(true)] out string? value) =>
        throw new NotImplementedException();
}

/// <summary>
/// RakNet's <c>Serialize(bool writeToBitstream, ...)</c> idiom: one method body that either writes or reads.
/// </summary>
/// <remarks>
/// A ref struct cannot hold a <c>ref</c> to another ref struct, so the serializer owns its writer or reader by value;
/// <see cref="Writer"/> and <see cref="Reader"/> return the advanced copy.
/// </remarks>
public ref struct BitSerializer
{
    public BitSerializer(BitStreamWriter writer) => throw new NotImplementedException();

    public BitSerializer(BitStreamReader reader) => throw new NotImplementedException();

    public readonly bool IsWriting => throw new NotImplementedException();

    /// <summary>The writer after the calls so far. Throws when reading.</summary>
    public readonly BitStreamWriter Writer => throw new NotImplementedException();

    /// <summary>The reader after the calls so far. Throws when writing.</summary>
    public readonly BitStreamReader Reader => throw new NotImplementedException();

    /// <summary>Writes <paramref name="value"/>, or reads into it; false when a read fails.</summary>
    public bool Serialize<T>(ref T value)
        where T : unmanaged, INumber<T> => throw new NotImplementedException();

    public bool Serialize(ref bool value) => throw new NotImplementedException();

    public bool SerializeCompressed<T>(ref T value)
        where T : unmanaged, IBinaryInteger<T> => throw new NotImplementedException();
}
