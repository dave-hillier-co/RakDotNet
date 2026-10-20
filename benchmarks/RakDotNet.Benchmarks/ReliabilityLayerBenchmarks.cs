using BenchmarkDotNet.Attributes;
using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// The send and receive halves of <see cref="ReliabilityLayer"/>, measured apart by replaying a
/// <see cref="SessionRecording"/>. One operation is one step: with one message per step that is one message in its own
/// datagram; with 16 it is one datagram carrying 16 small messages.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Send"/>: queue the step's messages, update, frame the datagram, then take the receiver's recorded
/// acknowledgements. <see cref="Receive"/>: take the sender's recorded datagrams, deliver the messages, update and frame
/// the acknowledgements.
/// </para>
/// <para>
/// When the recording runs out the replayed layer is replaced by a new one, once every
/// <see cref="RecordingSteps"/> operations; that construction is amortised into the measurement.
/// </para>
/// <para>
/// Datagrams and deliveries are taken as PeerCore takes them: copied into a reused buffer, and recycled once read.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class ReliabilityLayerBenchmarks
{
    private const int RecordingSteps = 16_384;
    private const int MessageBytes = 32;

    private SessionRecording _recording = null!;
    private ReliabilityLayer _sender = null!;
    private ReliabilityLayer _receiver = null!;
    private int _sendStep;
    private int _receiveStep;
    private readonly byte[] _datagram = new byte[SessionRecording.Mtu];

    [Params(1, 16)]
    public int MessagesPerStep { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var message = new byte[MessageBytes];
        message[0] = (byte)MessageId.UserPacketEnum;
        _recording = new SessionRecording(RecordingSteps, MessagesPerStep, message, Reliability.ReliableOrdered);
        VerifyReplay();
        _sender = SessionRecording.NewLayer();
        _receiver = SessionRecording.NewLayer();
    }

    [Benchmark]
    public void Send()
    {
        if (_sendStep == RecordingSteps)
        {
            _sender = SessionRecording.NewLayer();
            _sendStep = 0;
        }
        SendStep(_sender, _sendStep++);
    }

    [Benchmark]
    public int Receive()
    {
        if (_receiveStep == RecordingSteps)
        {
            _receiver = SessionRecording.NewLayer();
            _receiveStep = 0;
        }
        return ReceiveStep(_receiver, _receiveStep++);
    }

    private void SendStep(ReliabilityLayer sender, int step)
    {
        var nowUs = SessionRecording.TimeOf(step);
        _recording.SendStep(sender, nowUs);
        while (sender.TryDequeueDatagram(_datagram, out _))
        {
        }
        var acks = _recording.ToSender(step);
        for (var i = 0; i < acks.Count; i++)
            sender.Receive(acks[i], nowUs);
        while (sender.TryDequeueEvent(out _))
        {
        }
    }

    private int ReceiveStep(ReliabilityLayer receiver, int step)
    {
        var nowUs = SessionRecording.TimeOf(step);
        var datagrams = _recording.ToReceiver(step);
        for (var i = 0; i < datagrams.Count; i++)
            receiver.Receive(datagrams[i], nowUs);
        receiver.Update(nowUs);
        while (receiver.TryDequeueDatagram(_datagram, out _))
        {
        }
        return SessionRecording.CountDelivered(receiver);
    }

    // A replayed layer must send exactly what the live one sent, or the replay measures something else.
    private void VerifyReplay()
    {
        var sender = SessionRecording.NewLayer();
        var receiver = SessionRecording.NewLayer();
        var delivered = 0;
        for (var step = 0; step < RecordingSteps; step++)
        {
            var nowUs = SessionRecording.TimeOf(step);
            _recording.SendStep(sender, nowUs);
            AssertSame(sender, _recording.ToReceiver(step), "sender", step);
            foreach (var ack in _recording.ToSender(step))
                sender.Receive(ack, nowUs);

            foreach (var datagram in _recording.ToReceiver(step))
                receiver.Receive(datagram, nowUs);
            receiver.Update(nowUs);
            delivered += SessionRecording.CountDelivered(receiver);
            AssertSame(receiver, _recording.ToSender(step), "receiver", step);
        }
        if (delivered != _recording.Delivered)
            throw new InvalidOperationException($"The replayed receiver delivered {delivered} messages, not {_recording.Delivered}.");
    }

    private static void AssertSame(ReliabilityLayer layer, IReadOnlyList<byte[]> recorded, string side, int step)
    {
        var index = 0;
        while (layer.TryDequeueDatagram(out var datagram))
        {
            if (index >= recorded.Count || !datagram.AsSpan().SequenceEqual(recorded[index]))
                throw new InvalidOperationException($"The replayed {side} diverged from the recording at step {step}.");
            index++;
        }
        if (index != recorded.Count)
            throw new InvalidOperationException($"The replayed {side} sent {index} datagrams at step {step}, not {recorded.Count}.");
    }
}
