namespace RakDotNet.Transport;

/// <summary>
/// RakNet's packetReturnQueue: the entries of the event stream between the I/O cycle and the dispatch step, with
/// PushAtHead for <c>Inject(..., atHead: true)</c>.
/// </summary>
internal sealed class EventQueue
{
    private readonly Lock _gate = new();
    private EventSegment[] _items = new EventSegment[64];
    private int _head;
    private int _count;

    public int Count => Volatile.Read(ref _count);

    public void Enqueue(EventSegment segment)
    {
        lock (_gate)
        {
            Grow();
            _items[(_head + _count) % _items.Length] = segment;
            _count++;
        }
    }

    public void EnqueueAtHead(EventSegment segment)
    {
        lock (_gate)
        {
            Grow();
            _head = (_head - 1 + _items.Length) % _items.Length;
            _items[_head] = segment;
            _count++;
        }
    }

    public bool TryDequeue(out EventSegment segment)
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                segment = null!;
                return false;
            }
            segment = _items[_head];
            _items[_head] = null!;
            _head = (_head + 1) % _items.Length;
            _count--;
            return true;
        }
    }

    private void Grow()
    {
        if (_count < _items.Length)
            return;
        var larger = new EventSegment[_items.Length * 2];
        for (var i = 0; i < _count; i++)
            larger[i] = _items[(_head + i) % _items.Length];
        _items = larger;
        _head = 0;
    }
}
