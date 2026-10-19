using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// A growable BitStream for application and plugin payloads, with its buffer from <see cref="ArrayPool{T}.Shared"/>.
/// </summary>
/// <remarks>
/// Every <c>Write*</c> member forwards to a <see cref="BitStreamWriter"/> created at the current bit position with
/// <see cref="BeginWrite"/> and committed with <see cref="EndWrite"/>; the class never packs bits itself, as
/// docs/conventions.md requires. A <see cref="BitStreamWriter"/> is a ref struct and cannot be held by a class, which
/// is why the stream keeps a bit position and recreates the writer with the resume constructor. Not thread-safe.
/// </remarks>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "RakNet's BitStream name, kept so ported code reads the same; it is not a System.IO.Stream.")]
public sealed class BitStream : IBufferWriter<byte>, IDisposable
{
    public const int DefaultInitialCapacityBytes = 256;

    // Write<T> stops growing here, so a codec that throws InvalidOperationException for another reason still surfaces.
    private const int MaxGrowthHintBytes = 1 << 26;

    private byte[]? _buffer;
    private int _bitPosition;

    public BitStream(int initialCapacityBytes = DefaultInitialCapacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacityBytes);
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialCapacityBytes));
    }

    /// <summary>
    /// A stream whose buffer comes from <see cref="ArrayPool{T}.Shared"/>; <see cref="Dispose"/> returns the buffer.
    /// </summary>
    /// <remarks>
    /// Only the buffer is pooled, never the stream object. A pooled object would have no owner token, so a stale
    /// second <see cref="Dispose"/> after another caller rented it would return that caller's buffer and pool the
    /// stream twice. To build payloads without allocating, keep a stream and <see cref="Reset"/> it.
    /// </remarks>
    public static BitStream Rent(int initialCapacityBytes = DefaultInitialCapacityBytes) => new(initialCapacityBytes);

    public int BitsWritten
    {
        get
        {
            ThrowIfDisposed();
            return _bitPosition;
        }
    }

    public int BytesWritten => (BitsWritten + 7) >> 3;

    public ReadOnlySpan<byte> Written => Buffer.AsSpan(0, BytesWritten);

    /// <summary>Empties the stream and keeps its buffer.</summary>
    public void Reset()
    {
        ThrowIfDisposed();
        _bitPosition = 0;
    }

    /// <summary>A writer at the current bit position over a buffer with at least <paramref name="sizeHintBytes"/> free.</summary>
    public BitStreamWriter BeginWrite(int sizeHintBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHintBytes);
        Reserve(sizeHintBytes);
        return new BitStreamWriter(_buffer, _bitPosition);
    }

    /// <summary>Commits what <paramref name="writer"/>, from <see cref="BeginWrite"/>, wrote.</summary>
    /// <exception cref="ArgumentException">The writer is not over this stream's buffer, or is behind its position.</exception>
    public void EndWrite(in BitStreamWriter writer)
    {
        var buffer = Buffer;
        if (!Unsafe.AreSame(ref MemoryMarshal.GetReference(writer.Written), ref MemoryMarshal.GetArrayDataReference(buffer)))
            throw new ArgumentException("The writer was not created by this stream's BeginWrite, or the stream has grown since.", nameof(writer));
        if (writer.BitsWritten < _bitPosition)
            throw new ArgumentException("The writer is behind the stream's position.", nameof(writer));
        _bitPosition = writer.BitsWritten;
    }

    /// <summary>Writes a wire structure, growing the buffer until it fits.</summary>
    public void Write<T>(in T message)
        where T : IWireMessage<T>
    {
        var hint = Math.Max(64, Buffer.Length - BytesWritten);
        while (true)
        {
            var writer = BeginWrite(hint);
            try
            {
                message.Write(ref writer);
            }
            catch (InvalidOperationException) when (hint < MaxGrowthHintBytes)
            {
                // The writer ran out of buffer: clear the bits it ORed past the position into the current byte, grow
                // and write again.
                if ((_bitPosition & 7) != 0)
                    _buffer![_bitPosition >> 3] &= (byte)(0xFF << (8 - (_bitPosition & 7)));
                hint = checked(hint * 2);
                continue;
            }
            EndWrite(writer);
            return;
        }
    }

    public void WriteBit(bool value)
    {
        var writer = BeginWrite(1);
        writer.WriteBit(value);
        EndWrite(writer);
    }

    public void WriteBits(byte value, int count)
    {
        var writer = BeginWrite(2);
        writer.WriteBits(value, count);
        EndWrite(writer);
    }

    public void WriteBits(ReadOnlySpan<byte> source, int bitCount)
    {
        var writer = BeginWrite(source.Length + 1);
        writer.WriteBits(source, bitCount);
        EndWrite(writer);
    }

    public void WriteBytes(ReadOnlySpan<byte> source)
    {
        var writer = BeginWrite(source.Length + 1);
        writer.WriteBytes(source);
        EndWrite(writer);
    }

    public void WriteAlignedBytes(ReadOnlySpan<byte> source)
    {
        var writer = BeginWrite(source.Length + 1);
        writer.WriteAlignedBytes(source);
        EndWrite(writer);
    }

    public void AlignToByte()
    {
        var writer = BeginWrite(1);
        writer.AlignToByte();
        EndWrite(writer);
    }

    public void WriteByte(byte value)
    {
        var writer = BeginWrite(2);
        writer.WriteByte(value);
        EndWrite(writer);
    }

    public void WriteBool(bool value) => WriteBit(value);

    public void WriteUInt16(ushort value)
    {
        var writer = BeginWrite(3);
        writer.WriteUInt16(value);
        EndWrite(writer);
    }

    public void WriteInt16(short value) => WriteUInt16(unchecked((ushort)value));

    public void WriteUInt32(uint value)
    {
        var writer = BeginWrite(5);
        writer.WriteUInt32(value);
        EndWrite(writer);
    }

    public void WriteInt32(int value) => WriteUInt32(unchecked((uint)value));

    public void WriteUInt64(ulong value)
    {
        var writer = BeginWrite(9);
        writer.WriteUInt64(value);
        EndWrite(writer);
    }

    public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

    public void WriteSingle(float value) => WriteUInt32(BitConverter.SingleToUInt32Bits(value));

    public void WriteDouble(double value) => WriteUInt64(BitConverter.DoubleToUInt64Bits(value));

    public void WriteUInt24(UInt24 value)
    {
        var writer = BeginWrite(4);
        writer.WriteUInt24(value);
        EndWrite(writer);
    }

    public void WriteAddress(IPEndPoint endPoint)
    {
        var writer = BeginWrite(8);
        writer.WriteAddress(endPoint);
        EndWrite(writer);
    }

    public void WriteAddress(SystemAddress address)
    {
        var writer = BeginWrite(8);
        writer.WriteAddress(address);
        EndWrite(writer);
    }

    /// <inheritdoc cref="BitStreamWriter.WriteRakNetGuid"/>
    public void WriteRakNetGuid(RakNetGuid value) => WriteUInt64(value.Value);

    /// <summary>A reader over <see cref="Written"/>, limited to <see cref="BitsWritten"/>.</summary>
    public BitStreamReader CreateReader() => new(Written, _bitPosition);

    /// <summary>Byte-aligned <see cref="IBufferWriter{T}"/> access; aligns the stream first.</summary>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var start = AlignedBytes();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Buffer.Length - start);
        _bitPosition = (start + count) * 8;
    }

    /// <inheritdoc cref="Advance"/>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        AlignToByte();
        Reserve(Math.Max(1, sizeHint));
        return _buffer.AsMemory(BytesWritten);
    }

    /// <inheritdoc cref="Advance"/>
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;

    /// <summary>Returns the buffer to the pool. Disposing again does nothing.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        if (buffer is null)
            return;
        _buffer = null;
        _bitPosition = 0;
        ArrayPool<byte>.Shared.Return(buffer);
    }

    private byte[] Buffer
    {
        get
        {
            ThrowIfDisposed();
            return _buffer!;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_buffer is null)
            ThrowDisposed();
    }

    [DoesNotReturn]
    private void ThrowDisposed() => throw new ObjectDisposedException(nameof(BitStream));

    private int AlignedBytes()
    {
        ThrowIfDisposed();
        return (_bitPosition + 7) >> 3;
    }

    // Makes room for sizeHintBytes after the byte the position is in, growing into a larger pooled buffer.
    private void Reserve(int sizeHintBytes)
    {
        var buffer = Buffer;
        var needed = checked((_bitPosition >> 3) + sizeHintBytes + 1);
        if (needed <= buffer.Length)
            return;
        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(needed, checked(buffer.Length * 2)));
        buffer.AsSpan(0, AlignedBytes()).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer);
        _buffer = grown;
    }
}
