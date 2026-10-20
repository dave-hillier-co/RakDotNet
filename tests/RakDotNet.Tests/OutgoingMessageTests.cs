using System.Buffers;

namespace RakDotNet.Tests;

/// <summary>
/// What a plugin host other than <see cref="RakPeer"/>, such as the TCP interface, reads from a message it is asked to
/// send (PluginInterface2.cpp:37-42 sends GetData() and GetNumberOfBytesUsed()).
/// </summary>
public sealed class OutgoingMessageTests
{
    [Fact]
    public void A_message_from_bytes_copies_those_bytes()
    {
        var message = new OutgoingMessage([1, 2, 3]);
        var copy = new byte[message.ByteLength];
        message.CopyTo(copy);

        Assert.Equal(3, message.ByteLength);
        Assert.Equal([1, 2, 3], copy);
    }

    [Fact]
    public void A_message_from_parts_copies_them_joined()
    {
        var first = new Segment(new byte[] { 1, 2 });
        var last = first.Append(new byte[] { 3 });
        var message = new OutgoingMessage(new ReadOnlySequence<byte>(first, 0, last, 1));
        var copy = new byte[message.ByteLength];
        message.CopyTo(copy);

        Assert.Equal([1, 2, 3], copy);
    }

    [Fact]
    public void A_message_with_a_partial_last_byte_counts_that_byte()
    {
        var message = new OutgoingMessage([0xFF, 0x80], bitLength: 9);

        Assert.Equal(2, message.ByteLength);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
