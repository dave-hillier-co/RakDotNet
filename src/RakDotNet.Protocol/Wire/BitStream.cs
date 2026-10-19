using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// A growable, poolable BitStream for application and plugin payloads, backed by <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
/// <remarks>
/// Every <c>Write*</c> member forwards to a <see cref="BitStreamWriter"/> created at the current bit position with
/// <see cref="BeginWrite"/> and committed with <see cref="EndWrite"/>; the class never packs bits itself, as
/// docs/conventions.md requires. A <see cref="BitStreamWriter"/> is a ref struct and cannot be held by a class, which
/// is why the stream keeps a bit position and recreates the writer with the resume constructor.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "RakNet's BitStream name, kept so ported code reads the same; it is not a System.IO.Stream.")]
public sealed class BitStream : IBufferWriter<byte>, IDisposable
{
    public const int DefaultInitialCapacityBytes = 256;

    public BitStream(int initialCapacityBytes = DefaultInitialCapacityBytes) => throw new NotImplementedException();

    /// <summary>A stream from a shared pool; <see cref="Dispose"/> returns it.</summary>
    public static BitStream Rent(int initialCapacityBytes = DefaultInitialCapacityBytes) => throw new NotImplementedException();

    public int BitsWritten => throw new NotImplementedException();

    public int BytesWritten => throw new NotImplementedException();

    public ReadOnlySpan<byte> Written => throw new NotImplementedException();

    /// <summary>Empties the stream and keeps its buffer.</summary>
    public void Reset() => throw new NotImplementedException();

    /// <summary>A writer at the current bit position over a buffer with at least <paramref name="sizeHintBytes"/> free.</summary>
    public BitStreamWriter BeginWrite(int sizeHintBytes) => throw new NotImplementedException();

    /// <summary>Commits what <paramref name="writer"/>, from <see cref="BeginWrite"/>, wrote.</summary>
    public void EndWrite(in BitStreamWriter writer) => throw new NotImplementedException();

    public void Write<T>(in T message)
        where T : IWireMessage<T> => throw new NotImplementedException();

    public void WriteBit(bool value) => throw new NotImplementedException();

    public void WriteBits(byte value, int count) => throw new NotImplementedException();

    public void WriteBits(ReadOnlySpan<byte> source, int bitCount) => throw new NotImplementedException();

    public void WriteBytes(ReadOnlySpan<byte> source) => throw new NotImplementedException();

    public void WriteAlignedBytes(ReadOnlySpan<byte> source) => throw new NotImplementedException();

    public void AlignToByte() => throw new NotImplementedException();

    public void WriteByte(byte value) => throw new NotImplementedException();

    public void WriteBool(bool value) => throw new NotImplementedException();

    public void WriteUInt16(ushort value) => throw new NotImplementedException();

    public void WriteInt16(short value) => throw new NotImplementedException();

    public void WriteUInt32(uint value) => throw new NotImplementedException();

    public void WriteInt32(int value) => throw new NotImplementedException();

    public void WriteUInt64(ulong value) => throw new NotImplementedException();

    public void WriteInt64(long value) => throw new NotImplementedException();

    public void WriteSingle(float value) => throw new NotImplementedException();

    public void WriteDouble(double value) => throw new NotImplementedException();

    public void WriteUInt24(UInt24 value) => throw new NotImplementedException();

    public void WriteAddress(IPEndPoint endPoint) => throw new NotImplementedException();

    public void WriteAddress(SystemAddress address) => throw new NotImplementedException();

    /// <summary>A reader over <see cref="Written"/>, limited to <see cref="BitsWritten"/>.</summary>
    public BitStreamReader CreateReader() => throw new NotImplementedException();

    /// <summary>Byte-aligned <see cref="IBufferWriter{T}"/> access; aligns the stream first.</summary>
    public void Advance(int count) => throw new NotImplementedException();

    /// <inheritdoc cref="Advance"/>
    public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotImplementedException();

    /// <inheritdoc cref="Advance"/>
    public Span<byte> GetSpan(int sizeHint = 0) => throw new NotImplementedException();

    /// <summary>Returns the buffer to the pool, and the stream itself if it was rented.</summary>
    public void Dispose() => throw new NotImplementedException();
}
