using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
/// Inputs: <see cref="Send(ReadOnlySpan{byte}, int, Reliability, Priority, byte, uint, long)"/> queues a message,
/// <see cref="Receive"/> handles one datagram from the remote system (HandleSocketReceiveFromConnectedPlayer), and
/// <see cref="Update"/> runs one tick: it discards unreliable messages past the unreliable timeout, sends due
/// acknowledgements and NAKs, resends reliable messages whose retransmission time has come, then packs queued messages
/// into datagrams as the congestion window and the bandwidth limit allow. Outputs: <see cref="TryDequeueDatagram(Span{byte}, out int)"/>
/// and <see cref="TryDequeueEvent"/>.
/// </para>
/// <para>
/// Loss recovery follows the C++ exactly. Every sent reliable message joins the tail of the resend list with a next
/// action time one RTO ahead (<see cref="SlidingWindow.GetRtoForRetransmission"/>). Each update resends from the head
/// while the head is due, putting each resent message back at the tail with a fresh RTO. A NAK makes the messages of
/// the NAKed datagrams due at once, but they still wait for their turn at the head. A receiver NAKs every datagram
/// number it skipped (<see cref="SlidingWindow.OnGotPacket"/>). Resends and NAKs feed the sliding window's backoff.
/// </para>
/// <para>
/// A message too large for one datagram is split into parts (SplitPacket) that share its ordering and sequencing
/// indexes and are queued one by one, so they interleave with other messages by priority. Every part is reliable, as
/// RakNet forces. Received parts are held by <see cref="SplitMessageAssembler"/> until the message is whole; it then
/// passes through ordering like any other message, and acknowledgements go out at once.
/// </para>
/// <para>
/// Receiving is bounded by <see cref="SessionLimits"/>, which RakNet does not have: parts per split message, bytes per
/// split message, incomplete split messages at once, time without a new part, messages waiting per ordering channel,
/// and the bytes all waiting messages and parts hold together. Exceeding one closes the connection
/// (<see cref="ConnectionDied"/> with its <see cref="ConnectionDeathCause"/>), except an UNRELIABLE_SEQUENCED message
/// that finds no room to wait, which is dropped. Pending NAK ranges stop at
/// <see cref="SessionLimits.MaxPendingNakRanges"/>. A closed layer ignores everything it receives.
/// </para>
/// </remarks>
internal sealed class ReliabilityLayer
{
    /// <summary>defaultTimeoutTime of a release build, RakPeer.cpp:247-250.</summary>
    public const long DefaultTimeoutMs = 10_000;

    /// <summary>RakPeer's unreliableTimeout (RakPeer.cpp:238), which it gives every connection (RakPeer.cpp:3647).</summary>
    public const long RakPeerUnreliableTimeoutMs = 1_000;

    /// <summary>RESEND_BUFFER_ARRAY_LENGTH, RakNetDefines.h:105.</summary>
    private const int ResendBufferLength = 512;

    private const int ChannelCount = OrderingChannels.ChannelCount;
    private const int PriorityCount = 4;
    private const int MetricCount = 7;

    // ReliabilityLayer.cpp:1741-1742.
    private const long MaxTimeSinceLastTickUs = 100_000;

    // ReliabilityLayer.cpp:1844-1850.
    private const long BpsClearIntervalUs = 100_000;

    // AllocateFromInternalPacketPool sets reliableMessageNumber to (uint32_t)-1 (ReliabilityLayer.cpp:3678).
    private static readonly UInt24 UnassignedMessageNumber = UInt24.MaxValue;

    // The most free messages and delivery events kept for reuse; beyond it they are left to the collector.
    private const int MaxPooled = 2 * ResendBufferLength;

    private static readonly Comparison<UInt24> ByValue = static (a, b) => a.Value.CompareTo(b.Value);

    private readonly int _mtu;
    private readonly SessionLimits _limits;
    private readonly long _connectionStartTimeUs;
    private readonly SlidingWindow _window;

    // Sending.
    private readonly UInt24[] _orderedWriteIndex = new UInt24[ChannelCount];
    private readonly UInt24[] _sequencedWriteIndex = new UInt24[ChannelCount];
    private readonly SendQueue<OutgoingMessage> _outgoing = new();
    private readonly LinkedList<OutgoingMessage> _unreliable = new();
    private readonly OutgoingMessage?[] _resendBuffer = new OutgoingMessage?[ResendBufferLength];
    private readonly LinkedList<OutgoingMessage> _resendList = new();
    private readonly DatagramHistory _datagramHistory = new();
    private readonly List<UnreliableReceipt> _unreliableReceipts = [];
    private readonly Stack<OutgoingMessage> _freeMessages = new();
    private readonly List<UInt24> _datagramMessageNumbers = [];
    private UInt24 _nextReliableMessageNumber;
    private ushort _nextSplitId;
    private int _unacknowledgedBytes;
    private bool _bandwidthExceeded;
    private long _lastUpdateUs;
    private long _unreliableTimeoutUs;
    private long _timeToNextUnreliableCullUs;

    // The datagrams being filled this update: packetsToSendThisUpdate, packetsToSendThisUpdateDatagramBoundaries,
    // datagramSizeSoFar and allDatagramSizesSoFar (ReliabilityLayer.cpp:3452-3506).
    private readonly List<OutgoingMessage> _packetsThisUpdate = [];
    private readonly List<int> _datagramBoundaries = [];
    private int _datagramBits;
    private int _allDatagramBits;

    // Receiving.
    private readonly RangeList _acknowledgements = new();
    private readonly RangeList _naks = new();
    private readonly ReceivedMessageNumbers _receivedMessageNumbers = new();
    private readonly OrderingChannels _orderingChannels;
    private readonly SplitMessageAssembler _splitMessages;
    private readonly List<ReceivedMessage> _released = [];
    private readonly List<AckRange> _receivedRanges = [];
    private readonly List<UInt24> _knownDatagramNumbers = [];
    private readonly Stack<MessageDelivered> _freeDeliveries = new();
    private readonly Stack<ReceiptAcknowledged> _freeReceipts = new();
    private byte[] _receiveBuffer = [];
    private long _timeLastDatagramArrivedMs;

    // Statistics.
    private readonly BpsTracker[] _metrics = new BpsTracker[MetricCount];
    private readonly uint[] _messagesInSendBuffer = new uint[PriorityCount];
    private readonly double[] _bytesInSendBuffer = new double[PriorityCount];
    private ulong _bytesInResendBuffer;
    private long _lastBpsClearUs;
    private bool _isLimitedByCongestionControl;
    private bool _isLimitedByOutgoingBandwidthLimit;
    private ulong _bpsLimitByOutgoingBandwidthLimit;

    // Outputs.
    private readonly DatagramQueue<ValueTuple> _datagrams = new();
    private readonly Queue<SessionEvent> _events = new();

    /// <summary>ReliabilityLayer::Reset(true, MTUSize, false) (ReliabilityLayer.cpp:345-363).</summary>
    /// <param name="mtu">The negotiated MTU including the 28-byte IP and UDP header.</param>
    /// <param name="nowUs">The current time.</param>
    /// <param name="limits">The receive limits; <see cref="SessionLimits.Default"/> when null.</param>
    public ReliabilityLayer(int mtu, long nowUs, SessionLimits? limits = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mtu, DatagramLimits.MinimumMtuSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(mtu, DatagramLimits.MaximumMtuSize);
        _mtu = mtu;
        _limits = (limits ?? SessionLimits.Default).Validated();
        _orderingChannels = new OrderingChannels(_limits.MaxOrderingBufferMessagesPerChannel);
        _splitMessages = new SplitMessageAssembler(_limits);
        _window = new SlidingWindow(mtu - DatagramLimits.UdpHeaderBytes);
        _connectionStartTimeUs = nowUs;
        _lastUpdateUs = nowUs;
        _timeLastDatagramArrivedMs = nowUs / 1000;
        for (var i = 0; i < MetricCount; i++)
            _metrics[i] = new BpsTracker();
    }

    /// <summary>The receive limits this connection enforces.</summary>
    public SessionLimits Limits => _limits;

    /// <summary>SetTimeoutTime: how long reliable messages may go unacknowledged with nothing heard.</summary>
    public long TimeoutMs { get; set; } = DefaultTimeoutMs;

    /// <summary>
    /// SetUnreliableTimeout (ReliabilityLayer.cpp:2383-2390): unreliable messages still queued this long after they
    /// were sent are discarded. 0, the layer's own default (ReliabilityLayer.cpp:404), keeps them until sent.
    /// </summary>
    public long UnreliableTimeoutMs
    {
        get => _unreliableTimeoutUs / 1000;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _unreliableTimeoutUs = value * 1000;
        }
    }

    /// <summary>
    /// SetSplitMessageProgressInterval (ReliabilityLayer.cpp:2378-2381, RakPeer.cpp:3646): while a split message is
    /// arriving, an ID_DOWNLOAD_PROGRESS message is delivered every this many parts. 0, the default, never.
    /// </summary>
    public int SplitMessageProgressInterval
    {
        get => _splitMessages.ProgressInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _splitMessages.ProgressInterval = value;
        }
    }

    /// <summary>Split messages with some but not all of their parts received.</summary>
    public int IncompleteSplitMessages => _splitMessages.IncompleteCount;

    /// <summary>
    /// The bytes held in incomplete split messages and ordering buffers, as charged against
    /// <see cref="SessionLimits.MaxReceiveBufferBytes"/>.
    /// </summary>
    public long BufferedReceiveBytes => _splitMessages.BufferedBytes + _orderingChannels.BufferedBytes;

    /// <summary>Disjoint ranges of datagram numbers waiting to be NAKed.</summary>
    public int PendingNakRanges => _naks.Count;

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

    // The largest message sent whole, and the size of every split part but the last: the datagram budget less the
    // largest message header (ReliabilityLayer.cpp:1604, 2939).
    private int MaxBlockBytes => MaxPayloadBits / 8 - BitsToBytes(EncapsulatedMessage.MaxHeaderLengthBits);

    /// <summary>A message to send. The bit length is the whole payload.</summary>
    public bool Send(ReadOnlySpan<byte> data, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs) =>
        Send(data, data.Length * 8, reliability, priority, channel, receipt, nowUs);

    /// <summary>
    /// ReliabilityLayer::Send (ReliabilityLayer.cpp:1539-1683): copies the message, assigns its ordering and sequencing
    /// indexes and queues it, split into parts when it does not fit one datagram. Returns false for an empty message.
    /// </summary>
    /// <param name="data">The message, at least <c>ceil(bitLength / 8)</c> bytes.</param>
    /// <param name="bitLength">The message length in bits.</param>
    /// <param name="reliability">How it is delivered.</param>
    /// <param name="priority">Which goes first when several are queued.</param>
    /// <param name="channel">The ordering channel; out-of-range channels become 0, as in the C++.</param>
    /// <param name="receipt">The serial reported by <see cref="ReceiptAcknowledged"/> or <see cref="ReceiptLost"/>.</param>
    /// <param name="nowUs">The current time.</param>
    public bool Send(ReadOnlySpan<byte> data, int bitLength, Reliability reliability, Priority priority, byte channel, uint receipt, long nowUs)
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
        var byteLength = BitsToBytes(bitLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(data.Length, byteLength, nameof(data));

        _metrics[(int)SessionMetric.UserMessageBytesPushed].Push(nowUs, (ulong)byteLength);

        // ReliabilityLayer.cpp:1604-1606.
        var needsSplitting = byteLength > MaxBlockBytes;
        if (needsSplitting)
        {
            // "Split packets cannot be unreliable, in case that one part doesn't arrive and the whole cannot be
            // reassembled" (ReliabilityLayer.cpp:1609-1622).
            reliability = reliability switch
            {
                Reliability.Unreliable => Reliability.Reliable,
                Reliability.UnreliableWithAckReceipt => Reliability.ReliableWithAckReceipt,
                Reliability.UnreliableSequenced => Reliability.ReliableSequenced,
                _ => reliability,
            };
        }

        var message = RentMessage();
        // A split message's parts share one copy of it, which lives as long as they do.
        if (needsSplitting)
            message.Reference(data[..byteLength].ToArray());
        else
            message.CopyFrom(data[..byteLength]);
        message.Set(bitLength, reliability, priority, receipt, nowUs);
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

        if (needsSplitting)
        {
            SplitAndQueue(message);
            ReleaseMessage(message);
            return true;
        }

        // AddToUnreliableLinkedList, ReliabilityLayer.cpp:3715-3736.
        if (message.IsUnreliable)
            _unreliable.AddLast(message.UnreliableNode);
        Queue(message);
        return true;
    }

    // SplitPacket, ReliabilityLayer.cpp:2928-3033: parts of maximumSendBlockBytes, the last one shorter, each queued with
    // its own weight so that other messages interleave with them.
    private void SplitAndQueue(OutgoingMessage message)
    {
        var blockBytes = MaxBlockBytes;
        var byteLength = message.Data.Length;
        var count = (uint)((byteLength - 1) / blockBytes + 1);
        var id = _nextSplitId++; // "It's ok if this wraps to 0" (ReliabilityLayer.cpp:3001).
        for (var index = 0u; index < count; index++)
        {
            var offset = (int)index * blockBytes;
            var bytesToSend = Math.Min(byteLength - offset, blockBytes);
            // ReliabilityLayer.cpp:2990-2993: a full block is whole bytes even when it ends the message.
            var bitLength = bytesToSend != blockBytes ? message.BitLength - offset * 8 : bytesToSend * 8;
            var part = RentMessage();
            part.Reference(message.Data.Slice(offset, bytesToSend));
            part.Set(bitLength, message.Reliability, message.Priority, message.Receipt, message.CreationTimeUs);
            part.OrderingChannel = message.OrderingChannel;
            part.OrderingIndex = message.OrderingIndex;
            part.SequencingIndex = message.SequencingIndex;
            part.Split = new SplitHeader(count, id, index);
            // Parts are reliable, so none joins the unreliable list (ReliabilityLayer.cpp:3015).
            Queue(part);
        }
    }

    private void Queue(OutgoingMessage message)
    {
        _outgoing.Enqueue(message, message.Priority);
        _messagesInSendBuffer[(int)message.Priority]++;
        _bytesInSendBuffer[(int)message.Priority] += BitsToBytes(message.BitLength);
    }

    /// <summary>
    /// HandleSocketReceiveFromConnectedPlayer (ReliabilityLayer.cpp:635-1502). Returns false where the C++ does: an
    /// ACK or NAK whose range list is malformed or unacceptable.
    /// </summary>
    public bool Receive(ReadOnlySpan<byte> datagram, long nowUs)
    {
        if (IsDead)
            return true; // Closed: nothing more is processed.

        _metrics[(int)SessionMetric.ActualBytesReceived].Push(nowUs, (ulong)datagram.Length);

        // A length of 1 or 2 is a connection request resend that is ignored (ReliabilityLayer.cpp:653).
        if (datagram.Length <= 2)
            return true;
        _timeLastDatagramArrivedMs = nowUs / 1000;

        var reader = new BitStreamReader(datagram);
        if (!DatagramHeader.TryRead(ref reader, out var header))
            return true; // Not a reliability-layer datagram (ReliabilityLayer.cpp:689-695).

        switch (header.Kind)
        {
            case DatagramKind.Ack:
                return AckRangeList.TryRead(ref reader, _receivedRanges) && ReceiveAcks(_receivedRanges, nowUs);
            case DatagramKind.Nak:
                if (!AckRangeList.TryRead(ref reader, _receivedRanges))
                    return false; // ReliabilityLayer.cpp:811-817.
                ReceiveNaks(_receivedRanges, nowUs);
                return true;
            default:
                ReceiveData(header, CopyToReceiveBuffer(datagram[(reader.BitPosition >> 3)..]), nowUs);
                return true;
        }
    }

    /// <summary>
    /// One tick of ReliabilityLayer::Update (ReliabilityLayer.cpp:1687-2242): discard stale unreliable messages, kill
    /// the connection on an ack timeout, send due acknowledgements and NAKs, report lost unreliable receipts, then
    /// resend due reliable messages and fill datagrams with queued messages while the window allows.
    /// </summary>
    /// <param name="nowUs">The current time.</param>
    /// <param name="bitsPerSecondLimit">
    /// RakPeer's maxOutgoingBPS (SetPerConnectionOutgoingBandwidthLimit, RakPeer.cpp:2961-2964): new user data stops
    /// for the tick once more than this many bits' worth was sent in the last second. 0 is unlimited. Resends and
    /// acknowledgements are not limited.
    /// </param>
    public void Update(long nowUs, uint bitsPerSecondLimit = 0)
    {
        if (IsDead)
            return;

        if (_splitMessages.HasTimedOut(nowUs))
        {
            Die(ConnectionDeathCause.SplitReassemblyTimedOut);
            return;
        }

        // "This line is necessary because the timer isn't accurate" (ReliabilityLayer.cpp:1727-1733).
        if (nowUs <= _lastUpdateUs)
        {
            _lastUpdateUs = nowUs;
            return;
        }
        var timeSinceLastTickUs = Math.Min(nowUs - _lastUpdateUs, MaxTimeSinceLastTickUs);
        _lastUpdateUs = nowUs;

        CullStaleUnreliableMessages(nowUs, timeSinceLastTickUs);

        if (MessagesInResendBuffer != 0 && AckTimeout(nowUs / 1000))
        {
            Die(ConnectionDeathCause.AckTimeout);
            return;
        }

        if (_window.ShouldSendAcks(nowUs))
            SendAcks(nowUs);

        if (_naks.Count > 0)
            SendNak(nowUs);

        var needsBAndAs = _window.IsInSlowStart;
        var isContinuousSend = _bandwidthExceeded;
        _bandwidthExceeded = _outgoing.Count > 0;
        var hasDataToSendOrResend = _resendList.Count > 0 || _bandwidthExceeded;

        _bpsLimitByOutgoingBandwidthLimit = BitsToBytes(bitsPerSecondLimit);

        // ReliabilityLayer.cpp:1844-1858.
        if (nowUs > _lastBpsClearUs + BpsClearIntervalUs)
        {
            foreach (var metric in _metrics)
                metric.ClearExpired(nowUs);
            _lastBpsClearUs = nowUs;
        }

        ReportLostUnreliableReceipts(nowUs);

        if (hasDataToSendOrResend)
            SendQueuedMessages(nowUs, bitsPerSecondLimit, needsBAndAs, isContinuousSend);
    }

    /// <summary>The next datagram to send, as a new array.</summary>
    public bool TryDequeueDatagram([NotNullWhen(true)] out byte[]? datagram)
    {
        datagram = _datagrams.TryDequeue(out var bytes, out _) ? bytes.ToArray() : null;
        return datagram is not null;
    }

    /// <summary>
    /// The next datagram to send, copied into <paramref name="destination"/>, which holds at least <see cref="Mtu"/>
    /// bytes. Allocates nothing.
    /// </summary>
    public bool TryDequeueDatagram(Span<byte> destination, out int length)
    {
        if (!_datagrams.TryDequeue(out var datagram, out _))
        {
            length = 0;
            return false;
        }
        datagram.CopyTo(destination);
        length = datagram.Length;
        return true;
    }

    /// <summary>
    /// Hands back a delivery event the caller is done with, payload included, so a later delivery can reuse it instead
    /// of allocating.
    /// </summary>
    public void Recycle(MessageDelivered delivered)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        if (_freeDeliveries.Count < MaxPooled)
            _freeDeliveries.Push(delivered);
    }

    /// <summary>Hands back a receipt event the caller is done with, so a later receipt can reuse it.</summary>
    public void Recycle(ReceiptAcknowledged acknowledged)
    {
        ArgumentNullException.ThrowIfNull(acknowledged);
        if (_freeReceipts.Count < MaxPooled)
            _freeReceipts.Push(acknowledged);
    }

    public bool TryDequeueEvent([NotNullWhen(true)] out SessionEvent? sessionEvent) => _events.TryDequeue(out sessionEvent);

    /// <summary>
    /// AckTimeout (ReliabilityLayer.cpp:3428-3433): nothing has arrived for longer than <see cref="TimeoutMs"/>.
    /// </summary>
    public bool AckTimeout(long nowMs) => nowMs - _timeLastDatagramArrivedMs > TimeoutMs;

    /// <summary>KillConnection: the connection is dead from now on.</summary>
    public void Kill() => Die(ConnectionDeathCause.Killed);

    /// <summary>GetStatistics (ReliabilityLayer.cpp:3378-3415), as a new snapshot.</summary>
    public SessionStatistics GetStatistics(long nowUs)
    {
        _ = nowUs; // GetBPS1Threadsafe ignores the time (ReliabilityLayer.h:92).
        var statistics = new SessionStatistics();
        CopyStatistics(statistics);
        return statistics;
    }

    /// <summary>GetStatistics (ReliabilityLayer.cpp:3378-3415), overwriting <paramref name="target"/> without allocating.</summary>
    public void CopyStatistics(SessionStatistics target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var lastSecond = target.ValueOverLastSecond;
        var total = target.RunningTotal;
        for (var i = 0; i < MetricCount; i++)
        {
            lastSecond[i] = _metrics[i].LastSecond;
            total[i] = _metrics[i].Total;
        }

        var sentLastSecond = lastSecond[(int)SessionMetric.UserMessageBytesSent];
        var resentLastSecond = lastSecond[(int)SessionMetric.UserMessageBytesResent];
        target.PacketLossLastSecond = sentLastSecond + resentLastSecond > 0
            ? (float)(resentLastSecond / ((double)sentLastSecond + resentLastSecond))
            : 0f;

        // ReliabilityLayer.cpp:3398-3407, integer division and all: the total is nonzero only when nothing was resent,
        // and then it is zero.
        var lossTotal = 0f;
        var sentTotal = total[(int)SessionMetric.UserMessageBytesSent];
        var resentTotal = total[(int)SessionMetric.UserMessageBytesResent];
        var denominator = sentTotal + resentTotal;
        if (denominator != 0 && sentTotal / denominator > 0)
            lossTotal = (float)(resentTotal / ((double)sentTotal + resentTotal));
        target.PacketLossTotal = lossTotal;

        target.ConnectionStartTimeUs = _connectionStartTimeUs;
        target.IsLimitedByCongestionControl = _isLimitedByCongestionControl;
        target.BpsLimitByCongestionControl = 0;
        target.IsLimitedByOutgoingBandwidthLimit = _isLimitedByOutgoingBandwidthLimit;
        target.BpsLimitByOutgoingBandwidthLimit = _bpsLimitByOutgoingBandwidthLimit;
        _messagesInSendBuffer.CopyTo(target.MessagesInSendBuffer, 0);
        _bytesInSendBuffer.CopyTo(target.BytesInSendBuffer, 0);
        target.MessagesInResendBuffer = (uint)MessagesInResendBuffer;
        target.BytesInResendBuffer = _bytesInResendBuffer;
    }

    private void Die(ConnectionDeathCause cause)
    {
        if (IsDead)
            return;
        IsDead = true;
        // Nothing buffered can be delivered any more.
        _splitMessages.Clear();
        _orderingChannels.Clear();
        _events.Enqueue(new ConnectionDied(cause));
    }

    // ReliabilityLayer.cpp:1745-1793. Messages are culled from the front of the unreliable list, which is in send
    // order, stopping at the first that is young enough. A culled message stays queued with no data until the send
    // loop pops it (ReliabilityLayer.cpp:2007-2016).
    private void CullStaleUnreliableMessages(long nowUs, long timeSinceLastTickUs)
    {
        if (_unreliableTimeoutUs <= 0)
            return;
        if (timeSinceLastTickUs < _timeToNextUnreliableCullUs)
        {
            _timeToNextUnreliableCullUs -= timeSinceLastTickUs;
            return;
        }

        while (_unreliable.First is { } node && nowUs > node.Value.CreationTimeUs + _unreliableTimeoutUs)
        {
            var message = node.Value;
            _unreliable.RemoveFirst();
            message.IsDiscarded = true;
            if (message.Reliability == Reliability.UnreliableWithAckReceipt)
                _events.Enqueue(new ReceiptDiscarded(message.Receipt));
        }
        _timeToNextUnreliableCullUs = _unreliableTimeoutUs / 2;
    }

    // Copies a received datagram body into a buffer reused from datagram to datagram, so messages can be read as memory.
    // Nothing keeps a slice of it past the datagram: ordering and split reassembly copy what they hold, and delivered
    // payloads are copied into their events.
    private ReadOnlyMemory<byte> CopyToReceiveBuffer(ReadOnlySpan<byte> body)
    {
        if (_receiveBuffer.Length < body.Length)
            _receiveBuffer = new byte[Math.Max(body.Length, _mtu)];
        body.CopyTo(_receiveBuffer);
        return _receiveBuffer.AsMemory(0, body.Length);
    }

    private void ReceiveData(DatagramHeader header, ReadOnlyMemory<byte> body, long nowUs)
    {
        if (!_window.OnGotPacket(header.DatagramNumber, nowUs, out var skippedMessageCount))
            return;

        // NAK every datagram number jumped over (ReliabilityLayer.cpp:871-877).
        // Past the limit, numbers are left to the retransmission timeout.
        for (var offset = skippedMessageCount; offset > 0 && _naks.Count < SessionLimits.MaxPendingNakRanges; offset--)
            _naks.Insert(header.DatagramNumber - offset);

        // Every data datagram is acknowledged, reliable or not (ReliabilityLayer.cpp:880-886).
        _acknowledgements.Insert(header.DatagramNumber);

        var messages = new EncapsulatedMessageReader(body);
        while (!IsDead && messages.TryReadNext(out var message, out var payload))
            ReceiveMessage(message, payload, nowUs);
    }

    // The body of the parse loop, ReliabilityLayer.cpp:897-1494.
    private void ReceiveMessage(in EncapsulatedMessage.Header message, ReadOnlyMemory<byte> payload, long nowUs)
    {
        if (message.Reliability.IsReliable() && !_receivedMessageNumbers.TryAccept(message.ReliableMessageNumber))
        {
            // A duplicate, ReliabilityLayer.cpp:959-999.
            _metrics[(int)SessionMetric.UserMessageBytesReceivedIgnored].Push(nowUs, (ulong)BitsToBytes(message.BitLength));
            return;
        }

        ReceivedMessage received;
        if (message.Split is null)
        {
            received = ReceivedMessage.From(message, payload);
        }
        else
        {
            // ReliabilityLayer.cpp:1221-1242.
            var available = _limits.MaxReceiveBufferBytes - BufferedReceiveBytes;
            switch (_splitMessages.TryInsert(message.ToMessage(payload), nowUs, available, out var progress, out var complete))
            {
                case SplitInsertResult.Accepted:
                    break;
                case SplitInsertResult.Ignored:
                    _metrics[(int)SessionMetric.UserMessageBytesReceivedIgnored].Push(nowUs, (ulong)BitsToBytes(message.BitLength));
                    return;
                case SplitInsertResult.SplitCountExceeded:
                    Die(ConnectionDeathCause.SplitCountExceeded);
                    return;
                case SplitInsertResult.SplitMessageTooLarge:
                    Die(ConnectionDeathCause.SplitMessageTooLarge);
                    return;
                case SplitInsertResult.TooManyConcurrentSplits:
                    Die(ConnectionDeathCause.TooManyConcurrentSplits);
                    return;
                default:
                    Die(ConnectionDeathCause.ReceiveBufferFull);
                    return;
            }
            // Progress goes straight to the output queue, ahead of ordering (ReliabilityLayer.cpp:3171).
            if (progress is not null)
                _events.Enqueue(new MessageDelivered(progress, progress.Length * 8));
            if (complete is not { } whole)
                return; // Don't have all the parts yet.
            // "Ack immediately, because for large files this can take a long time" (ReliabilityLayer.cpp:3243-3244).
            SendAcks(nowUs);
            received = whole;
        }

        _released.Clear();
        var ordering = _orderingChannels.Receive(received, _released, _limits.MaxReceiveBufferBytes - BufferedReceiveBytes);
        if (ordering != OrderingResult.Accepted)
        {
            // An unreliable message may be lost anyway; a reliable one was acknowledged with its datagram and cannot be.
            if (received.Reliability == Reliability.UnreliableSequenced)
            {
                _metrics[(int)SessionMetric.UserMessageBytesReceivedIgnored].Push(nowUs, (ulong)BitsToBytes(received.BitLength));
                return;
            }
            Die(ordering == OrderingResult.ChannelFull ? ConnectionDeathCause.OrderingBufferFull : ConnectionDeathCause.ReceiveBufferFull);
            return;
        }
        foreach (var released in _released)
        {
            // Counted as each message reaches the output queue (ReliabilityLayer.cpp:1346, 1412, 1481).
            _metrics[(int)SessionMetric.UserMessageBytesReceivedProcessed].Push(nowUs, (ulong)BitsToBytes(released.BitLength));
            _events.Enqueue(Deliver(released.Payload, released.BitLength));
        }
    }

    private ReceiptAcknowledged Acknowledged(uint receipt)
    {
        if (!_freeReceipts.TryPop(out var acknowledged))
            return new ReceiptAcknowledged(receipt);
        acknowledged.Set(receipt);
        return acknowledged;
    }

    // A delivery event for the payload, reused when one was recycled. A payload in the receive buffer is copied; one the
    // session made for itself (reassembled, or held for ordering) is not written to again and is passed on as it is.
    private MessageDelivered Deliver(ReadOnlyMemory<byte> payload, int bitLength)
    {
        var delivered = _freeDeliveries.TryPop(out var free) ? free : new MessageDelivered(default, 0);
        if (MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array == _receiveBuffer)
            delivered.CopyFrom(payload.Span, bitLength);
        else
            delivered.Reference(payload, bitLength);
        return delivered;
    }

    // The ACK branch of HandleSocketReceiveFromConnectedPlayer, ReliabilityLayer.cpp:696-806.
    private bool ReceiveAcks(List<AckRange> acks, long nowUs)
    {
        foreach (var range in acks)
        {
            if (range.Max == UInt24.MaxValue)
                return false; // ReliabilityLayer.cpp:738-745; earlier ranges have been processed.

            // The C++ visits every number in the range; only numbers this layer could know about are visited here, in
            // the same ascending order, so a hostile range costs at most the history size.
            foreach (var datagramNumber in KnownDatagramNumbersIn(range, includeUnreliableReceipts: true))
                AcknowledgeDatagram(datagramNumber, nowUs);
        }
        return true;
    }

    // The NAK branch of HandleSocketReceiveFromConnectedPlayer, ReliabilityLayer.cpp:807-857.
    private void ReceiveNaks(List<AckRange> naks, long nowUs)
    {
        foreach (var range in naks)
        {
            // The C++ calls OnNAK once per number in the range (line 833). It only sets ssThresh from cwnd, which
            // nothing in between changes, so once per range is the same.
            _window.OnNak();

            foreach (var datagramNumber in KnownDatagramNumbersIn(range, includeUnreliableReceipts: false))
            {
                if (!_datagramHistory.TryGet(datagramNumber, out _, out var messageNumbers))
                    continue;
                foreach (var messageNumber in messageNumbers)
                {
                    // Update timers so resends occur immediately (lines 843-851). The slot is not checked against the
                    // message number, as in the C++.
                    var message = _resendBuffer[(int)(messageNumber.Value % ResendBufferLength)];
                    if (message is not null && message.NextActionTimeUs != 0)
                        message.NextActionTimeUs = nowUs;
                }
            }
        }
    }

    // The numbers in the range that the history or an unreliable receipt holds, ascending. The list is reused, so it is
    // valid until the next call.
    private List<UInt24> KnownDatagramNumbersIn(AckRange range, bool includeUnreliableReceipts)
    {
        var numbers = _knownDatagramNumbers;
        numbers.Clear();
        // Whichever is shorter is walked: the range, in ascending order already, or the history, in sending order.
        var sorted = range.Count <= (uint)_datagramHistory.Count;
        if (sorted)
        {
            for (var n = range.Min.Value; n <= range.Max.Value; n++)
            {
                var number = new UInt24(n);
                if (_datagramHistory.Holds(number))
                    numbers.Add(number);
            }
        }
        else
        {
            for (var i = 0; i < _datagramHistory.Count; i++)
            {
                var number = _datagramHistory.FirstNumber + (uint)i;
                if (range.Contains(number))
                    numbers.Add(number);
            }
        }
        if (includeUnreliableReceipts)
        {
            foreach (var receipt in _unreliableReceipts)
            {
                if (range.Contains(receipt.DatagramNumber) && !numbers.Contains(receipt.DatagramNumber))
                {
                    numbers.Add(receipt.DatagramNumber);
                    sorted = false;
                }
            }
        }
        if (!sorted)
            numbers.Sort(ByValue);
        return numbers;
    }

    private void AcknowledgeDatagram(UInt24 datagramNumber, long nowUs)
    {
        // ReliabilityLayer.cpp:750-770.
        for (var k = 0; k < _unreliableReceipts.Count;)
        {
            if (_unreliableReceipts[k].DatagramNumber == datagramNumber)
            {
                _events.Enqueue(Acknowledged(_unreliableReceipts[k].Receipt));
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
        _bytesInResendBuffer -= (ulong)BitsToBytes(message.BitLength);

        // Only the last part of a split message reports its receipt (ReliabilityLayer.cpp:2486-2488).
        if (message.Reliability >= Reliability.ReliableWithAckReceipt && (message.Split is not { } split || split.Index + 1 == split.Count))
            _events.Enqueue(Acknowledged(message.Receipt));

        // RemoveFromList(internalPacket, isReliable), ReliabilityLayer.cpp:3559-3579.
        _resendList.Remove(message.ResendNode);
        _unacknowledgedBytes -= message.UnacknowledgedBytes;
        ReleaseMessage(message);
    }

    // SendACKs, ReliabilityLayer.cpp:3616-3656.
    private void SendAcks(long nowUs)
    {
        while (_acknowledgements.Count > 0)
        {
            var writer = new BitStreamWriter(_datagrams.Reserve(_mtu));
            // Sliding window never sends B and AS (CCRakNetSlidingWindow.cpp:264-271).
            DatagramHeader.ForAck().Write(ref writer);
            _acknowledgements.Write(ref writer, MaxPayloadBits);
            SendDatagram(writer.BytesWritten, nowUs);
            _window.OnSendAck();
        }
    }

    // One NAK datagram per update, ReliabilityLayer.cpp:1815-1825; what does not fit waits for the next.
    private void SendNak(long nowUs)
    {
        var writer = new BitStreamWriter(_datagrams.Reserve(_mtu));
        DatagramHeader.ForNak().Write(ref writer);
        _naks.Write(ref writer, MaxPayloadBits);
        SendDatagram(writer.BytesWritten, nowUs);
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

    // The send half of Update, ReliabilityLayer.cpp:1883-2237.
    private void SendQueuedMessages(long nowUs, uint bitsPerSecondLimit, bool needsBAndAs, bool isContinuousSend)
    {
        ResetPacketsAndDatagrams();

        var transmissionBandwidth = _window.GetTransmissionBandwidth(_unacknowledgedBytes, isContinuousSend);
        var retransmissionBandwidth = SlidingWindow.GetRetransmissionBandwidth(_unacknowledgedBytes);
        if (retransmissionBandwidth > 0 || transmissionBandwidth > 0)
        {
            _isLimitedByCongestionControl = false;
            _allDatagramBits = 0;
            // Keep filling datagrams until we exceed retransmission bandwidth (ReliabilityLayer.cpp:1902-1969).
            while (BitsToBytes(_allDatagramBits) < retransmissionBandwidth)
            {
                if (!PushDueResends(nowUs))
                    break;
            }
        }
        else
        {
            _isLimitedByCongestionControl = true;
        }

        if (BitsToBytes(_allDatagramBits) < transmissionBandwidth)
        {
            _allDatagramBits = 0;
            // Keep filling datagrams until we exceed transmission bandwidth (ReliabilityLayer.cpp:1976-2137). The
            // packet-pair clause of the condition never holds: packet pairs are disabled (ReliabilityLayer.cpp:3495).
            while (!ResendBufferOverflow() && BitsToBytes(_allDatagramBits) < transmissionBandwidth)
            {
                _isLimitedByOutgoingBandwidthLimit = bitsPerSecondLimit != 0
                    && (ulong)BitsToBytes(bitsPerSecondLimit) < _metrics[(int)SessionMetric.UserMessageBytesSent].LastSecond;
                if (!_isLimitedByOutgoingBandwidthLimit)
                    PushNewMessages(nowUs);
                if (_datagramBits == 0)
                    break; // No datagrams pushed.
                PushDatagram();
            }
        }

        var datagramNumberCount = _datagramBoundaries.Count;
        for (var i = 0; i < datagramNumberCount; i++)
            WriteDatagram(i, nowUs, needsBAndAs, isContinuousSend || i > 0);

        // Any data waiting to send after attempting to send means bandwidth is exceeded (ReliabilityLayer.cpp:2232).
        _bandwidthExceeded = _outgoing.Count > 0;
    }

    // One pass of the resend loop, ReliabilityLayer.cpp:1905-1968: fill one datagram from the head of the resend list
    // while the head is due. Returns pushedAnything.
    private bool PushDueResends(long nowUs)
    {
        var pushedAnything = false;
        var maxBits = MaxPayloadBits;
        while (_resendList.First is { } head)
        {
            var message = head.Value;
            // "if ( time - internalPacket->nextActionTime < (((CCTimeType)-1)/2) )": due once the time reaches it.
            if (nowUs < message.NextActionTimeUs)
            {
                PushDatagram(); // Filled one datagram.
                break;
            }
            if (_datagramBits + message.HeaderBits + message.BitLength > maxBits)
            {
                PushDatagram();
                break;
            }

            // PopListHead(false): the bytes stay unacknowledged.
            _resendList.RemoveFirst();
            _metrics[(int)SessionMetric.UserMessageBytesResent].Push(nowUs, (ulong)BitsToBytes(message.BitLength));
            PushPacket(message);
            message.TimesSent++;
            _window.OnResend();
            message.NextActionTimeUs = _window.GetRtoForRetransmission() + nowUs;
            pushedAnything = true;

            // InsertPacketIntoResendList(internalPacket, time, false, false): back at the tail.
            _resendList.AddLast(head);
        }
        return pushedAnything;
    }

    // The inner loop of the new-data phase, ReliabilityLayer.cpp:1998-2124: fill one datagram from the send queue.
    private void PushNewMessages(long nowUs)
    {
        var maxBits = MaxPayloadBits;
        while (_outgoing.TryPeek(out var message))
        {
            if (message.IsDiscarded)
            {
                // Culled by the unreliable timeout (ReliabilityLayer.cpp:2007-2016).
                _outgoing.Dequeue();
                LeaveSendBuffer(message);
                ReleaseMessage(message);
                continue;
            }

            // The fit test uses exact bits, the running total whole bytes (ReliabilityLayer.cpp:2018-2020, 3464).
            if (_datagramBits + message.HeaderBits + message.BitLength > maxBits)
                break; // Hit the MTU.

            _outgoing.Dequeue();
            LeaveSendBuffer(message);
            if (message.Reliability.IsReliable())
            {
                message.ReliableMessageNumber = _nextReliableMessageNumber;
                message.NextActionTimeUs = _window.GetRtoForRetransmission() + nowUs;
                _resendBuffer[(int)(message.ReliableMessageNumber.Value % ResendBufferLength)] = message;
                _bytesInResendBuffer += (ulong)BitsToBytes(message.BitLength);
                // InsertPacketIntoResendList(internalPacket, time, true, isReliable).
                _resendList.AddLast(message.ResendNode);
                _unacknowledgedBytes += message.UnacknowledgedBytes;
                _nextReliableMessageNumber++;
            }
            else
            {
                if (message.Reliability == Reliability.UnreliableWithAckReceipt)
                {
                    var datagramNumber = _window.NextDatagramNumber + (uint)_datagramBoundaries.Count;
                    var deadline = _window.GetRtoForRetransmission() + nowUs;
                    _unreliableReceipts.Add(new UnreliableReceipt(datagramNumber, message.Receipt, deadline));
                }
                // ClearPacketsAndDatagrams removes sent unreliable messages from the unreliable list (line 3527).
                if (message.UnreliableNode.List is not null)
                    _unreliable.Remove(message.UnreliableNode);
            }

            _metrics[(int)SessionMetric.UserMessageBytesSent].Push(nowUs, (ulong)BitsToBytes(message.BitLength));
            PushPacket(message);
            message.TimesSent++;

            if (ResendBufferOverflow())
                break;
        }
    }

    private void LeaveSendBuffer(OutgoingMessage message)
    {
        _messagesInSendBuffer[(int)message.Priority]--;
        _bytesInSendBuffer[(int)message.Priority] -= BitsToBytes(message.BitLength);
    }

    // ResetPacketsAndDatagrams, ReliabilityLayer.cpp:3452-3460.
    private void ResetPacketsAndDatagrams()
    {
        _packetsThisUpdate.Clear();
        _datagramBoundaries.Clear();
        _datagramBits = 0;
    }

    // PushPacket, ReliabilityLayer.cpp:3462-3483.
    private void PushPacket(OutgoingMessage message)
    {
        var bits = message.PushedBytes * 8;
        _datagramBits += bits;
        _allDatagramBits += bits;
        _packetsThisUpdate.Add(message);
    }

    // PushDatagram, ReliabilityLayer.cpp:3485-3506.
    private void PushDatagram()
    {
        if (_datagramBits > 0)
        {
            _datagramBoundaries.Add(_packetsThisUpdate.Count);
            _datagramBits = 0;
        }
    }

    // ReliabilityLayer.cpp:2140-2227.
    private void WriteDatagram(int index, long nowUs, bool needsBAndAs, bool isContinuousSend)
    {
        var datagramNumber = _window.TakeNextDatagramNumber();
        var writer = new BitStreamWriter(_datagrams.Reserve(_mtu));
        DatagramHeader.ForData(datagramNumber, isPacketPair: false, isContinuousSend, needsBAndAs).Write(ref writer);

        var first = index == 0 ? 0 : _datagramBoundaries[index - 1];
        var end = _datagramBoundaries[index];
        var messageNumbers = _datagramMessageNumbers;
        messageNumbers.Clear();
        for (var i = first; i < end; i++)
        {
            var message = _packetsThisUpdate[i];
            // Reliable or needs a receipt (line 2174): UNRELIABLE_WITH_ACK_RECEIPT records its unassigned number.
            if (message.Reliability is not (Reliability.Unreliable or Reliability.UnreliableSequenced))
                messageNumbers.Add(message.ReliableMessageNumber);
            EncapsulatedMessage.Write(
                ref writer,
                message.Reliability,
                message.Data.Span,
                message.BitLength,
                message.ReliableMessageNumber,
                message.SequencingIndex,
                message.OrderingIndex,
                message.OrderingChannel,
                message.Split
            );
            // An unreliable message is done once sent; a reliable one stays for resends until it is acknowledged.
            if (!message.Reliability.IsReliable())
                ReleaseMessage(message);
        }

        _datagramHistory.Add(nowUs, messageNumbers);
        SendDatagram(writer.BytesWritten, nowUs);
    }

    // SendBitStream, ReliabilityLayer.cpp:2247-2340: queues the datagram just written into the reserved room.
    private void SendDatagram(int length, long nowUs)
    {
        _metrics[(int)SessionMetric.ActualBytesSent].Push(nowUs, (ulong)length);
        _datagrams.Commit(length, default);
    }

    private OutgoingMessage RentMessage() => _freeMessages.TryPop(out var message) ? message : new OutgoingMessage();

    // InternalPacket back to the pool (ReleaseToInternalPacketPool): its data is dropped and its state cleared.
    private void ReleaseMessage(OutgoingMessage message)
    {
        message.Clear();
        if (_freeMessages.Count < MaxPooled)
            _freeMessages.Push(message);
    }

    // ResendBufferOverflow, ReliabilityLayer.cpp:3758-3765.
    private bool ResendBufferOverflow() =>
        _resendBuffer[(int)(_nextReliableMessageNumber.Value % ResendBufferLength)] is not null;

    // BITS_TO_BYTES, RakNetDefines.h.
    private static int BitsToBytes(int bits) => (bits + 7) >> 3;

    private static uint BitsToBytes(uint bits) => (uint)(((ulong)bits + 7) >> 3);

    private readonly record struct UnreliableReceipt(UInt24 DatagramNumber, uint Receipt, long DeadlineUs);

    /// <summary>
    /// The send-side InternalPacket. Instances are pooled (<see cref="RentMessage"/>, <see cref="ReleaseMessage"/>), and
    /// each keeps the buffer it copies unsplit messages into and its list nodes from one use to the next.
    /// </summary>
    private sealed class OutgoingMessage
    {
        private byte[] _buffer = [];

        public OutgoingMessage()
        {
            ResendNode = new LinkedListNode<OutgoingMessage>(this);
            UnreliableNode = new LinkedListNode<OutgoingMessage>(this);
        }

        /// <summary>The message, or for a split part its slice of the whole message's copy.</summary>
        public ReadOnlyMemory<byte> Data { get; private set; }

        public int BitLength { get; private set; }

        public Reliability Reliability { get; private set; }

        public Priority Priority { get; private set; }

        public uint Receipt { get; private set; }

        public long CreationTimeUs { get; private set; }

        public byte OrderingChannel { get; set; }

        public UInt24 OrderingIndex { get; set; }

        public UInt24 SequencingIndex { get; set; }

        public UInt24 ReliableMessageNumber { get; set; } = UnassignedMessageNumber;

        public SplitHeader? Split { get; set; }

        public long NextActionTimeUs { get; set; }

        public int TimesSent { get; set; }

        /// <summary>This message's node in the resend list; in it while sent and not yet acknowledged.</summary>
        public LinkedListNode<OutgoingMessage> ResendNode { get; }

        /// <summary>This message's node in the unreliable list; in it while queued and not yet sent or culled.</summary>
        public LinkedListNode<OutgoingMessage> UnreliableNode { get; }

        /// <summary>Culled by the unreliable timeout: the C++ frees its data and sets it to 0 (line 1764-1765).</summary>
        public bool IsDiscarded { get; set; }

        /// <summary>The types kept on the unreliable list (AddToUnreliableLinkedList, ReliabilityLayer.cpp:3717-3722).</summary>
        public bool IsUnreliable =>
            Reliability is Reliability.Unreliable or Reliability.UnreliableSequenced or Reliability.UnreliableWithAckReceipt;

        /// <summary>GetMessageHeaderLengthBits (ReliabilityLayer.cpp:2555-2605).</summary>
        public int HeaderBits => EncapsulatedMessage.GetHeaderLengthBits(Reliability, isSplit: Split is not null);

        /// <summary>Header plus data, each rounded up to whole bytes (PushPacket, ReliabilityLayer.cpp:3464).</summary>
        public int PushedBytes => BitsToBytes(BitLength) + BitsToBytes(HeaderBits);

        /// <summary>Header and data rounded up together (AddToListTail and RemoveFromList, ReliabilityLayer.cpp:3573, 3585).</summary>
        public int UnacknowledgedBytes => BitsToBytes(HeaderBits + BitLength);

        /// <summary>Copies <paramref name="data"/> into this message's own buffer.</summary>
        public void CopyFrom(ReadOnlySpan<byte> data)
        {
            if (_buffer.Length < data.Length)
                _buffer = new byte[Math.Max(data.Length, 64)];
            data.CopyTo(_buffer);
            Data = _buffer.AsMemory(0, data.Length);
        }

        /// <summary>Sends <paramref name="data"/> as it is: memory this message does not own.</summary>
        public void Reference(ReadOnlyMemory<byte> data) => Data = data;

        public void Set(int bitLength, Reliability reliability, Priority priority, uint receipt, long creationTimeUs)
        {
            BitLength = bitLength;
            Reliability = reliability;
            Priority = priority;
            Receipt = receipt;
            CreationTimeUs = creationTimeUs;
        }

        public void Clear()
        {
            Data = default;
            BitLength = 0;
            Reliability = default;
            Priority = default;
            Receipt = 0;
            CreationTimeUs = 0;
            OrderingChannel = 0;
            OrderingIndex = default;
            SequencingIndex = default;
            ReliableMessageNumber = UnassignedMessageNumber;
            Split = null;
            NextActionTimeUs = 0;
            TimesSent = 0;
            IsDiscarded = false;
        }
    }
}
