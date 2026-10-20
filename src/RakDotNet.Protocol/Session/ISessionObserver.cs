using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// The hooks ReliabilityLayer.cpp calls on its messageHandlerList: PluginInterface2's OnInternalPacket, OnAck and
/// OnReliabilityLayerNotification. <see cref="ReliabilityLayer"/> checks <see cref="IsObserving"/> before it builds
/// anything to report, so an observer that is not observing costs a branch and allocates nothing.
/// </summary>
internal interface ISessionObserver
{
    /// <summary>False while nothing listens; the layer then raises nothing.</summary>
    bool IsObserving { get; }

    /// <summary>OnInternalPacket: a message packed into a datagram, resent, or parsed from a received datagram.</summary>
    void OnFrame(in SessionFrame frame);

    /// <summary>OnAck: each message number of a datagram the remote system acknowledged (ReliabilityLayer.cpp:2448-2455).</summary>
    void OnAck(UInt24 messageNumber, long nowUs);

    /// <summary>
    /// OnReliabilityLayerNotification, with the C++ message text and the received datagram's length in bits
    /// (BYTES_TO_BITS(length)).
    /// </summary>
    void OnNotification(string message, int bitLength, bool isError);
}

/// <summary>The InternalPacket fields OnInternalPacket passes, valid for the call.</summary>
internal readonly ref struct SessionFrame
{
    public SessionFrame(
        bool isSend,
        uint frameNumber,
        long timeUs,
        Reliability reliability,
        Priority priority,
        byte orderingChannel,
        UInt24 messageNumber,
        UInt24 orderingIndex,
        UInt24 sequencingIndex,
        SplitHeader? split,
        long creationTimeUs,
        ReadOnlySpan<byte> data,
        int bitLength)
    {
        IsSend = isSend;
        FrameNumber = frameNumber;
        TimeUs = timeUs;
        Reliability = reliability;
        Priority = priority;
        OrderingChannel = orderingChannel;
        MessageNumber = messageNumber;
        OrderingIndex = orderingIndex;
        SequencingIndex = sequencingIndex;
        Split = split;
        CreationTimeUs = creationTimeUs;
        Data = data;
        BitLength = bitLength;
    }

    public bool IsSend { get; }

    /// <summary>
    /// On send, the number of the datagram the message goes out in (packetsToSendThisUpdateDatagramBoundaries.Size() +
    /// GetNextDatagramSequenceNumber(), ReliabilityLayer.cpp:1945, 2115); on receive, how many datagrams were received
    /// before this one (receivePacketCount, ReliabilityLayer.cpp:902, 1499).
    /// </summary>
    public uint FrameNumber { get; }

    public long TimeUs { get; }

    public Reliability Reliability { get; }

    /// <summary>The send priority; a received frame has the default, as the wire carries none.</summary>
    public Priority Priority { get; }

    public byte OrderingChannel { get; }

    /// <summary>The reliable message number; <see cref="UInt24.MaxValue"/> when none was assigned (ReliabilityLayer.cpp:3678).</summary>
    public UInt24 MessageNumber { get; }

    public UInt24 OrderingIndex { get; }

    public UInt24 SequencingIndex { get; }

    public SplitHeader? Split { get; }

    public long CreationTimeUs { get; }

    public ReadOnlySpan<byte> Data { get; }

    public int BitLength { get; }
}
