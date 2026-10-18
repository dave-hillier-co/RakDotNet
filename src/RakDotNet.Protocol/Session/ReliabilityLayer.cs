using System.Diagnostics.CodeAnalysis;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// One connection's reliability state machine, ported from ReliabilityLayer.cpp. Sans-IO: received datagrams, sends and
/// the current time go in; datagrams to transmit and <see cref="SessionEvent"/>s come out.
/// </summary>
/// <remarks>
/// <para>
/// Times are microseconds (CCTimeType, see <see cref="SlidingWindow"/>). Every entry point takes the current time;
/// nothing reads a clock.
/// </para>
/// <para>
/// Inputs: <see cref="Send(ReadOnlyMemory{byte}, int, Reliability, Priority, byte, uint, long)"/> queues a message,
/// <see cref="Receive"/> handles one datagram from the remote system (HandleSocketReceiveFromConnectedPlayer), and
/// <see cref="Update"/> runs one tick: it sends due acknowledgements, then packs queued messages into datagrams as the
/// window allows. Outputs: <see cref="TryDequeueDatagram"/> and <see cref="TryDequeueEvent"/>.
/// </para>
/// <para>
/// Resends, NAKs, split messages and the unreliable send timeout are not implemented yet. The places they attach are
/// marked: sent reliable messages already sit in the resend list with their next action time, received datagram gaps
/// are reported by <see cref="SlidingWindow.OnGotPacket"/>, and split headers are decoded by the wire layer.
/// </para>
/// </remarks>
internal sealed class ReliabilityLayer
{
    /// <summary>defaultTimeoutTime of a release build, RakPeer.cpp:247-250.</summary>
    public const long DefaultTimeoutMs = 10_000;

    /// <summary>RESEND_BUFFER_ARRAY_LENGTH, RakNetDefines.h:105.</summary>
    private const int ResendBufferLength = 512;

    private const int ChannelCount = OrderingChannels.ChannelCount;

    // ReliabilityLayer.cpp:1741-1742.
    private const long MaxTimeSinceLastTickUs = 100_000;

    // AllocateFromInternalPacketPool sets reliableMessageNumber to (uint32_t)-1 (ReliabilityLayer.cpp:3678).
    private static readonly UInt24 UnassignedMessageNumber = UInt24.MaxValue;

    private readonly int _mtu;
    private readonly SlidingWindow _window;

    // Sending.
    private readonly UInt24[] _orderedWriteIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _sequencedWriteIndex = new UInt24[ChannelCount];
    private readonly SendQueue<OutgoingMessage> _outgoing = new();
    private readonly OutgoingMessage?[] _resendBuffer = new OutgoingMessage?[ResendBufferLength];
    private readonly LinkedList<OutgoingMessage> _resendList = new();
    private readonly DatagramHistory _datagramHistory = new();
    private readonly List<UnreliableReceipt> _unreliableReceipts = [];
    private UInt24 _nextReliableMessageNumber;
    private int _unacknowledgedBytes;
    private bool _bandwidthExceeded;
    private long _lastUpdateUs;

    // Receiving.
    private readonly List<UInt24> _acknowledgements = [];
    private readonly ReceivedMessageNumbers _receivedMessageNumbers = new();
    private readonly OrderingChannels _orderingChannels = new();
    private readonly List<EncapsulatedMessage> _released = [];
    private long _timeLastDatagramArrivedMs;

    // Outputs.
    private readonly Queue<byte[]> _datagrams = new();
    private readonly Queue<SessionEvent> _events = new();

    /// <summary>ReliabilityLayer::Reset(true, MTUSize, false) (ReliabilityLayer.cpp:345-363).</summary>
    /// <param name="mtu">The negotiated MTU including the 28-byte IP and UDP header.</param>
    /// <param name="nowUs">The current time.</param>
    public ReliabilityLayer(int mtu, long nowUs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mtu, DatagramLimits.MinimumMtuSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mtu, DatagramLimits.MaximumMtuSize);
        _mtu = mtu;
        _window = new SlidingWindow(mtu - DatagramLimits.UdpHeaderBytes);
        _lastUpdateUs = nowUs;
        _timeLastDatagramArrivedMs = nowUs / 1000;
    }

    /// <summary>SetTimeoutTime: how long reliable messages may go unacknowledged with nothing heard.</summary>
    public long TimeoutMs { get; set; } = DefaultTimeoutMs;

    public int Mtu => _mtu;

    /// <summary>IsDeadConnection.</summary>
    public bool IsDead { get; private set; }

    /// <summary>statistics.messagesInResendBuffer: reliable messages sent and not yet acknowledged.</summary>
    public int MessagesInResendBuffer => _resendList.Count;

    /// <summary>IsOutgoingDataWaiting (ReliabilityLayer.cpp:2345-2360).</summary>
    public bool IsOutgoingDataWaiting => _outgoing.Count > 0 || MessagesInResendBuffer != 0;

    /// <summary>AreAcksWaiting (ReliabilityLayer.cpp:2362-2365).</summary>
    public bool AreAcksWaiting => _acknowledgements.Count > 0;

    /// <summary>The most recent round-trip sample in microseconds, or 0 before the first.</summary>
    public double LastRttUs => _window.LastRttUs;

    private int MaxPayloadBits => DatagramLimits.MaxPayloadBits(_mtu);

    /// <summary>A message to send. The bit length is the whole payload.</summary>
    public bool Send(ReadOnlyMemory<byte> data, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs) =>
        Send(data, data.Length * 8, reliability, priority, channel, receipt, nowUs);

    /// <summary>
    /// ReliabilityLayer::Send (ReliabilityLayer.cpp:1539-1683): copies the message, assigns its ordering and sequencing
    /// indexes and queues it. Returns false for an empty message or one that would need splitting.
    /// </summary>
    /// <param name="data">The message, at least <c>ceil(bitLength / 8)</c> bytes.</param>
    /// <param name="bitLength">The message length in bits.</param>
    /// <param name="reliability">How it is delivered.</param>
    /// <param name="priority">Which goes first when several are queued.</param>
    /// <param name="channel">The ordering channel; out-of-range channels become 0, as in the C++.</param>
    /// <param name="receipt">The serial reported by <see cref="ReceiptAcknowledged"/> or <see cref="ReceiptLost"/>.</param>
    /// <param name="nowUs">The current time.</param>
    public bool Send(ReadOnlyMemory<byte> data, int bitLength, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs)
    {
        // Fix any bad parameters, ReliabilityLayer.cpp:1556-1564.
        if (reliability > Reliability.ReliableOrderedWithAckReceipt)
            reliability = Reliability.Reliable;
        if (priority > Priority.Low)
            priority = Priority.High;
        if (channel >= ChannelCount)
            channel = 0;

        if (bitLength <= 0)
            return false;
        var byteLength = (bitLength + 7) / 8;
        ArgumentOutOfRangeException.ThrowIfLessThan(data.Length, byteLength, nameof(data));

        // ReliabilityLayer.cpp:1604-1606.
        var maxDataSizeBytes = MaxPayloadBits / 8 - (EncapsulatedMessage.MaxHeaderLengthBits + 7) / 8;
        if (byteLength > maxDataSizeBytes)
            return false; // Needs SplitPacket (ReliabilityLayer.cpp:1659-1669), not implemented yet.

        var message = new OutgoingMessage(data[..byteLength].ToArray(), bitLength, reliability, priority, receipt, nowUs);
        if (reliability.IsSequenced())
        {
            message.OrderingChannel = channel;
            message.OrderingIndex = _orderedWriteIndex[channel];
            message.SequencingIndex = _sequencedWriteIndex[channel]++;
        }
        else if (reliability.IsOrdered())
        {
            message.OrderingChannel = channel;
            message.OrderingIndex = _orderedWriteIndex[channel]++;
            _sequencedWriteIndex[channel] = default;
        }

        _outgoing.Enqueue(message, priority);
        return true;
    }

    /// <summary>
    /// HandleSocketReceiveFromConnectedPlayer (ReliabilityLayer.cpp:635-1502). Returns false where the C++ does: an
    /// ACK or NAK whose range list is malformed or unacceptable.
    /// </summary>
    public bool Receive(ReadOnlySpan<byte> datagram, long nowUs)
    {
        // A length of 1 or 2 is a connection request resend that is ignored (ReliabilityLayer.cpp:653).
        if (datagram.Length <= 2)
            return true;
        _timeLastDatagramArrivedMs = nowUs / 1000;

        // Copy once, so delivered payloads and buffered ordered messages can slice it after the caller reuses its buffer.
        var bytes = datagram.ToArray();
        var reader = new BitStreamReader(bytes);
        if (!DatagramHeader.TryRead(ref reader, out var header))
            return true; // Not a reliability-layer datagram (ReliabilityLayer.cpp:689-695).

        switch (header.Kind)
        {
            case DatagramKind.Ack:
                return AckRangeList.TryRead(ref reader, out var acks) && ReceiveAcks(acks, nowUs);
            case DatagramKind.Nak:
                // Resends on NAK are not implemented yet; the list is still validated (ReliabilityLayer.cpp:811-817).
                return AckRangeList.TryRead(ref reader, out _);
            default:
                ReceiveData(header, bytes.AsMemory(reader.BitPosition >> 3), nowUs);
                return true;
        }
    }

    /// <summary>
    /// One tick of ReliabilityLayer::Update (ReliabilityLayer.cpp:1687-2242): kill the connection on an ack timeout,
    /// send due acknowledgements, report lost unreliable receipts, then fill datagrams with queued messages while the
    /// window allows.
    /// </summary>
    public void Update(long nowUs)
    {
        if (IsDead)
            return;

        // "This line is necessary because the timer isn't accurate" (ReliabilityLayer.cpp:1727-1733).
        if (nowUs <= _lastUpdateUs)
        {
            _lastUpdateUs = nowUs;
            return;
        }
        var timeSinceLastTickUs = Math.Min(nowUs - _lastUpdateUs, MaxTimeSinceLastTickUs);
        _ = timeSinceLastTickUs; // Used by the unreliable send timeout, not implemented yet.
        _lastUpdateUs = nowUs;

        if (MessagesInResendBuffer != 0 && AckTimeout(nowUs / 1000))
        {
            Die();
            return;
        }

        if (_window.ShouldSendAcks(nowUs))
            SendAcks();

        var needsBAndAs = _window.IsInSlowStart;
        var isContinuousSend = _bandwidthExceeded;
        _bandwidthExceeded = _outgoing.Count > 0;
        var hasDataToSendOrResend = _resendList.Count > 0 || _bandwidthExceeded;

        ReportLostUnreliableReceipts(nowUs);

        if (hasDataToSendOrResend)
            SendQueuedMessages(nowUs, needsBAndAs, isContinuousSend);
    }

    public bool TryDequeueDatagram([NotNullWhen(true)] out byte[]? datagram) => _datagrams.TryDequeue(out datagram);

    public bool TryDequeueEvent([NotNullWhen(true)] out SessionEvent? sessionEvent) => _events.TryDequeue(out sessionEvent);

    /// <summary>
    /// AckTimeout (ReliabilityLayer.cpp:3428-3433): nothing has arrived for longer than <see cref="TimeoutMs"/>.
    /// </summary>
    public bool AckTimeout(long nowMs) => nowMs - _timeLastDatagramArrivedMs > TimeoutMs;

    /// <summary>KillConnection: the connection is dead from now on.</summary>
    public void Kill() => Die();

    private void Die()
    {
        if (IsDead)
            return;
        IsDead = true;
        _events.Enqueue(new ConnectionDied());
    }

    private void ReceiveData(DatagramHeader header, ReadOnlyMemory<byte> body, long nowUs)
    {
        if (!_window.OnGotPacket(header.DatagramNumber, nowUs, out _))
            return;
        // The skipped count from OnGotPacket becomes NAKs (ReliabilityLayer.cpp:871-877) once resends exist.

        // Every data datagram is acknowledged, reliable or not (ReliabilityLayer.cpp:880-886).
        _acknowledgements.Add(header.DatagramNumber);

        var messages = new EncapsulatedMessageReader(body);
        while (messages.TryReadNext(out var message))
            ReceiveMessage(message);
    }

    // The body of the parse loop, ReliabilityLayer.cpp:897-1494.
    private void ReceiveMessage(EncapsulatedMessage message)
    {
        if (message.Reliability.IsReliable() && !_receivedMessageNumbers.TryAccept(message.ReliableMessageNumber))
            return;

        if (message.Split is not null)
            return; // Reassembly (InsertIntoSplitPacketList, ReliabilityLayer.cpp:1221-1242) is not implemented yet.

        _released.Clear();
        _orderingChannels.Receive(message, _released);
        foreach (var released in _released)
            _events.Enqueue(new MessageDelivered(released.Payload, released.PayloadBitLength));
    }

    // The ACK branch of HandleSocketReceiveFromConnectedPlayer, ReliabilityLayer.cpp:696-806.
    private bool ReceiveAcks(AckRangeList acks, long nowUs)
    {
        foreach (var range in acks.Ranges)
        {
            if (range.Max == UInt24.MaxValue)
                return false; // ReliabilityLayer.cpp:738-745; earlier ranges have been processed.

            // The C++ visits every number in the range; only numbers this layer could know about are visited here, in
            // the same ascending order, so a hostile range costs at most the history size.
            foreach (var datagramNumber in KnownDatagramNumbersIn(range))
                AcknowledgeDatagram(datagramNumber, nowUs);
        }
        return true;
    }

    private List<UInt24> KnownDatagramNumbersIn(AckRange range)
    {
        var numbers = new List<UInt24>();
        for (var i = 0; i < _datagramHistory.Count; i++)
        {
            var number = _datagramHistory.FirstNumber + (uint)i;
            if (range.Contains(number))
                numbers.Add(number);
        }
        foreach (var receipt in _unreliableReceipts)
        {
            if (range.Contains(receipt.DatagramNumber) && !numbers.Contains(receipt.DatagramNumber))
                numbers.Add(receipt.DatagramNumber);
        }
        numbers.Sort((a, b) => a.Value.CompareTo(b.Value));
        return numbers;
    }

    private void AcknowledgeDatagram(UInt24 datagramNumber, long nowUs)
    {
        // ReliabilityLayer.cpp:750-770.
        for (var k = 0; k < _unreliableReceipts.Count;)
        {
            if (_unreliableReceipts[k].DatagramNumber == datagramNumber)
            {
                _events.Enqueue(new ReceiptAcknowledged(_unreliableReceipts[k].Receipt));
                _unreliableReceipts.RemoveAt(k);
            }
            else
            {
                k++;
            }
        }

        if (!_datagramHistory.TryGet(datagramNumber, out var sentUs, out var messageNumbers))
            return;

        // RTT against this datagram's own send time (ReliabilityLayer.cpp:779-784).
        var rttUs = nowUs > sentUs ? nowUs - sentUs : 0;
        _window.OnAck(rttUs, _bandwidthExceeded, datagramNumber);
        foreach (var messageNumber in messageNumbers)
            RemoveFromResendList(messageNumber);
        _datagramHistory.Remove(datagramNumber);
    }

    // RemovePacketFromResendListAndDeleteOlderReliableSequenced, ReliabilityLayer.cpp:2437-2523.
    private void RemoveFromResendList(UInt24 messageNumber)
    {
        var slot = (int)(messageNumber.Value % ResendBufferLength);
        var message = _resendBuffer[slot];
        // May be asked to remove twice, for example after a resend and a second ack.
        if (message is null || message.ReliableMessageNumber != messageNumber)
            return;

        _resendBuffer[slot] = null;
        _resendList.Remove(message.ResendNode!);
        message.ResendNode = null;
        _unacknowledgedBytes -= message.TotalBytes;

        if (message.Reliability >= Reliability.ReliableWithAckReceipt)
            _events.Enqueue(new ReceiptAcknowledged(message.Receipt));
    }

    // SendACKs, ReliabilityLayer.cpp:3616-3656.
    private void SendAcks()
    {
        if (_acknowledgements.Count == 0)
            return;
        var ranges = AckRangeList.FromNumbers(_acknowledgements);
        _acknowledgements.Clear();
        while (ranges.Ranges.Count > 0)
        {
            var buffer = new byte[_mtu];
            var writer = new BitStreamWriter(buffer);
            // Sliding window never sends B and AS (CCRakNetSlidingWindow.cpp:259-266).
            DatagramHeader.ForAck().Write(ref writer);
            var written = ranges.Write(ref writer, MaxPayloadBits);
            ranges = ranges.Skip(written);
            _datagrams.Enqueue(writer.Written.ToArray());
            _window.OnSendAck();
        }
    }

    // ReliabilityLayer.cpp:1860-1881.
    private void ReportLostUnreliableReceipts(long nowUs)
    {
        for (var i = 0; i < _unreliableReceipts.Count;)
        {
            if (nowUs >= _unreliableReceipts[i].DeadlineUs)
            {
                _events.Enqueue(new ReceiptLost(_unreliableReceipts[i].Receipt));
                _unreliableReceipts.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    // The send half of Update, ReliabilityLayer.cpp:1883-2237, without resends.
    private void SendQueuedMessages(long nowUs, bool needsBAndAs, bool isContinuousSend)
    {
        var transmissionBandwidth = _window.GetTransmissionBandwidth(_unacknowledgedBytes, isContinuousSend);
        // Resends of messages whose next action time has passed go first here, limited by
        // SlidingWindow.GetRetransmissionBandwidth (ReliabilityLayer.cpp:1894-1974); not implemented yet.

        var datagrams = new List<List<OutgoingMessage>>();
        var maxBits = MaxPayloadBits;
        var allDatagramBits = 0;
        while (!ResendBufferOverflow() && allDatagramBits / 8 < transmissionBandwidth)
        {
            var datagram = new List<OutgoingMessage>();
            var datagramBits = 0;
            while (_outgoing.TryPeek(out var message))
            {
                // The fit test uses exact bits, the running total whole bytes (ReliabilityLayer.cpp:2018-2020, 3464).
                if (datagramBits + message.HeaderBits + message.BitLength > maxBits)
                    break; // Hit the MTU.
                var bits = message.TotalBytes * 8;

                _outgoing.Dequeue();
                var datagramNumber = _window.NextDatagramNumber + (uint)datagrams.Count;
                if (message.Reliability.IsReliable())
                {
                    message.ReliableMessageNumber = _nextReliableMessageNumber++;
                    message.NextActionTimeUs = _window.GetRtoForRetransmission() + nowUs;
                    _resendBuffer[(int)(message.ReliableMessageNumber.Value % ResendBufferLength)] = message;
                    message.ResendNode = _resendList.AddLast(message);
                    _unacknowledgedBytes += message.TotalBytes;
                }
                else if (message.Reliability == Reliability.UnreliableWithAckReceipt)
                {
                    _unreliableReceipts.Add(new UnreliableReceipt(datagramNumber, message.Receipt, _window.GetRtoForRetransmission() + nowUs));
                }

                message.TimesSent++;
                datagram.Add(message);
                datagramBits += bits;
                allDatagramBits += bits;
                if (ResendBufferOverflow())
                    break;
            }

            if (datagramBits == 0)
                break;
            datagrams.Add(datagram);
        }

        for (var i = 0; i < datagrams.Count; i++)
            WriteDatagram(datagrams[i], nowUs, needsBAndAs, isContinuousSend || i > 0);

        _bandwidthExceeded = _outgoing.Count > 0;
    }

    // ReliabilityLayer.cpp:2140-2213.
    private void WriteDatagram(List<OutgoingMessage> messages, long nowUs, bool needsBAndAs, bool isContinuousSend)
    {
        var datagramNumber = _window.TakeNextDatagramNumber();
        var buffer = new byte[_mtu];
        var writer = new BitStreamWriter(buffer);
        DatagramHeader.ForData(datagramNumber, isPacketPair: false, isContinuousSend, needsBAndAs).Write(ref writer);

        var messageNumbers = new List<UInt24>();
        foreach (var message in messages)
        {
            if (message.Reliability is not (Reliability.Unreliable or Reliability.UnreliableSequenced))
                messageNumbers.Add(message.ReliableMessageNumber);
            message.ToEncapsulated().Write(ref writer);
        }

        _datagramHistory.Add(nowUs, messageNumbers);
        _datagrams.Enqueue(writer.Written.ToArray());
    }

    // ResendBufferOverflow, ReliabilityLayer.cpp:3758-3765.
    private bool ResendBufferOverflow() =>
        _resendBuffer[(int)(_nextReliableMessageNumber.Value % ResendBufferLength)] is not null;

    private readonly record struct UnreliableReceipt(UInt24 DatagramNumber, uint Receipt, long DeadlineUs);

    /// <summary>The send-side InternalPacket.</summary>
    private sealed class OutgoingMessage(byte[] data, int bitLength, Reliability reliability, Priority priority, uint receipt, long creationTimeUs)
    {
        public byte[] Data { get; } = data;

        public int BitLength { get; } = bitLength;

        public Reliability Reliability { get; } = reliability;

        public Priority Priority { get; } = priority;

        public uint Receipt { get; } = receipt;

        public long CreationTimeUs { get; } = creationTimeUs;

        public byte OrderingChannel { get; set; }

        public UInt24 OrderingIndex { get; set; }

        public UInt24 SequencingIndex { get; set; }

        public UInt24 ReliableMessageNumber { get; set; } = UnassignedMessageNumber;

        public long NextActionTimeUs { get; set; }

        public int TimesSent { get; set; }

        public LinkedListNode<OutgoingMessage>? ResendNode { get; set; }

        /// <summary>GetMessageHeaderLengthBits (ReliabilityLayer.cpp:2555-2605).</summary>
        public int HeaderBits => EncapsulatedMessage.GetHeaderLengthBits(Reliability, isSplit: false);

        /// <summary>Header plus data, each rounded up to whole bytes (PushPacket, ReliabilityLayer.cpp:3464).</summary>
        public int TotalBytes => (BitLength + 7) / 8 + (HeaderBits + 7) / 8;

        public EncapsulatedMessage ToEncapsulated() =>
            new(
                Reliability,
                Data,
                BitLength,
                ReliableMessageNumber,
                SequencingIndex,
                OrderingIndex,
                OrderingChannel
            );
    }
}
