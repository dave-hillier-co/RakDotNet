using System.Buffers.Binary;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// Reassembles split messages: splitPacketChannelList with InsertIntoSplitPacketList (ReliabilityLayer.cpp:3038-3175)
/// and BuildPacketFromSplitPacketList (ReliabilityLayer.cpp:3180-3255), in the PREALLOCATE_LARGE_MESSAGES 0 form the
/// default build uses (RakNetDefines.h:127-128).
/// </summary>
/// <remarks>
/// <para>
/// Parts are grouped by split id. A message is complete when as many parts have arrived as the first part to arrive
/// says there are, and it takes its reliability, ordering and sequencing fields from that part, as in the C++.
/// </para>
/// <para>
/// Two differences from the C++, both only for input stock RakNet never produces in a working message. The C++ joins the
/// parts in the order they arrived, which corrupts a message whose parts were reordered by the network; here they are
/// joined in split index order. And a second part with a split index already held, which only a broken or hostile
/// sender could produce past the duplicate filter, is ignored here, where the C++ counts it towards completion.
/// </para>
/// <para>
/// The C++ has no limits here. This class enforces the split limits of <see cref="SessionLimits"/>: parts per message,
/// bytes per message, incomplete messages at once, and time without a new part. Each part's payload is copied, so a
/// held part costs its own length (plus <see cref="SessionLimits.BufferedEntryOverheadBytes"/>), never its datagram.
/// </para>
/// </remarks>
internal sealed class SplitMessageAssembler
{
    // ID_DOWNLOAD_PROGRESS, then three host-order unsigned ints (ReliabilityLayer.cpp:3151-3160).
    private const int ProgressHeaderBytes = 1 + 3 * sizeof(uint);

    private readonly SessionLimits _limits;
    private readonly Dictionary<ushort, PartialMessage> _messages = [];

    public SplitMessageAssembler(SessionLimits? limits = null)
    {
        _limits = (limits ?? SessionLimits.Default).Validated();
    }

    /// <summary>
    /// SetSplitMessageProgressInterval (ReliabilityLayer.cpp:2378-2381): every this many parts of an incomplete message,
    /// once its first part has arrived, an ID_DOWNLOAD_PROGRESS message is produced. 0, the default, never.
    /// </summary>
    public int ProgressInterval { get; set; }

    /// <summary>Messages with some but not all parts received.</summary>
    public int IncompleteCount => _messages.Count;

    /// <summary>The bytes held in parts of incomplete messages, each charged its payload plus the entry overhead.</summary>
    public long BufferedBytes { get; private set; }

    /// <summary>
    /// InsertIntoSplitPacketList followed by BuildPacketFromSplitPacketList for one part, after checking it against the
    /// limits. Nothing is held when the result is not <see cref="SplitInsertResult.Accepted"/>.
    /// </summary>
    /// <param name="part">A part: a message with a <see cref="EncapsulatedMessage.Split"/> header.</param>
    /// <param name="nowUs">The current time, which restarts the message's reassembly timeout.</param>
    /// <param name="availableBytes">How many more bytes the connection may buffer.</param>
    /// <param name="progress">The ID_DOWNLOAD_PROGRESS message this part produced, if any.</param>
    /// <param name="complete">The whole message, when this was its last missing part.</param>
    public SplitInsertResult TryInsert(
        EncapsulatedMessage part,
        long nowUs,
        long availableBytes,
        out byte[]? progress,
        out ReceivedMessage? complete
    )
    {
        ArgumentNullException.ThrowIfNull(part);
        var split = part.Split ?? throw new ArgumentException("The message is not a split part.", nameof(part));
        progress = null;
        complete = null;

        if (split.Count > (uint)_limits.MaxSplitCount)
            return SplitInsertResult.SplitCountExceeded;

        var isNew = !_messages.TryGetValue(split.Id, out var message);
        if (isNew)
        {
            if (_messages.Count >= _limits.MaxConcurrentSplits)
                return SplitInsertResult.TooManyConcurrentSplits;
        }
        else if (message!.Parts.ContainsKey(split.Index))
        {
            return SplitInsertResult.Ignored;
        }

        var length = part.Payload.Length;
        // Every part but the last fills a whole block, so for any RakNet sender (count - 1) blocks are less than the
        // message: a part that is not the last and implies more than the limit is refused before the rest is buffered.
        var impliedBytes = split.Index + 1 < split.Count ? (long)(split.Count - 1) * length : 0;
        var messageBytes = (isNew ? 0 : message!.Bytes) + length;
        if (messageBytes > _limits.MaxSplitMessageBytes || impliedBytes > _limits.MaxSplitMessageBytes)
            return SplitInsertResult.SplitMessageTooLarge;

        var cost = (long)length + SessionLimits.BufferedEntryOverheadBytes;
        if (cost > availableBytes)
            return SplitInsertResult.ReceiveBufferFull;

        if (isNew)
        {
            message = new PartialMessage(part);
            _messages.Add(split.Id, message);
        }
        var held = new HeldPart(part.Payload.ToArray(), part.PayloadBitLength);
        message!.Parts.Add(split.Index, held);
        message.Bytes = messageBytes;
        message.Cost += cost;
        message.LastArrivalUs = nowUs;
        BufferedBytes += cost;
        if (split.Index == 0)
        {
            message.FirstPart = held;
            message.FirstPartCount = split.Count;
        }

        var arrived = message.Parts.Count;
        // ReliabilityLayer.cpp:3135-3138.
        if (
            ProgressInterval != 0
            && message.FirstPart is { } first
            && arrived != message.FirstPartCount
            && arrived % ProgressInterval == 0
        )
            progress = Progress((uint)arrived, split.Count, first.Data);

        // ReliabilityLayer.cpp:3241: the count of the first part to arrive decides.
        if (arrived == message.Count)
        {
            Remove(split.Id, message);
            complete = Build(message);
        }
        return SplitInsertResult.Accepted;
    }

    /// <summary>True when an incomplete message has gone longer than the reassembly timeout without a new part.</summary>
    public bool HasTimedOut(long nowUs)
    {
        foreach (var message in _messages.Values)
        {
            if (nowUs - message.LastArrivalUs > _limits.SplitReassemblyTimeoutUs)
                return true;
        }
        return false;
    }

    /// <summary>Releases every incomplete message.</summary>
    public void Clear()
    {
        _messages.Clear();
        BufferedBytes = 0;
    }

    private void Remove(ushort id, PartialMessage message)
    {
        _messages.Remove(id);
        BufferedBytes -= message.Cost;
    }

    // ReliabilityLayer.cpp:3140-3171.
    private static byte[] Progress(uint arrived, uint count, byte[] firstBytes)
    {
        var progress = new byte[ProgressHeaderBytes + firstBytes.Length];
        progress[0] = (byte)MessageId.DownloadProgress;
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(1), arrived);
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(5), count);
        BinaryPrimitives.WriteUInt32LittleEndian(progress.AsSpan(9), (uint)firstBytes.Length);
        firstBytes.CopyTo(progress.AsSpan(ProgressHeaderBytes));
        return progress;
    }

    // BuildPacketFromSplitPacketList, ReliabilityLayer.cpp:3195-3220: the bit lengths add up, and each part is copied
    // at the byte its predecessors' bits round up to. Parts from RakNet are whole bytes except the last, so the copies
    // tile the message; parts that are not would run past its end in the C++, and the buffer here is sized to hold them.
    // A part's bit length is at most eight times its bytes, so with MaxSplitMessageBytes capped at
    // SessionLimits.LargestSplitMessageBytes both the bit length and the buffer fit an int.
    private static ReceivedMessage Build(PartialMessage message)
    {
        var indexes = message.Parts.Keys.Order().ToList();
        long bitLength = 0;
        long bufferLength = 0;
        foreach (var index in indexes)
        {
            var part = message.Parts[index];
            bufferLength = Math.Max(bufferLength, ((bitLength + 7) >> 3) + part.Data.Length);
            bitLength += part.BitLength;
        }
        var data = new byte[bufferLength];
        long offset = 0;
        foreach (var index in indexes)
        {
            var part = message.Parts[index];
            part.Data.CopyTo(data.AsSpan((int)((offset + 7) >> 3)));
            offset += part.BitLength;
        }

        var header = message.Header;
        var byteLength = (int)((bitLength + 7) >> 3);
        // The payload owns its array whenever the parts tile it, so ordering buffers can hold it without a copy.
        ReadOnlyMemory<byte> payload = byteLength == data.Length ? data : data.AsMemory(0, byteLength);
        return new ReceivedMessage(header.Reliability, payload, (int)bitLength, header.SequencingIndex, header.OrderingIndex, header.OrderingChannel);
    }

    private readonly record struct HeldPart(byte[] Data, int BitLength);

    /// <summary>A SplitPacketChannel.</summary>
    private sealed class PartialMessage(EncapsulatedMessage firstArrived)
    {
        /// <summary>splitPacketList[0]'s header: the fields the rebuilt message takes.</summary>
        public (Reliability Reliability, UInt24 SequencingIndex, UInt24 OrderingIndex, byte OrderingChannel) Header { get; } =
            (firstArrived.Reliability, firstArrived.SequencingIndex, firstArrived.OrderingIndex, firstArrived.OrderingChannel);

        /// <summary>The part count of the first part to arrive, which decides when the message is complete.</summary>
        public uint Count { get; } = firstArrived.Split!.Value.Count;

        /// <summary>firstPacket: split index 0, once it has arrived; progress reports carry it.</summary>
        public HeldPart? FirstPart { get; set; }

        /// <summary>firstPacket->splitPacketCount.</summary>
        public uint FirstPartCount { get; set; }

        public Dictionary<uint, HeldPart> Parts { get; } = [];

        /// <summary>The payload bytes held.</summary>
        public long Bytes { get; set; }

        /// <summary>The bytes charged to the connection's receive buffer.</summary>
        public long Cost { get; set; }

        public long LastArrivalUs { get; set; }
    }
}

/// <summary>What <see cref="SplitMessageAssembler.TryInsert"/> did with a part.</summary>
internal enum SplitInsertResult
{
    /// <summary>The part is held, or completed its message.</summary>
    Accepted,

    /// <summary>A repeat of a split index already held; nothing changed.</summary>
    Ignored,

    /// <summary>The part declared more parts than <see cref="SessionLimits.MaxSplitCount"/>.</summary>
    SplitCountExceeded,

    /// <summary>The message would grow, or declares it will grow, past <see cref="SessionLimits.MaxSplitMessageBytes"/>.</summary>
    SplitMessageTooLarge,

    /// <summary>The part starts a new message with <see cref="SessionLimits.MaxConcurrentSplits"/> already incomplete.</summary>
    TooManyConcurrentSplits,

    /// <summary>Holding the part would take the connection past its receive buffer limit.</summary>
    ReceiveBufferFull,
}
