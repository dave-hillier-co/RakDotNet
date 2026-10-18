using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// outgoingPacketBuffer and its weights, ReliabilityLayer::GetNextWeight (ReliabilityLayer.cpp:3926-3949).
public class SendQueueTests
{
    [Fact]
    public void Messages_of_one_priority_leave_in_the_order_queued()
    {
        var queue = new SendQueue<string>();
        queue.Enqueue("a", Priority.High);
        queue.Enqueue("b", Priority.High);
        queue.Enqueue("c", Priority.High);

        Assert.Equal(["a", "b", "c"], Drain(queue));
    }

    [Fact]
    public void Higher_priorities_queued_later_overtake_lower_ones()
    {
        // Weights from GetNextWeight: Low 27 on an empty queue, then High 9 and Immediate 8.
        var queue = new SendQueue<string>();
        queue.Enqueue("low", Priority.Low);
        queue.Enqueue("high", Priority.High);
        queue.Enqueue("immediate", Priority.Immediate);

        Assert.Equal(["immediate", "high", "low"], Drain(queue));
    }

    [Fact]
    public void Lower_priorities_are_interleaved_rather_than_starved()
    {
        var queue = new SendQueue<string>();
        queue.Enqueue("low", Priority.Low);
        for (var i = 0; i < 20; i++)
            queue.Enqueue($"high{i}", Priority.High);

        var order = Drain(queue);

        Assert.InRange(order.IndexOf("low"), 1, 19);
    }

    [Fact]
    public void Peek_returns_the_next_message_without_removing_it()
    {
        var queue = new SendQueue<string>();
        queue.Enqueue("a", Priority.Medium);

        Assert.True(queue.TryPeek(out var peeked));
        Assert.Equal("a", peeked);
        Assert.Equal(1, queue.Count);
    }

    private static List<string> Drain(SendQueue<string> queue)
    {
        var order = new List<string>();
        while (queue.Count > 0)
            order.Add(queue.Dequeue());
        return order;
    }
}
