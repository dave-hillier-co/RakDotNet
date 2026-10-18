using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// Messages waiting to be sent, highest priority first: outgoingPacketBuffer, a min-heap keyed by the weights of
/// ReliabilityLayer::GetNextWeight (ReliabilityLayer.cpp:3926-3949).
/// </summary>
/// <remarks>
/// Each priority's weights climb faster the lower the priority, so lower priorities are interleaved rather than
/// starved. Equal weights leave in the order queued.
/// </remarks>
internal sealed class SendQueue<T>
{
    private const int PriorityCount = 4; // NUMBER_OF_PRIORITIES

    private readonly PriorityQueue<(T Item, Priority Priority, ulong Weight), (ulong Weight, long Sequence)> _heap = new();
    private readonly ulong[] _nextWeights = new ulong[PriorityCount];
    private long _sequence;

    public SendQueue()
    {
        InitWeights();
    }

    public int Count => _heap.Count;

    public void Enqueue(T item, Priority priority)
    {
        var weight = NextWeight((int)priority);
        _heap.Enqueue((item, priority, weight), (weight, _sequence++));
    }

    public bool TryPeek(out T item)
    {
        if (_heap.TryPeek(out var entry, out _))
        {
            item = entry.Item;
            return true;
        }
        item = default!;
        return false;
    }

    public T Dequeue() => _heap.Dequeue().Item;

    // InitHeapWeights, ReliabilityLayer.cpp:3926-3930.
    private void InitWeights()
    {
        for (var level = 0; level < PriorityCount; level++)
            _nextWeights[level] = (ulong)((1 << level) * level + level);
    }

    // GetNextWeight, ReliabilityLayer.cpp:3932-3949. The weight read before an empty queue resets the table is the
    // one returned, as in the C++.
    private ulong NextWeight(int level)
    {
        var next = _nextWeights[level];
        if (_heap.TryPeek(out var head, out _))
        {
            var peekLevel = (int)head.Priority;
            var min = head.Weight - (ulong)((1 << peekLevel) * peekLevel) + (ulong)peekLevel;
            if (next < min)
                next = min + (ulong)((1 << level) * level + level);
            _nextWeights[level] = next + (ulong)((1 << level) * (level + 1) + level);
        }
        else
        {
            InitWeights();
        }
        return next;
    }
}
