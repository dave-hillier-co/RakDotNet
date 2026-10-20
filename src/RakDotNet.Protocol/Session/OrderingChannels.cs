using System.Runtime.InteropServices;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// Receive-side ordering and sequencing for the 32 ordering channels of one connection (ReliabilityLayer.cpp:1248-1484):
/// orderedReadIndex, highestSequencedReadIndex, heapIndexOffsets and orderingHeaps.
/// </summary>
/// <remarks>
/// <para>
/// Ordered messages are delivered in ordering-index order; ones that arrive early wait in a heap. A sequenced message
/// carries the ordering index of the next ordered message on its channel, so it waits for the ordered messages sent
/// before it, and is dropped when a newer sequenced message with the same ordering index was already delivered.
/// </para>
/// <para>
/// Heap weights are <c>(orderingIndex - heapIndexOffset) * 2^20</c> plus the sequencing index for sequenced messages
/// or <c>2^20 - 1</c> for ordered ones, so sequenced messages come out before the ordered message that follows them.
/// </para>
/// <para>
/// Unlike the C++, the heaps are bounded: a channel holds at most <see cref="SessionLimits.MaxOrderingBufferMessagesPerChannel"/>
/// messages, and a message is only buffered within the receive buffer bytes the caller says are left. A buffered message
/// owns a copy of its payload, so it never keeps the datagram it arrived in alive.
/// </para>
/// </remarks>
internal sealed class OrderingChannels
{
    /// <summary>NUMBER_OF_ORDERED_STREAMS, ReliabilityLayer.h:52.</summary>
    public const int ChannelCount = 32;

    private const ulong SequenceSpan = 1_048_576; // ReliabilityLayer.cpp:1440

    private readonly UInt24[] _orderedReadIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _highestSequencedReadIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _heapIndexOffsets = new UInt24[ChannelCount];
    private readonly PriorityQueue<ReceivedMessage, ulong>[] _heaps = new PriorityQueue<ReceivedMessage, ulong>[ChannelCount];
    private readonly int _maxBufferedPerChannel;

    public OrderingChannels(int maxBufferedPerChannel = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedPerChannel);
        _maxBufferedPerChannel = maxBufferedPerChannel;
        for (var i = 0; i < ChannelCount; i++)
            _heaps[i] = new PriorityQueue<ReceivedMessage, ulong>();
    }

    /// <summary>
    /// The bytes held in the heaps, each message charged its payload plus <see cref="SessionLimits.BufferedEntryOverheadBytes"/>.
    /// </summary>
    public long BufferedBytes { get; private set; }

    /// <summary>
    /// Adds every message <paramref name="message"/> releases to <paramref name="delivered"/>, in delivery order:
    /// nothing, the message itself, or the message followed by ones it unblocked. When the message has to wait but its
    /// channel is full, or holding it would cost more than <paramref name="availableBytes"/>, nothing is buffered or
    /// delivered and the result says which limit stopped it.
    /// </summary>
    public OrderingResult Receive(ReceivedMessage message, ICollection<ReceivedMessage> delivered, long availableBytes = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(delivered);

        if (!message.Reliability.HasOrderingChannel())
        {
            delivered.Add(message);
            return OrderingResult.Accepted;
        }

        var channel = message.OrderingChannel;
        var isSequenced = message.Reliability.IsSequenced();
        if (message.OrderingIndex == _orderedReadIndex[channel])
        {
            if (isSequenced)
            {
                if (OrderingIndex.IsOlder(message.SequencingIndex, _highestSequencedReadIndex[channel]))
                    return OrderingResult.Accepted; // Lower than the highest known value.
                // ReliabilityLayer.cpp:1314-1317: plus one, so a duplicate is not returned again.
                _highestSequencedReadIndex[channel] = message.SequencingIndex + 1u;
                delivered.Add(message);
                return OrderingResult.Accepted;
            }

            delivered.Add(message);
            _orderedReadIndex[channel]++;
            _highestSequencedReadIndex[channel] = default;

            var heap = _heaps[channel];
            while (heap.TryPeek(out var next, out _) && next.OrderingIndex == _orderedReadIndex[channel])
            {
                heap.Dequeue();
                BufferedBytes -= Cost(next);
                delivered.Add(next);
                if (next.Reliability.IsSequenced())
                    _highestSequencedReadIndex[channel] = next.SequencingIndex; // No plus one here (ReliabilityLayer.cpp:1421).
                else
                    _orderedReadIndex[channel]++;
            }
            return OrderingResult.Accepted;
        }

        if (OrderingIndex.IsOlder(message.OrderingIndex, _orderedReadIndex[channel]))
            return OrderingResult.Accepted; // An older resend.

        // A newer ordering index: buffer it until the gap fills.
        var buffered = _heaps[channel];
        if (buffered.Count >= _maxBufferedPerChannel)
            return OrderingResult.ChannelFull;
        var cost = Cost(message);
        if (cost > availableBytes)
            return OrderingResult.ReceiveBufferFull;
        message = Owned(message);
        BufferedBytes += cost;
        if (buffered.Count == 0)
            _heapIndexOffsets[channel] = _orderedReadIndex[channel];
        ulong orderedHoleCount = (message.OrderingIndex - _heapIndexOffsets[channel]).Value;
        var weight = orderedHoleCount * SequenceSpan + (isSequenced ? message.SequencingIndex.Value : SequenceSpan - 1);
        buffered.Enqueue(message, weight);
        return OrderingResult.Accepted;
    }

    /// <summary>Releases every buffered message.</summary>
    public void Clear()
    {
        foreach (var heap in _heaps)
            heap.Clear();
        BufferedBytes = 0;
    }

    private static long Cost(ReceivedMessage message) => (long)message.Payload.Length + SessionLimits.BufferedEntryOverheadBytes;

    // A message that owns its whole array keeps it; one that slices a datagram gets a copy of its payload.
    private static ReceivedMessage Owned(ReceivedMessage message) =>
        MemoryMarshal.TryGetArray(message.Payload, out var segment) && segment.Offset == 0 && segment.Count == segment.Array!.Length
            ? message
            : message with { Payload = message.Payload.ToArray() };
}

/// <summary>What <see cref="OrderingChannels.Receive"/> did with a message.</summary>
internal enum OrderingResult
{
    /// <summary>Delivered, buffered, or dropped as stale, as the C++ does.</summary>
    Accepted,

    /// <summary>It had to wait on a channel already holding the most messages it may; nothing changed.</summary>
    ChannelFull,

    /// <summary>Holding it would exceed the receive buffer bytes left; nothing changed.</summary>
    ReceiveBufferFull,
}
