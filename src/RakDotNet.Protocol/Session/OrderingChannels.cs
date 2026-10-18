using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

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
/// </remarks>
internal sealed class OrderingChannels
{
    /// <summary>NUMBER_OF_ORDERED_STREAMS, ReliabilityLayer.h:52.</summary>
    public const int ChannelCount = 32;

    private const ulong SequenceSpan = 1_048_576; // ReliabilityLayer.cpp:1440

    private readonly UInt24[] _orderedReadIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _highestSequencedReadIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _heapIndexOffsets = new UInt24[ChannelCount];
    private readonly PriorityQueue<EncapsulatedMessage, ulong>[] _heaps = new PriorityQueue<EncapsulatedMessage, ulong>[ChannelCount];

    public OrderingChannels()
    {
        for (var i = 0; i < ChannelCount; i++)
            _heaps[i] = new PriorityQueue<EncapsulatedMessage, ulong>();
    }

    /// <summary>
    /// Adds every message <paramref name="message"/> releases to <paramref name="delivered"/>, in delivery order:
    /// nothing, the message itself, or the message followed by ones it unblocked.
    /// </summary>
    public void Receive(EncapsulatedMessage message, ICollection<EncapsulatedMessage> delivered)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(delivered);

        if (!message.Reliability.HasOrderingChannel())
        {
            delivered.Add(message);
            return;
        }

        var channel = message.OrderingChannel;
        var isSequenced = message.Reliability.IsSequenced();
        if (message.OrderingIndex == _orderedReadIndex[channel])
        {
            if (isSequenced)
            {
                if (OrderingIndex.IsOlder(message.SequencingIndex, _highestSequencedReadIndex[channel]))
                    return; // Lower than the highest known value.
                // ReliabilityLayer.cpp:1314-1317: plus one, so a duplicate is not returned again.
                _highestSequencedReadIndex[channel] = message.SequencingIndex + 1u;
                delivered.Add(message);
                return;
            }

            delivered.Add(message);
            _orderedReadIndex[channel]++;
            _highestSequencedReadIndex[channel] = default;

            var heap = _heaps[channel];
            while (heap.TryPeek(out var next, out _) && next.OrderingIndex == _orderedReadIndex[channel])
            {
                heap.Dequeue();
                delivered.Add(next);
                if (next.Reliability.IsSequenced())
                    _highestSequencedReadIndex[channel] = next.SequencingIndex; // No plus one here (ReliabilityLayer.cpp:1421).
                else
                    _orderedReadIndex[channel]++;
            }
            return;
        }

        if (OrderingIndex.IsOlder(message.OrderingIndex, _orderedReadIndex[channel]))
            return; // An older resend.

        // A newer ordering index: buffer it until the gap fills.
        var buffered = _heaps[channel];
        if (buffered.Count == 0)
            _heapIndexOffsets[channel] = _orderedReadIndex[channel];
        ulong orderedHoleCount = (message.OrderingIndex - _heapIndexOffsets[channel]).Value;
        var weight = orderedHoleCount * SequenceSpan + (isSequenced ? message.SequencingIndex.Value : SequenceSpan - 1);
        buffered.Enqueue(message, weight);
    }
}
