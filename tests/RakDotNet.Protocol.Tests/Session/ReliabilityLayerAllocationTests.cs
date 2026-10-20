using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// docs/api-design.md section 1: the hot path allocates nothing per message in steady state. Two reliability layers
// exchange small RELIABLE_ORDERED messages and their acknowledgements on the test thread, so every byte the layers
// allocate is counted by GC.GetAllocatedBytesForCurrentThread. The test takes datagrams and events as PeerCore does:
// datagrams copied into its own buffer, and each delivery recycled once read.
public class ReliabilityLayerAllocationTests
{
    /// <summary>The allocation budget of the session hot path: bytes per non-split message, after warm-up.</summary>
    public const long BudgetBytesPerMessage = 0;

    private const int Mtu = 1492;
    private const long StepUs = 1_000;
    private const int WarmUpMessages = 4_096;
    private const int MeasuredMessages = 4_096;

    private readonly ReliabilityLayer _sender;
    private readonly ReliabilityLayer _receiver;
    private readonly byte[] _message = new byte[32];
    private readonly byte[] _datagram = new byte[Mtu];
    private long _nowUs = 1_000_000;
    private int _delivered;

    public ReliabilityLayerAllocationTests()
    {
        _sender = new ReliabilityLayer(Mtu, _nowUs);
        _receiver = new ReliabilityLayer(Mtu, _nowUs);
        _message[0] = (byte)MessageId.UserPacketEnum;
    }

    [Fact]
    public void Sending_and_receiving_a_non_split_message_allocates_nothing_in_steady_state()
    {
        for (var i = 0; i < WarmUpMessages; i++)
            Step();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < MeasuredMessages; i++)
            Step();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(WarmUpMessages + MeasuredMessages, _delivered);
        Assert.True(
            allocated / MeasuredMessages <= BudgetBytesPerMessage,
            $"ReliabilityLayer Send, Update, Receive and the ACK path allocated {allocated} B for {MeasuredMessages} messages "
            + $"({allocated / MeasuredMessages} B per message); the budget is {BudgetBytesPerMessage} B per message."
        );
    }

    // One message from sender to receiver and the acknowledgement back, each side updated once.
    private void Step()
    {
        _nowUs += StepUs;
        _sender.Send(_message, Reliability.ReliableOrdered, Priority.High, channel: 0, receipt: 0, _nowUs);
        _sender.Update(_nowUs);
        while (_sender.TryDequeueDatagram(_datagram, out var length))
            _receiver.Receive(_datagram.AsSpan(0, length), _nowUs);
        _receiver.Update(_nowUs);
        while (_receiver.TryDequeueEvent(out var e))
        {
            if (e is MessageDelivered delivered)
            {
                _delivered++;
                _receiver.Recycle(delivered);
            }
        }
        while (_receiver.TryDequeueDatagram(_datagram, out var length))
            _sender.Receive(_datagram.AsSpan(0, length), _nowUs);
        while (_sender.TryDequeueEvent(out _))
        {
        }
    }
}
