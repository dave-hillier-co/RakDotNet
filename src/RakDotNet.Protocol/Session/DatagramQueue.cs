namespace RakDotNet.Protocol.Session;

/// <summary>
/// Datagrams waiting to be sent, first in first out, packed into one buffer that is reused once the queue empties, so
/// queueing and taking datagrams allocates nothing in steady state.
/// </summary>
/// <remarks>
/// A datagram is written in place: <see cref="Reserve"/> hands out room for it and <see cref="Commit"/> queues the bytes
/// written. A datagram taken by <see cref="TryDequeue"/> stays valid until the next <see cref="Reserve"/>; the buffer
/// is only written over from the start when nothing is queued, and growing it leaves earlier datagrams where they were.
/// </remarks>
/// <typeparam name="TTag">What travels with each datagram, such as its destination.</typeparam>
internal sealed class DatagramQueue<TTag>
{
    private readonly RingQueue<Entry> _entries = new();
    private byte[] _buffer;
    private int _used;
    private int _reserved = -1;

    public DatagramQueue(int initialCapacity = 2048)
    {
        _buffer = new byte[Math.Max(initialCapacity, 1)];
    }

    public int Count => _entries.Count;

    /// <summary>Room for a datagram of up to <paramref name="maxLength"/> bytes, queued by <see cref="Commit"/>.</summary>
    public Span<byte> Reserve(int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLength);
        if (_entries.Count == 0)
            _used = 0;
        if (_buffer.Length - _used < maxLength)
        {
            var buffer = new byte[Math.Max(_buffer.Length * 2, _used + maxLength)];
            _buffer.AsSpan(0, _used).CopyTo(buffer);
            _buffer = buffer;
        }
        _reserved = maxLength;
        return _buffer.AsSpan(_used, maxLength);
    }

    /// <summary>Queues the first <paramref name="length"/> bytes of the room <see cref="Reserve"/> gave.</summary>
    public void Commit(int length, TTag tag)
    {
        if (_reserved < 0)
            throw new InvalidOperationException("Nothing was reserved.");
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, _reserved);
        _entries.Enqueue(new Entry(_used, length, tag));
        _used += length;
        _reserved = -1;
    }

    /// <summary>Copies <paramref name="datagram"/> into the queue.</summary>
    public void Enqueue(ReadOnlySpan<byte> datagram, TTag tag)
    {
        datagram.CopyTo(Reserve(datagram.Length));
        Commit(datagram.Length, tag);
    }

    /// <summary>The oldest datagram, valid until the next <see cref="Reserve"/>.</summary>
    public bool TryDequeue(out ReadOnlySpan<byte> datagram, out TTag tag)
    {
        if (_entries.Count == 0)
        {
            datagram = default;
            tag = default!;
            return false;
        }
        var entry = _entries.Dequeue();
        datagram = _buffer.AsSpan(entry.Offset, entry.Length);
        tag = entry.Tag;
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _used = 0;
        _reserved = -1;
    }

    private readonly record struct Entry(int Offset, int Length, TTag Tag);
}
