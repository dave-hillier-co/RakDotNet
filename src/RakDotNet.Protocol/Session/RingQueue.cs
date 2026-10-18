namespace RakDotNet.Protocol.Session;

/// <summary>
/// A growable FIFO queue with indexed access, standing in for DataStructures::Queue (DS_Queue.h), which RakNet
/// indexes from the head.
/// </summary>
internal sealed class RingQueue<T>
{
    private T[] _items;
    private int _head;

    public RingQueue(int capacity = 16)
    {
        _items = new T[Math.Max(capacity, 1)];
    }

    public int Count { get; private set; }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            return _items[(_head + index) % _items.Length];
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count, nameof(index));
            _items[(_head + index) % _items.Length] = value;
        }
    }

    public void Enqueue(T item)
    {
        if (Count == _items.Length)
            Grow();
        _items[(_head + Count) % _items.Length] = item;
        Count++;
    }

    public T Dequeue()
    {
        if (Count == 0)
            throw new InvalidOperationException("The queue is empty.");
        var item = _items[_head];
        _items[_head] = default!;
        _head = (_head + 1) % _items.Length;
        Count--;
        return item;
    }

    public T Peek()
    {
        if (Count == 0)
            throw new InvalidOperationException("The queue is empty.");
        return _items[_head];
    }

    public void Clear()
    {
        Array.Clear(_items);
        _head = 0;
        Count = 0;
    }

    private void Grow()
    {
        var items = new T[_items.Length * 2];
        for (var i = 0; i < Count; i++)
            items[i] = _items[(_head + i) % _items.Length];
        _items = items;
        _head = 0;
    }
}
