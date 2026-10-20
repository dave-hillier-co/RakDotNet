using RakDotNet.Protocol.Session;

namespace RakDotNet.Protocol.Tests.Session;

public class DatagramQueueTests
{
    [Fact]
    public void Datagrams_leave_in_the_order_they_were_queued_with_their_tags()
    {
        var queue = new DatagramQueue<int>(initialCapacity: 4);
        queue.Enqueue([1, 2, 3], 10);
        Write(queue, [4], 20);
        queue.Enqueue([5, 6, 7, 8, 9], 30);

        Assert.Equal(3, queue.Count);
        AssertTaken(queue, [1, 2, 3], 10);
        AssertTaken(queue, [4], 20);
        AssertTaken(queue, [5, 6, 7, 8, 9], 30);
        Assert.False(queue.TryDequeue(out _, out _));
    }

    [Fact]
    public void Only_the_committed_length_of_a_reservation_is_queued()
    {
        var queue = new DatagramQueue<int>();
        var room = queue.Reserve(1492);
        room[0] = 7;
        room[1] = 8;
        queue.Commit(2, 0);
        queue.Enqueue([9], 0);

        Assert.Equal([7, 8], Take(queue).Bytes);
        Assert.Equal([9], Take(queue).Bytes);
    }

    [Fact]
    public void A_taken_datagram_is_not_written_over_while_others_are_still_queued()
    {
        var queue = new DatagramQueue<int>(initialCapacity: 4);
        queue.Enqueue([1, 2], 0);
        queue.Enqueue([3, 4], 0);
        Assert.True(queue.TryDequeue(out var first, out _));

        // Still one queued, so the buffer is not reused from the start; this one also forces it to grow.
        queue.Enqueue([5, 6, 7, 8, 9, 10], 0);

        Assert.Equal([1, 2], first.ToArray());
        Assert.Equal([3, 4], Take(queue).Bytes);
        Assert.Equal([5, 6, 7, 8, 9, 10], Take(queue).Bytes);
    }

    [Fact]
    public void Committing_without_a_reservation_is_a_programming_error()
    {
        var queue = new DatagramQueue<int>();
        Assert.Throws<InvalidOperationException>(() => queue.Commit(1, 0));
        queue.Reserve(4);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Commit(5, 0));
    }

    [Fact]
    public void Queueing_and_taking_datagrams_allocates_nothing_once_the_buffer_has_grown()
    {
        var queue = new DatagramQueue<int>(initialCapacity: 16);
        var datagram = new byte[1492];
        Cycle(queue, datagram);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
            Cycle(queue, datagram);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Cycle(DatagramQueue<int> queue, byte[] datagram)
    {
        for (var i = 0; i < 16; i++)
            queue.Enqueue(datagram, i);
        while (queue.TryDequeue(out _, out _))
        {
        }
    }

    private static void Write(DatagramQueue<int> queue, byte[] bytes, int tag)
    {
        bytes.CopyTo(queue.Reserve(bytes.Length + 10));
        queue.Commit(bytes.Length, tag);
    }

    private static void AssertTaken(DatagramQueue<int> queue, byte[] bytes, int tag)
    {
        var taken = Take(queue);
        Assert.Equal(bytes, taken.Bytes);
        Assert.Equal(tag, taken.Tag);
    }

    private static (byte[] Bytes, int Tag) Take(DatagramQueue<int> queue)
    {
        Assert.True(queue.TryDequeue(out var datagram, out var tag));
        return (datagram.ToArray(), tag);
    }
}
