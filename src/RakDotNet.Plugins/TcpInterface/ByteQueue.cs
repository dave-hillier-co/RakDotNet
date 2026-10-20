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

    /// <summary>GetBytesWritten: how many bytes are queued (DS_ByteQueue.cpp:113-119).</summary>
    public int BytesWritten => _writeOffset >= _readOffset ? _writeOffset - _readOffset : _writeOffset + (_data.Length - _readOffset);

    /// <summary>WriteBytes (DS_ByteQueue.cpp:30-68).</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        var bytesWritten = BytesWritten;
        var length = data.Length;
        // One byte always stays free, so a full queue is never mistaken for an empty one.
        if (_data.Length == 0 || length > _data.Length - bytesWritten - 1)
        {
            var oldLength = _data.Length;
            var grow = Math.Max(length + oldLength + 1, MinimumAllocation);
            Array.Resize(ref _data, oldLength + grow);
            if (_writeOffset < _readOffset)
            {
                // The wrapped head moves after the old end. DS_ByteQueue.cpp:50-55 has a second branch for a head longer
                // than the growth, which cannot happen: the growth always exceeds the old length.
                _data.AsSpan(0, _writeOffset).CopyTo(_data.AsSpan(oldLength));
                _writeOffset = _readOffset + bytesWritten;
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
