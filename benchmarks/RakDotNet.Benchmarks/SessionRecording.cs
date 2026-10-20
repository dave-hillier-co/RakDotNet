using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// The datagrams two live <see cref="ReliabilityLayer"/>s exchange, step by step, while one sends a steady stream of
/// messages to the other. The layers are deterministic, so a fresh layer fed the same sends, times and recorded
/// datagrams from the other side behaves exactly as the live one did; that is what lets the send and receive halves
/// be measured on their own.
/// </summary>
internal sealed class SessionRecording
{
    public const int Mtu = 1492;
    public const long StartUs = 1_000_000;
    public const long StepUs = 1_000;

    private readonly List<byte[]>[] _toReceiver;
    private readonly List<byte[]>[] _toSender;

    public SessionRecording(int steps, int messagesPerStep, byte[] message, Reliability reliability)
    {
        Steps = steps;
        MessagesPerStep = messagesPerStep;
        Message = message;
        Reliability = reliability;
        _toReceiver = new List<byte[]>[steps];
        _toSender = new List<byte[]>[steps];

        var sender = new ReliabilityLayer(Mtu, StartUs);
        var receiver = new ReliabilityLayer(Mtu, StartUs);
        for (var step = 0; step < steps; step++)
        {
            var nowUs = TimeOf(step);
            SendStep(sender, nowUs);
            _toReceiver[step] = Take(sender);
            foreach (var datagram in _toReceiver[step])
                receiver.Receive(datagram, nowUs);
            receiver.Update(nowUs);
            Delivered += CountDelivered(receiver);
            _toSender[step] = Take(receiver);
            foreach (var datagram in _toSender[step])
                sender.Receive(datagram, nowUs);
            while (sender.TryDequeueEvent(out _))
            {
            }
        }
        if (Delivered != steps * messagesPerStep)
            throw new InvalidOperationException($"The recording delivered {Delivered} of {steps * messagesPerStep} messages.");
    }

    public int Steps { get; }

    public int MessagesPerStep { get; }

    public byte[] Message { get; }

    public Reliability Reliability { get; }

    /// <summary>Messages the live receiver delivered over the whole recording.</summary>
    public int Delivered { get; }

    public static long TimeOf(int step) => StartUs + (step + 1) * StepUs;

    public static ReliabilityLayer NewLayer() => new(Mtu, StartUs);

    /// <summary>The sender's datagrams in <paramref name="step"/>, in order.</summary>
    public IReadOnlyList<byte[]> ToReceiver(int step) => _toReceiver[step];

    /// <summary>The receiver's datagrams (acknowledgements) in <paramref name="step"/>, in order.</summary>
    public IReadOnlyList<byte[]> ToSender(int step) => _toSender[step];

    /// <summary>Queues this step's messages on <paramref name="sender"/> and updates it.</summary>
    public void SendStep(ReliabilityLayer sender, long nowUs)
    {
        for (var i = 0; i < MessagesPerStep; i++)
            sender.Send(Message, Reliability, Priority.High, channel: 0, receipt: 0, nowUs);
        sender.Update(nowUs);
    }

    /// <summary>Takes the layer's events, recycling each delivery as PeerCore does, and counts the deliveries.</summary>
    public static int CountDelivered(ReliabilityLayer layer)
    {
        var delivered = 0;
        while (layer.TryDequeueEvent(out var e))
        {
            if (e is MessageDelivered message)
            {
                delivered++;
                layer.Recycle(message);
            }
        }
        return delivered;
    }

    private static List<byte[]> Take(ReliabilityLayer layer)
    {
        var datagrams = new List<byte[]>();
        while (layer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram);
        return datagrams;
    }
}
