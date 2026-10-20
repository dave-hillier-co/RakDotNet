using RakDotNet.Protocol.Session;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// The reliability-layer hooks of PluginInterface2 (UsesReliabilityLayer() true). Runs in the I/O cycle, so it must not
/// block; it may call the thread-safe commands. Spans are valid for the call; everything else is a value.
/// </summary>
/// <remarks>
/// Observers in <see cref="RakPeerLowLevel.Observers"/> are called first, then plugins that implement this interface,
/// each in attach order. With none attached the hooks cost a branch and allocate nothing.
/// </remarks>
public interface INetworkObserver
{
    /// <summary>OnDirectSocketSend</summary>
    void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote) { }

    /// <summary>OnDirectSocketReceive</summary>
    void OnDatagramReceived(ReadOnlySpan<byte> datagram, SystemAddress remote) { }

    /// <summary>
    /// OnInternalPacket: a message of a connection packed into a datagram (ReliabilityLayer.cpp:2112-2119), resent
    /// (ReliabilityLayer.cpp:1942-1949), or parsed from a received datagram before duplicates are dropped
    /// (ReliabilityLayer.cpp:899-906).
    /// </summary>
    void OnFrame(in FrameInfo frame) { }

    /// <summary>
    /// OnAck: each message number of a datagram the remote system acknowledged, including numbers already
    /// acknowledged and the unassigned number 0xFFFFFF an UNRELIABLE_WITH_ACK_RECEIPT message records
    /// (ReliabilityLayer.cpp:2448-2455). <paramref name="timeMs"/> is <see cref="RakPeer.GetTime"/>.
    /// </summary>
    void OnAck(uint messageNumber, SystemAddress remote, ulong timeMs) { }

    /// <summary>
    /// OnReliabilityLayerNotification, with RakNet's message text and the datagram's length in bits. Malformed or hostile
    /// input is reported here and dropped; it never throws. Warnings (<paramref name="isError"/> false) are those RakNet
    /// raises without LOG_TRIVIAL_NOTIFICATIONS: a reliable message behind the receive window.
    /// </summary>
    void OnReliabilityNotification(ReadOnlySpan<char> message, int bitLength, SystemAddress remote, bool isError) { }

    /// <summary>The SetUserUpdateThread callback: once at the end of every I/O cycle.</summary>
    void OnUpdateCycle() { }
}

/// <summary>The InternalPacket fields PacketLogger prints, for <see cref="INetworkObserver.OnFrame"/>.</summary>
public readonly ref struct FrameInfo
{
    private readonly SplitHeaderValues _split;

    internal FrameInfo(in SessionFrame frame, SystemAddress remote, RakConnection? connection)
    {
        IsSend = frame.IsSend;
        FrameNumber = frame.FrameNumber;
        TimeMs = (ulong)(frame.TimeUs / 1000);
        Remote = remote;
        Connection = connection;
        Reliability = frame.Reliability;
        Priority = frame.Priority;
        OrderingChannel = frame.OrderingChannel;
        MessageNumber = frame.MessageNumber;
        OrderingIndex = frame.OrderingIndex;
        SequencingIndex = frame.SequencingIndex;
        _split = frame.Split is { } split ? new SplitHeaderValues(true, split.Id, split.Index, split.Count) : default;
        CreationTimeUs = (ulong)frame.CreationTimeUs;
        Data = frame.Data;
        BitLength = frame.BitLength;
    }

    /// <summary>OnInternalPacket's isSend.</summary>
    public bool IsSend { get; }

    /// <summary>
    /// frameNumber: on send, the number of the datagram the message goes out in; on receive, how many datagrams the
    /// connection received before this one (receivePacketCount).
    /// </summary>
    public uint FrameNumber { get; }

    /// <summary>The peer's time, <see cref="RakPeer.GetTime"/>.</summary>
    public ulong TimeMs { get; }

    public SystemAddress Remote { get; }

    /// <summary>The connection the remote system's slot belongs to, or null while none has been created for it.</summary>
    public RakConnection? Connection { get; }

    /// <summary>As sent, including the ack-receipt types; on receive, the base type the wire carries.</summary>
    public Reliability Reliability { get; }

    /// <summary>The send priority; a received frame reports <see cref="Priority.Immediate"/>, as the wire carries none.</summary>
    public Priority Priority { get; }

    /// <summary>The ordering channel; 0 for a message that is neither ordered nor sequenced.</summary>
    public byte OrderingChannel { get; }

    /// <summary>reliableMessageNumber; 0xFFFFFF for an unreliable message, which has none.</summary>
    public UInt24 MessageNumber { get; }

    /// <summary>The ordering index; 0 for a message that is neither ordered nor sequenced, where RakNet leaves a stale value.</summary>
    public UInt24 OrderingIndex { get; }

    public UInt24 SequencingIndex { get; }

    public bool IsSplit => _split.IsSplit;

    /// <summary>splitPacketId; 0 when not split.</summary>
    public ushort SplitId => _split.Id;

    /// <summary>splitPacketIndex; 0 when not split.</summary>
    public uint SplitIndex => _split.Index;

    /// <summary>splitPacketCount; 0 when not split.</summary>
    public uint SplitCount => _split.Count;

    /// <summary>When the message was queued for sending, or when it was received.</summary>
    public ulong CreationTimeUs { get; }

    /// <summary>The message, or this part of a split message.</summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>dataBitLength.</summary>
    public int BitLength { get; }

    private readonly record struct SplitHeaderValues(bool IsSplit, ushort Id, uint Index, uint Count);
}
