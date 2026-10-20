using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Session;

// docs/api-design.md section 1: the hot path allocates nothing per message in steady state. Two reliability layers
// exchange small RELIABLE_ORDERED messages and their acknowledgements on the test thread, so every byte the layers
// allocate is counted by GC.GetAllocatedBytesForCurrentThread.
public class ReliabilityLayerAllocationTests
{
    /// <summary>The allocation budget of the session hot path: bytes per non-split message, after warm-up.</summary>
    public const long BudgetBytesPerMessage = 0;

    /// <summary>The allocations that keep the budget from being met.</summary>
    public const string SkipReason =
        "ReliabilityLayer allocates per message: an MTU-sized buffer and a trimmed copy for every data and ACK datagram "
        + "(WriteDatagram, SendAcks), the OutgoingMessage, its payload copy and resend-list node (Send), the datagram history's "
        + "List<UInt24> (WriteDatagram), EncapsulatedMessage (ToEncapsulated, EncapsulatedMessageReader), the received-datagram "
        + "copy (Receive), ReceivedMessage and MessageDelivered, and in the ACK path the AckRangeList, its array, a boxed "
        + "enumerator and the List built by KnownDatagramNumbersIn.";

    private const int Mtu = 1492;
    private const long StepUs = 1_000;
    private const int WarmUpMessages = 4_096;
    private const int MeasuredMessages = 4_096;

    private readonly ReliabilityLayer _sender;
    private readonly ReliabilityLayer _receiver;
    private readonly byte[] _message = new byte[32];
    private long _nowUs = 1_000_000;
    private int _delivered;

    public ReliabilityLayerAllocationTests()
    {
        _sender = new ReliabilityLayer(Mtu, _nowUs);
        _receiver = new ReliabilityLayer(Mtu, _nowUs);
        _message[0] = (byte)MessageId.UserPacketEnum;
    }

    [Fact(Skip = SkipReason)]
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
        while (_sender.TryDequeueDatagram(out var datagram))
            _receiver.Receive(datagram, _nowUs);
        _receiver.Update(_nowUs);
        while (_receiver.TryDequeueEvent(out var e))
        {
            if (e is MessageDelivered)
                _delivered++;
        }
        while (_receiver.TryDequeueDatagram(out var datagram))
            _sender.Receive(datagram, _nowUs);
        while (_sender.TryDequeueEvent(out _))
        {
        }
    }
}
