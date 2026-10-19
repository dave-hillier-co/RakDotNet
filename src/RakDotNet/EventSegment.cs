using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// The pooled storage behind one entry of the event stream: RakNet's Packet plus what the typed members of
/// <see cref="PeerEvent"/> report. Views carry the <see cref="Generation"/> they were made for.
/// </summary>
/// <remarks>
/// Release is a compare-exchange from a view's generation to the next, so only one holder can release a segment and
/// every older view stops working at once.
/// </remarks>
internal sealed class EventSegment(EventPool pool)
{
    private byte[] _buffer = [];

    public int Generation;

    public EventPool Pool { get; } = pool;

    public bool IsOwned { get; set; }

    public PeerEventKind Kind { get; set; }

    public int Length { get; private set; }

    public int BitLength { get; set; }

    public RakConnection? Connection { get; set; }

    public RakNetGuid RakNetGuid { get; set; }

    public SystemAddress Address { get; set; }

    public int SocketIndex { get; set; }

    public ulong ReceivedTime { get; set; }

    public bool IsLocal { get; set; }

    public bool IsIncoming { get; set; }

    public DisconnectReason DisconnectReason { get; set; }

    public bool ClosedLocally { get; set; }

    public ConnectFailure ConnectFailure { get; set; }

    public byte RemoteProtocolVersion { get; set; }

    public SendReceipt Receipt { get; set; }

    public ReadOnlySpan<byte> Data => _buffer.AsSpan(0, Length);

    public ReadOnlyMemory<byte> Memory => _buffer.AsMemory(0, Length);

    /// <summary><see cref="Data"/>, for the rewrites RakPeer::Receive makes in place, such as the ID_TIMESTAMP shift.</summary>
    public Span<byte> WritableData => _buffer.AsSpan(0, Length);

    /// <summary>Room for <paramref name="length"/> bytes, which become <see cref="Data"/>.</summary>
    public Span<byte> SetLength(int length)
    {
        if (_buffer.Length < length)
            _buffer = new byte[Math.Max(length, 64)];
        Length = length;
        BitLength = length * 8;
        return _buffer.AsSpan(0, length);
    }

    public void SetData(ReadOnlySpan<byte> data) => data.CopyTo(SetLength(data.Length));

    /// <summary>Releases the segment if <paramref name="generation"/> is still current. True if this call released it.</summary>
    public bool TryRelease(int generation)
    {
        if (Interlocked.CompareExchange(ref Generation, generation + 1, generation) != generation)
            return false;
        Pool.Return(this);
        return true;
    }

    /// <summary>Takes the segment from the holder of <paramref name="generation"/> without returning it to the pool.</summary>
    public bool TryTake(int generation) => Interlocked.CompareExchange(ref Generation, generation + 1, generation) == generation;

    public void Reset()
    {
        Connection = null;
        IsOwned = false;
        Length = 0;
        BitLength = 0;
        Kind = default;
        RakNetGuid = RakNetGuid.Unassigned;
        Address = SystemAddress.Unassigned;
        SocketIndex = 0;
        ReceivedTime = 0;
        IsLocal = false;
        IsIncoming = false;
        DisconnectReason = default;
        ClosedLocally = false;
        ConnectFailure = default;
        RemoteProtocolVersion = 0;
        Receipt = default;
    }
}

/// <summary>The peer's segments. Thread-safe; owned events are returned from whichever thread disposes them.</summary>
internal sealed class EventPool
{
    private readonly Stack<EventSegment> _free = new();
    private readonly Lock _gate = new();
    private long _outstanding;
    private long _owned;
    private long _misses;

    public PoolStatistics Statistics => new(Interlocked.Read(ref _outstanding), Interlocked.Read(ref _owned), Interlocked.Read(ref _misses));

    public EventSegment Rent()
    {
        EventSegment? segment;
        lock (_gate)
            _free.TryPop(out segment);
        if (segment is null)
        {
            Interlocked.Increment(ref _misses);
            segment = new EventSegment(this);
        }
        Interlocked.Increment(ref _outstanding);
        return segment;
    }

    public void MarkOwned(EventSegment segment)
    {
        segment.IsOwned = true;
        Interlocked.Increment(ref _owned);
    }

    public void Return(EventSegment segment)
    {
        if (segment.IsOwned)
            Interlocked.Decrement(ref _owned);
        segment.Reset();
        Interlocked.Decrement(ref _outstanding);
        lock (_gate)
            _free.Push(segment);
    }

    /// <summary>A segment handed back by <see cref="EventSegment.TryTake"/> that is queued again rather than freed.</summary>
    public void Reuse(EventSegment segment)
    {
        if (segment.IsOwned)
            Interlocked.Decrement(ref _owned);
        segment.IsOwned = false;
    }
}
