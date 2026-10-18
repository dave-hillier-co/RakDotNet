using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// datagramHistory and datagramHistoryPopCount, ReliabilityLayer.cpp:3767-3836.
public class DatagramHistoryTests
{
    [Fact]
    public void A_sent_datagram_is_found_by_number_with_its_send_time_and_message_numbers()
    {
        var history = new DatagramHistory();
        history.Add(1_000, [new UInt24(7), new UInt24(8)]);
        history.Add(2_000, [new UInt24(9)]);

        Assert.True(history.TryGet(new UInt24(1), out var sentUs, out var messages));
        Assert.Equal(2_000, sentUs);
        Assert.Equal([new UInt24(9)], messages);
    }

    [Fact]
    public void A_datagram_with_only_unreliable_messages_is_numbered_but_never_found()
    {
        // AddFirstToDatagramHistory(datagramNumber, timeSent) pushes a node with no messages (ReliabilityLayer.cpp:3797-3809).
        var history = new DatagramHistory();
        history.Add(1_000, []);
        history.Add(2_000, [new UInt24(0)]);

        Assert.False(history.TryGet(new UInt24(0), out _, out _));
        Assert.True(history.TryGet(new UInt24(1), out _, out _));
    }

    [Fact]
    public void A_removed_datagram_is_not_found_again()
    {
        var history = new DatagramHistory();
        history.Add(1_000, [new UInt24(0)]);

        history.Remove(new UInt24(0));

        Assert.False(history.TryGet(new UInt24(0), out _, out _));
    }

    [Fact]
    public void Numbers_never_sent_are_not_found()
    {
        var history = new DatagramHistory();
        history.Add(1_000, [new UInt24(0)]);

        Assert.False(history.TryGet(new UInt24(1), out _, out _));
        Assert.False(history.TryGet(new UInt24(UInt24.Mask), out _, out _));
    }

    [Fact]
    public void Only_the_most_recent_513_datagrams_are_kept()
    {
        // The oldest is popped when the size exceeds DATAGRAM_MESSAGE_ID_ARRAY_LENGTH (512) before a push.
        var history = new DatagramHistory();
        for (var i = 0; i < 514; i++)
            history.Add(i, [new UInt24((uint)i)]);

        Assert.Equal(513, history.Count);
        Assert.Equal(new UInt24(1), history.FirstNumber);
        Assert.False(history.TryGet(new UInt24(0), out _, out _));
        Assert.True(history.TryGet(new UInt24(1), out _, out _));
        Assert.True(history.TryGet(new UInt24(513), out _, out _));
    }
}
