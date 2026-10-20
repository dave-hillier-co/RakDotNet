namespace RakDotNet.Plugins.Tests.TcpInterface;

public sealed class ByteQueueTests
{
    [Fact]
    public void Bytes_come_out_in_the_order_they_were_written()
    {
        var queue = new ByteQueue();
        queue.WriteBytes([1, 2, 3]);
        queue.WriteBytes([4, 5]);

        var read = new byte[5];
        Assert.True(queue.ReadBytes(read, peek: false));

        Assert.Equal([1, 2, 3, 4, 5], read);
        Assert.Equal(0, queue.BytesWritten);
    }

    [Fact]
    public void A_peek_leaves_the_bytes_in_the_queue()
    {
        var queue = new ByteQueue();
        queue.WriteBytes([1, 2, 3]);

        var peeked = new byte[2];
        Assert.True(queue.ReadBytes(peeked, peek: true));

        Assert.Equal([1, 2], peeked);
        Assert.Equal(3, queue.BytesWritten);
    }

    [Fact]
    public void A_read_copies_at_most_what_is_written_and_an_empty_queue_reads_nothing()
    {
        // DS_ByteQueue.cpp:69-97: the copy is the smaller of the two lengths; nothing to copy returns false.
        var queue = new ByteQueue();
        var destination = new byte[4];
        Assert.False(queue.ReadBytes(destination, peek: false));

        queue.WriteBytes([7, 8]);
        Assert.True(queue.ReadBytes(destination, peek: false));

        Assert.Equal([7, 8, 0, 0], destination);
        Assert.Equal(0, queue.BytesWritten);
    }

    [Fact]
    public void Writes_that_reach_the_end_of_the_buffer_wrap_to_its_start()
    {
        // 200 bytes take the minimum allocation of 256 (DS_ByteQueue.cpp:38-41); after reading 150, 150 more fit
        // without growing, 56 at the end and 94 at the start.
        var queue = new ByteQueue();
        queue.WriteBytes(Sequence(0, 200));
        queue.IncrementReadOffset(150);
        queue.WriteBytes(Sequence(200, 150));

        Assert.Equal(200, queue.BytesWritten);
        Assert.Equal(106, queue.PeekContiguousBytes().Length);
        var read = new byte[200];
        Assert.True(queue.ReadBytes(read, peek: false));
        Assert.Equal(Sequence(150, 200), read);
    }

    [Fact]
    public void Growing_while_wrapped_keeps_the_order()
    {
        var queue = new ByteQueue();
        queue.WriteBytes(Sequence(0, 200));
        queue.IncrementReadOffset(150);
        queue.WriteBytes(Sequence(200, 150));
        queue.WriteBytes(Sequence(350, 300));

        var read = new byte[500];
        Assert.True(queue.ReadBytes(read, peek: false));

        Assert.Equal(Sequence(150, 500), read);
    }

    [Fact]
    public void Decrementing_the_read_offset_reads_the_same_bytes_again()
    {
        var queue = new ByteQueue();
        queue.WriteBytes(Sequence(0, 10));
        queue.IncrementReadOffset(4);
        queue.DecrementReadOffset(4);

        var read = new byte[10];
        queue.ReadBytes(read, peek: false);

        Assert.Equal(Sequence(0, 10), read);
    }

    [Fact]
    public void Clear_empties_the_queue()
    {
        var queue = new ByteQueue();
        queue.WriteBytes([1, 2, 3]);
        queue.Clear();

        Assert.Equal(0, queue.BytesWritten);
        Assert.True(queue.PeekContiguousBytes().IsEmpty);
    }

    private static byte[] Sequence(int start, int count) => [.. Enumerable.Range(start, count).Select(i => (byte)i)];
}
