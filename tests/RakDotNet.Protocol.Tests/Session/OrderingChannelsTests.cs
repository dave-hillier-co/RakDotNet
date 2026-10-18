using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Tests.Session;

// Receive-side ordering and sequencing, ReliabilityLayer.cpp:1248-1484. Each message's payload is one byte naming
// it, so the delivered order reads as a string.
public class OrderingChannelsTests
{
    private readonly OrderingChannels _channels = new();
    private readonly List<EncapsulatedMessage> _delivered = [];

    private string Delivered => string.Concat(_delivered.Select(m => (char)m.Payload.Span[0]));

    [Fact]
    public void Unordered_messages_are_delivered_immediately()
    {
        Receive(Message('a', Reliability.Unreliable));
        Receive(Message('b', Reliability.Reliable));

        Assert.Equal("ab", Delivered);
    }

    [Fact]
    public void Ordered_messages_arriving_in_order_are_delivered_in_order()
    {
        Receive(Ordered('a', 0));
        Receive(Ordered('b', 1));
        Receive(Ordered('c', 2));

        Assert.Equal("abc", Delivered);
    }

    [Fact]
    public void Ordered_messages_arriving_early_wait_for_the_missing_ones()
    {
        Receive(Ordered('c', 2));
        Receive(Ordered('b', 1));
        Assert.Equal("", Delivered);

        Receive(Ordered('a', 0));

        Assert.Equal("abc", Delivered);
    }

    [Fact]
    public void Ordered_messages_older_than_the_read_index_are_dropped()
    {
        Receive(Ordered('a', 0));
        Receive(Ordered('x', 0));

        Assert.Equal("a", Delivered);
    }

    [Fact]
    public void Channels_are_ordered_independently()
    {
        Receive(Ordered('b', 1, channel: 0));
        Receive(Ordered('c', 0, channel: 3));
        Receive(Ordered('a', 0, channel: 0));

        Assert.Equal("cab", Delivered);
    }

    [Fact]
    public void Sequenced_messages_newer_than_the_highest_seen_are_delivered_and_older_ones_dropped()
    {
        Receive(Sequenced('a', orderingIndex: 0, sequencingIndex: 0));
        Receive(Sequenced('c', orderingIndex: 0, sequencingIndex: 2));
        Receive(Sequenced('b', orderingIndex: 0, sequencingIndex: 1));
        Receive(Sequenced('d', orderingIndex: 0, sequencingIndex: 3));

        Assert.Equal("acd", Delivered);
    }

    [Fact]
    public void A_duplicated_sequenced_message_is_delivered_once()
    {
        // ReliabilityLayer.cpp:1314-1317: the highest index is stored plus one, so an equal index is older.
        Receive(Sequenced('a', 0, 0, Reliability.UnreliableSequenced));
        Receive(Sequenced('a', 0, 0, Reliability.UnreliableSequenced));

        Assert.Equal("a", Delivered);
    }

    [Fact]
    public void Sequenced_messages_sent_after_an_ordered_one_wait_for_it()
    {
        // A sequenced message carries the channel's next ordering index (ReliabilityLayer.cpp:1634), so it is held
        // until the ordered message before it arrives.
        Receive(Sequenced('b', orderingIndex: 1, sequencingIndex: 0));
        Assert.Equal("", Delivered);

        Receive(Ordered('a', 0));

        Assert.Equal("ab", Delivered);
    }

    [Fact]
    public void The_connect_send_disconnect_stream_is_delivered_in_send_order()
    {
        // The client's channel 0 messages from tests/vectors/connect-send-disconnect.json datagrams 8, 12 and 15,
        // with the unreliable one sent between the first ordered and the sequenced.
        Receive(Ordered('n', 0));
        Receive(Ordered('o', 1));
        Receive(Message('u', Reliability.Unreliable));
        Receive(Sequenced('s', orderingIndex: 2, sequencingIndex: 0));
        Receive(Ordered('r', 2));
        Receive(Ordered('d', 3));

        Assert.Equal("nousrd", Delivered);
    }

    private void Receive(EncapsulatedMessage message) => _channels.Receive(message, _delivered);

    private static EncapsulatedMessage Message(char name, Reliability reliability) =>
        new(reliability, new[] { (byte)name });

    private static EncapsulatedMessage Ordered(char name, uint orderingIndex, byte channel = 0) =>
        new(Reliability.ReliableOrdered, new[] { (byte)name }, orderingIndex: new UInt24(orderingIndex), orderingChannel: channel);

    private static EncapsulatedMessage Sequenced(
        char name,
        uint orderingIndex,
        uint sequencingIndex,
        Reliability reliability = Reliability.ReliableSequenced
    ) =>
        new(
            reliability,
            new[] { (byte)name },
            sequencingIndex: new UInt24(sequencingIndex),
            orderingIndex: new UInt24(orderingIndex)
        );
}
