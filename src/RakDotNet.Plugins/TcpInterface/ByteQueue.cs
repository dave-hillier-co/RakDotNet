namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's DataStructures::ByteQueue: a growing ring of bytes. <see cref="TcpInterface"/> buffers each connection's
/// outgoing data in one and <see cref="PacketizedTcp"/> each connection's incoming stream.
/// </summary>
/// <remarks>Not thread-safe; its users guard it as RakNet does.</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "RakNet's DataStructures::ByteQueue")]
public sealed class ByteQueue
{
    private const int MinimumAllocation = 256;

    private byte[] _data = [];
    private int _readOffset;
    private int _writeOffset;

    /// <summary>A queue that holds at most <paramref name="maximumLength"/> bytes; by default as many as an array can.</summary>
    public ByteQueue(int maximumLength = MaximumArrayLength - 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumLength, MaximumArrayLength - 1);
        MaximumLength = maximumLength;
    }

    // Array.MaxLength, which is not a constant.
    private const int MaximumArrayLength = 0x7FFFFFC7;

    /// <summary>
    /// The most bytes the queue holds. RakNet grows without a bound, and its growth overflows past about 1 GiB; here the
    /// growth is capped at this length and the free byte.
    /// </summary>
    public int MaximumLength { get; }

    /// <summary>GetBytesWritten: how many bytes are queued (DS_ByteQueue.cpp:113-119).</summary>
    public int BytesWritten => _writeOffset >= _readOffset ? _writeOffset - _readOffset : _writeOffset + (_data.Length - _readOffset);

    /// <summary>WriteBytes (DS_ByteQueue.cpp:30-68).</summary>
    /// <exception cref="InvalidOperationException">
    /// The queue would hold more than <see cref="MaximumLength"/> bytes; nothing is written.
    /// </exception>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        var bytesWritten = BytesWritten;
        var length = data.Length;
        if ((long)bytesWritten + length > MaximumLength)
            throw new InvalidOperationException($"The queue holds at most {MaximumLength} bytes; {bytesWritten} are queued and {length} more were written.");
        // One byte always stays free, so a full queue is never mistaken for an empty one.
        if (_data.Length == 0 || length > _data.Length - bytesWritten - 1)
        {
            var oldLength = _data.Length;
            // DS_ByteQueue.cpp:41-44 grows by the old length, the new bytes and one, in long arithmetic here so a large
            // queue cannot overflow, and capped at the maximum and the free byte.
            var grown = oldLength + Math.Max((long)length + oldLength + 1, MinimumAllocation);
            Array.Resize(ref _data, (int)Math.Min(grown, (long)MaximumLength + 1));
            var growth = _data.Length - oldLength;
            if (_writeOffset < _readOffset)
            {
                if (_writeOffset <= growth)
                {
                    // The wrapped head moves after the old end (DS_ByteQueue.cpp:45-49).
                    _data.AsSpan(0, _writeOffset).CopyTo(_data.AsSpan(oldLength));
                    _writeOffset = _readOffset + bytesWritten;
                }
                else
                {
                    // A growth capped at the maximum can be shorter than the head: as much of the head as fits moves
                    // after the old end and the rest to the start (DS_ByteQueue.cpp:50-55).
                    _data.AsSpan(0, growth).CopyTo(_data.AsSpan(oldLength));
                    _data.AsSpan(growth, _writeOffset - growth).CopyTo(_data);
                    _writeOffset -= growth;
                }
            }
        }

        var untilEnd = _data.Length - _writeOffset;
        if (length <= untilEnd)
        {
            data.CopyTo(_data.AsSpan(_writeOffset));
        }
        else
        {
            data[..untilEnd].CopyTo(_data.AsSpan(_writeOffset));
            data[untilEnd..].CopyTo(_data);
        }
        _writeOffset = (_writeOffset + length) % _data.Length;
    }

    /// <summary>
    /// ReadBytes: copies the first min(<see cref="BytesWritten"/>, destination length) bytes, removing them unless
    /// <paramref name="peek"/>. False when nothing was copied (DS_ByteQueue.cpp:69-97).
    /// </summary>
    public bool ReadBytes(Span<byte> destination, bool peek)
    {
        var count = Math.Min(BytesWritten, destination.Length);
        if (count == 0)
            return false;
        var untilEnd = _data.Length - _readOffset;
        if (_writeOffset >= _readOffset || count <= untilEnd)
        {
            _data.AsSpan(_readOffset, count).CopyTo(destination);
        }
        else
        {
            _data.AsSpan(_readOffset, untilEnd).CopyTo(destination);
            _data.AsSpan(0, count - untilEnd).CopyTo(destination[untilEnd..]);
        }
        if (!peek)
            IncrementReadOffset(count);
        return true;
    }

    /// <summary>PeekContiguousBytes: the queued bytes up to the end of the buffer (DS_ByteQueue.cpp:98-105).</summary>
    public ReadOnlySpan<byte> PeekContiguousBytes() =>
        _writeOffset >= _readOffset
            ? _data.AsSpan(_readOffset, _writeOffset - _readOffset)
            : _data.AsSpan(_readOffset, _data.Length - _readOffset);

    /// <summary>IncrementReadOffset: drops <paramref name="length"/> queued bytes (DS_ByteQueue.cpp:120-123).</summary>
    public void IncrementReadOffset(int length)
    {
        if (_data.Length > 0)
            _readOffset = (_readOffset + length) % _data.Length;
    }

    /// <summary>DecrementReadOffset: queues the last <paramref name="length"/> bytes read again (DS_ByteQueue.cpp:124-130).</summary>
    public void DecrementReadOffset(int length)
    {
        if (length > _readOffset)
            _readOffset = _data.Length - (length - _readOffset);
        else
            _readOffset -= length;
    }

    /// <summary>Clear: frees the buffer (DS_ByteQueue.cpp:106-112).</summary>
    public void Clear()
    {
        _data = [];
        _readOffset = 0;
        _writeOffset = 0;
    }
}
