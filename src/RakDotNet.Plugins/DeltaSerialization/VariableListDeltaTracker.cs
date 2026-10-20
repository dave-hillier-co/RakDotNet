using System.Numerics;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>Writes a variable as BitStream::Write would, for a type without a built-in encoding.</summary>
public delegate void VariableWriter<in T>(BitStream stream, T value);

/// <summary>Reads a variable written by the matching <see cref="VariableWriter{T}"/>; false when the bits run out.</summary>
public delegate bool VariableReader<T>(ref BitStreamReader reader, out T value);

/// <summary>
/// VariableListDeltaTracker: remembers the last serialized value of each of a series of variables, written in the same
/// order every time, and says which changed. Dispose it to return its scratch buffer to the pool.
/// </summary>
/// <remarks>
/// <para>
/// Ported from VariableListDeltaTracker.h. Values are compared by their serialized bytes, as BitStream::Write produces
/// them: a number big-endian, a bool as one bit, other types through a <see cref="VariableWriter{T}"/>. On the wire a
/// variable is a changed bit, then the value when it changed.
/// </para>
/// <para>
/// RakNet's quirk is kept: when a variable's serialized size changes, the dirty flag cleared is the next variable's,
/// not its own (VariableListDeltaTracker.h:41-49). Not thread-safe.
/// </para>
/// </remarks>
public sealed class VariableListDeltaTracker : IDisposable
{
    // The changed bits of at most 448 variables fit the 56 bytes RakNet keeps per send (VariableDeltaSerializer.h:204-209).
    internal const int MaxTrackedVariables = 56 * 8;

    private readonly List<LastValue> _variables = [];
    private readonly BitStream _scratch = new(16);
    private int _nextWriteIndex;

    /// <summary>True once every remembered variable has been written since <see cref="StartWrite"/>.</summary>
    public bool IsPastEndOfList => _nextWriteIndex >= _variables.Count;

    /// <summary>Returns the scratch buffer to the pool.</summary>
    public void Dispose() => _scratch.Dispose();

    /// <summary>StartWrite: call before writing the series again.</summary>
    public void StartWrite() => _nextWriteIndex = 0;

    /// <summary>WriteVar: remembers <paramref name="value"/>; true when it is new, changed or flagged dirty.</summary>
    public bool WriteVar<T>(T value)
        where T : unmanaged, INumber<T>
    {
        Serialize(value);
        return WriteSerialized();
    }

    /// <inheritdoc cref="WriteVar{T}(T)"/>
    public bool WriteVar(bool value)
    {
        Serialize(value);
        return WriteSerialized();
    }

    /// <inheritdoc cref="WriteVar{T}(T)"/>
    public bool WriteVar<T>(T value, VariableWriter<T> write)
    {
        Serialize(value, write);
        return WriteSerialized();
    }

    /// <summary>WriteVarToBitstream: <see cref="WriteVar{T}(T)"/>, then writes the changed bit and, when set, the value.</summary>
    public bool WriteVarToBitstream<T>(T value, BitStream stream)
        where T : unmanaged, INumber<T>
    {
        Serialize(value);
        return WriteSerializedToBitstream(stream);
    }

    /// <inheritdoc cref="WriteVarToBitstream{T}(T, BitStream)"/>
    public bool WriteVarToBitstream(bool value, BitStream stream)
    {
        Serialize(value);
        return WriteSerializedToBitstream(stream);
    }

    /// <inheritdoc cref="WriteVarToBitstream{T}(T, BitStream)"/>
    public bool WriteVarToBitstream<T>(T value, BitStream stream, VariableWriter<T> write)
    {
        Serialize(value, write);
        return WriteSerializedToBitstream(stream);
    }

    /// <summary>
    /// WriteVarToBitstream with a changed-bit array: as <see cref="WriteVarToBitstream{T}(T, BitStream)"/>, also recording
    /// the result at bit <paramref name="writeOffset"/> of <paramref name="changedBits"/>, most significant bit first. The
    /// byte is reset when its first bit is written (VariableListDeltaTracker.h:79-99).
    /// </summary>
    public bool WriteVarToBitstream<T>(T value, BitStream stream, Span<byte> changedBits, int writeOffset)
        where T : unmanaged, INumber<T>
    {
        Serialize(value);
        return RecordChange(WriteSerializedToBitstream(stream), changedBits, writeOffset);
    }

    /// <inheritdoc cref="WriteVarToBitstream{T}(T, BitStream, Span{byte}, int)"/>
    public bool WriteVarToBitstream(bool value, BitStream stream, Span<byte> changedBits, int writeOffset)
    {
        Serialize(value);
        return RecordChange(WriteSerializedToBitstream(stream), changedBits, writeOffset);
    }

    /// <inheritdoc cref="WriteVarToBitstream{T}(T, BitStream, Span{byte}, int)"/>
    public bool WriteVarToBitstream<T>(T value, BitStream stream, Span<byte> changedBits, int writeOffset, VariableWriter<T> write)
    {
        Serialize(value, write);
        return RecordChange(WriteSerializedToBitstream(stream), changedBits, writeOffset);
    }

    /// <summary>
    /// ReadVarFromBitstream: reads the changed bit and, when set, the value into <paramref name="value"/>. True only when
    /// the value was read; an unchanged or truncated variable keeps its value.
    /// </summary>
    public static bool ReadVarFromBitstream<T>(ref T value, ref BitStreamReader reader)
        where T : unmanaged, INumber<T>
    {
        if (!reader.TryReadBit(out var written) || !written)
            return false;
        var serializer = new BitSerializer(reader);
        var read = value;
        if (!serializer.Serialize(ref read))
            return false;
        reader = serializer.Reader;
        value = read;
        return true;
    }

    /// <inheritdoc cref="ReadVarFromBitstream{T}(ref T, ref BitStreamReader)"/>
    public static bool ReadVarFromBitstream(ref bool value, ref BitStreamReader reader)
    {
        if (!reader.TryReadBit(out var written) || !written)
            return false;
        if (!reader.TryReadBit(out var read))
            return false;
        value = read;
        return true;
    }

    /// <inheritdoc cref="ReadVarFromBitstream{T}(ref T, ref BitStreamReader)"/>
    public static bool ReadVarFromBitstream<T>(ref T value, ref BitStreamReader reader, VariableReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!reader.TryReadBit(out var written) || !written)
            return false;
        if (!read(ref reader, out var readValue))
            return false;
        value = readValue;
        return true;
    }

    /// <summary>
    /// FlagDirtyFromBitArray: every remembered variable whose bit is set in <paramref name="changedBits"/>, most
    /// significant bit first, is different at its next write.
    /// </summary>
    public void FlagDirtyFromBitArray(ReadOnlySpan<byte> changedBits)
    {
        for (var i = 0; i < _variables.Count; i++)
        {
            var bit = i >> 3 < changedBits.Length && (changedBits[i >> 3] & (0x80 >> (i & 7))) != 0;
            if (bit)
                _variables[i].IsDirty = true;
        }
    }

    private void Serialize<T>(T value)
        where T : unmanaged, INumber<T>
    {
        _scratch.Reset();
        var writer = _scratch.BeginWrite(16);
        var serializer = new BitSerializer(writer);
        serializer.Serialize(ref value);
        _scratch.EndWrite(serializer.Writer);
    }

    private void Serialize(bool value)
    {
        _scratch.Reset();
        _scratch.WriteBit(value);
    }

    private void Serialize<T>(T value, VariableWriter<T> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        _scratch.Reset();
        write(_scratch, value);
    }

    // WriteVar (VariableListDeltaTracker.h:31-62) on the value in _scratch.
    private bool WriteSerialized()
    {
        var data = _scratch.Written;
        if (_nextWriteIndex >= _variables.Count)
        {
            _variables.Add(new LastValue(data.ToArray()));
            _nextWriteIndex++;
            return true;
        }
        var last = _variables[_nextWriteIndex];
        if (data.Length != last.Data.Length)
        {
            last.Data = data.ToArray();
            _nextWriteIndex++;
            if (_nextWriteIndex < _variables.Count)
                _variables[_nextWriteIndex].IsDirty = false;
            return true;
        }
        if (!last.IsDirty && data.SequenceEqual(last.Data))
        {
            _nextWriteIndex++;
            return false;
        }
        last.IsDirty = false;
        data.CopyTo(last.Data);
        _nextWriteIndex++;
        return true;
    }

    // WriteVarToBitstream (VariableListDeltaTracker.h:64-75).
    private bool WriteSerializedToBitstream(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var different = WriteSerialized();
        stream.WriteBit(different);
        if (different)
            CopyLoadedTo(stream);
        return different;
    }

    // VariableDeltaSerializer serializes a value once with Load, then picks what to do with it.
    internal void Load<T>(T value)
        where T : unmanaged, INumber<T> => Serialize(value);

    internal void Load(bool value) => Serialize(value);

    internal void Load<T>(T value, VariableWriter<T> write) => Serialize(value, write);

    internal bool WriteLoadedToBitstream(BitStream stream) => WriteSerializedToBitstream(stream);

    internal bool WriteLoadedToBitstream(BitStream stream, Span<byte> changedBits, int writeOffset) =>
        RecordChange(WriteSerializedToBitstream(stream), changedBits, writeOffset);

    internal void CopyLoadedTo(BitStream stream)
    {
        var reader = _scratch.CreateReader();
        stream.Write(ref reader);
    }

    private static bool RecordChange(bool different, Span<byte> changedBits, int writeOffset)
    {
        var index = writeOffset >> 3;
        if (different)
        {
            if ((writeOffset & 7) == 0)
                changedBits[index] = 0x80;
            else
                changedBits[index] |= (byte)(0x80 >> (writeOffset & 7));
        }
        else if ((writeOffset & 7) == 0)
        {
            changedBits[index] = 0;
        }
        return different;
    }

    private sealed class LastValue(byte[] data)
    {
        public byte[] Data { get; set; } = data;

        public bool IsDirty { get; set; }
    }
}
