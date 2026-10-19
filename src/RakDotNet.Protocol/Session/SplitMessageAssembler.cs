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
/// </remarks>
internal sealed class SplitMessageAssembler
{
    // ID_DOWNLOAD_PROGRESS, then three host-order unsigned ints (ReliabilityLayer.cpp:3151-3160).
    private const int ProgressHeaderBytes = 1 + 3 * sizeof(uint);

    private readonly Dictionary<ushort, PartialMessage> _messages = [];

    /// <summary>
    /// SetSplitMessageProgressInterval (ReliabilityLayer.cpp:2378-2381): every this many parts of an incomplete message,
    /// once its first part has arrived, an ID_DOWNLOAD_PROGRESS message is produced. 0, the default, never.
    /// </summary>
    public int ProgressInterval { get; set; }

    /// <summary>Messages with some but not all parts received.</summary>
    public int IncompleteCount => _messages.Count;

    /// <summary>
    /// InsertIntoSplitPacketList followed by BuildPacketFromSplitPacketList for one part. Returns false when the part
    /// was ignored as a repeat of a split index already held.
    /// </summary>
    /// <param name="part">A part: a message with a <see cref="EncapsulatedMessage.Split"/> header.</param>
    /// <param name="progress">The ID_DOWNLOAD_PROGRESS message this part produced, if any.</param>
    /// <param name="complete">The whole message, when this was its last missing part.</param>
    public bool TryInsert(EncapsulatedMessage part, out byte[]? progress, out ReceivedMessage? complete)
    {
        ArgumentNullException.ThrowIfNull(part);
        var split = part.Split ?? throw new ArgumentException("The message is not a split part.", nameof(part));
        progress = null;
        complete = null;

        if (!_messages.TryGetValue(split.Id, out var message))
        {
            message = new PartialMessage(part);
            _messages.Add(split.Id, message);
        }
        if (!message.Parts.TryAdd(split.Index, part))
            return false;
        if (split.Index == 0)
            message.FirstPart = part;

        var arrived = message.Parts.Count;
        // ReliabilityLayer.cpp:3135-3138.
        if (
            ProgressInterval != 0
            && message.FirstPart is { } first
            && arrived != first.Split!.Value.Count
            && arrived % ProgressInterval == 0
        )
            progress = Progress((uint)arrived, split.Count, first);

        // ReliabilityLayer.cpp:3241: the count of the first part to arrive decides.
        if (arrived == message.FirstArrived.Split!.Value.Count)
        {
            _messages.Remove(split.Id);
            complete = Build(message);
        }
        return true;
    }

    // ReliabilityLayer.cpp:3140-3171.
    private static byte[] Progress(uint arrived, uint count, EncapsulatedMessage first)
    {
        var firstBytes = first.Payload.Span;
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
    private static ReceivedMessage? Build(PartialMessage message)
    {
        var indexes = message.Parts.Keys.Order().ToList();
        long bitLength = 0;
        long bufferLength = 0;
        foreach (var index in indexes)
        {
            var part = message.Parts[index];
            bufferLength = Math.Max(bufferLength, ((bitLength + 7) >> 3) + part.Payload.Length);
            bitLength += part.PayloadBitLength;
        }
        if (bitLength > int.MaxValue || bufferLength > Array.MaxLength)
            return null;

        var data = new byte[bufferLength];
        long offset = 0;
        foreach (var index in indexes)
        {
            var part = message.Parts[index];
            part.Payload.Span.CopyTo(data.AsSpan((int)((offset + 7) >> 3)));
            offset += part.PayloadBitLength;
        }

        var header = message.FirstArrived;
        var payload = data.AsMemory(0, (int)((bitLength + 7) >> 3));
        return new ReceivedMessage(header.Reliability, payload, (int)bitLength, header.SequencingIndex, header.OrderingIndex, header.OrderingChannel);
    }

    /// <summary>A SplitPacketChannel.</summary>
    private sealed class PartialMessage(EncapsulatedMessage firstArrived)
    {
        /// <summary>splitPacketList[0]: the part whose header the rebuilt message takes.</summary>
        public EncapsulatedMessage FirstArrived { get; } = firstArrived;

        /// <summary>firstPacket: split index 0, once it has arrived; progress reports carry it.</summary>
        public EncapsulatedMessage? FirstPart { get; set; }

        public Dictionary<uint, EncapsulatedMessage> Parts { get; } = [];
    }
}
