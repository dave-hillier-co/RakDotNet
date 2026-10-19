using RakDotNet.Protocol.Session;

namespace RakDotNet;

/// <summary>
/// The hardening limits of docs/plan.md. The defaults accept everything stock RakNet sends.
/// </summary>
/// <remarks>
/// <para>
/// RakNet itself has none of the receive limits, so a remote system could make it buffer without end. Each default is
/// set above what any RakNet sample or sender produces, so only a broken or hostile remote system reaches one. Every
/// property not set in an initializer keeps its default.
/// </para>
/// <para>
/// A connection whose remote system exceeds a receive limit is closed and reported as lost, as an acknowledgement
/// timeout is (ID_CONNECTION_LOST). Split parts are always sent reliably and every reliable message is acknowledged as
/// its datagram arrives, so the sender will never send a dropped one again: dropping it would lose a reliable message
/// for good and stall its ordering channel. The one exception is an UNRELIABLE_SEQUENCED message that finds no room to
/// wait for its ordering channel; it is dropped, as an unreliable message may be.
/// </para>
/// <para>
/// When the stream holds <see cref="EventQueueCapacity"/> entries the I/O cycle stops reading datagrams until the
/// consumer drains it below half, so the kernel queues or drops datagrams and the reliability layer recovers them.
/// Locally generated control entries use reserved headroom above the cap and are never dropped or reordered.
/// </para>
/// </remarks>
public sealed record PeerLimits
{
    /// <summary>Limits that accept everything stock RakNet sends.</summary>
    public static PeerLimits Default { get; } = new();

    /// <summary>
    /// The most parts one split message may declare. Default 262,144: a message of
    /// <see cref="MaxSplitMessageBytes"/> needs about 260,000 parts at the smallest MTU (576), where each part carries
    /// 516 bytes.
    /// </summary>
    public int MaxSplitCount { get; init; } = SessionLimits.Default.MaxSplitCount;

    /// <summary>
    /// The longest message a remote system may send split into parts. Default 134,217,728 (128 MiB): the largest message
    /// in any RakNet sample is BigPacketTest's 83,296,256 bytes (BigPacketTest.cpp:27). At most 268,435,455, so that
    /// the message's length in bits fits RakNet's 32-bit BitSize_t.
    /// </summary>
    public int MaxSplitMessageBytes { get; init; } = SessionLimits.Default.MaxSplitMessageBytes;

    /// <summary>
    /// The most split messages that may be partly received at once on one connection. Default 1,024, twice what RakNet
    /// can cause: a message stays incomplete only while one of its parts is unacknowledged, and a RakNet sender holds at
    /// most 512 unacknowledged reliable messages (RESEND_BUFFER_ARRAY_LENGTH, RakNetDefines.h:105), with at most one
    /// partly sent message per priority besides.
    /// </summary>
    public int MaxConcurrentSplitsPerConnection { get; init; } = SessionLimits.Default.MaxConcurrentSplits;

    /// <summary>
    /// How long a partly received split message may go without a new part. Default 60 seconds. It is measured from the
    /// latest part, not the first, so a large message may take any time to arrive while it keeps arriving. A RakNet
    /// sender resends a missing part at least every 2 seconds (the retransmission timeout's cap) and drops the
    /// connection after 10 seconds without acknowledgements, so a live sender never lets a minute pass.
    /// </summary>
    public TimeSpan SplitReassemblyTimeout { get; init; } = TimeSpan.FromMicroseconds(SessionLimits.Default.SplitReassemblyTimeoutUs);

    /// <summary>
    /// The most messages one ordering channel may hold while it waits for a missing earlier one. Default 65,536: the
    /// wait lasts about one retransmission timeout, and this is room for tens of thousands of messages a second on one
    /// channel through it. RakNet's samples send at most a few hundred at once (BurstTest sends 128 by default).
    /// </summary>
    public int MaxOrderingBufferMessagesPerChannel { get; init; } = SessionLimits.Default.MaxOrderingBufferMessagesPerChannel;

    /// <summary>
    /// The most bytes one connection may hold in partly received split messages and ordering buffers together, each
    /// message or part charged its length plus 64 bytes for its bookkeeping. Default 268,435,456 (256 MiB). Reassembling
    /// a message of <see cref="MaxSplitMessageBytes"/> costs about 140 MB at MTU 1492 and 151 MB at MTU 576, with the
    /// charge for each part. Besides that, a RakNet sender can leave at most 511 messages waiting behind a missing one,
    /// because it holds at most 512 unacknowledged reliable messages (RESEND_BUFFER_ARRAY_LENGTH, RakNetDefines.h:105)
    /// and every message or split part it sends is at most one MTU; so a complete maximal message never waits on its
    /// ordering channel. This is the bound on what a remote system can make a connection buffer.
    /// </summary>
    public int MaxReceiveBufferBytesPerConnection { get; init; } = (int)SessionLimits.Default.MaxReceiveBufferBytes;

    public int EventQueueCapacity { get; init; } = 65_536;

    /// <summary>The session's form of these limits. Throws when a limit is below one or the timeout is not positive.</summary>
    internal SessionLimits ToSessionLimits() =>
        new SessionLimits
        {
            MaxSplitCount = MaxSplitCount,
            MaxSplitMessageBytes = MaxSplitMessageBytes,
            MaxConcurrentSplits = MaxConcurrentSplitsPerConnection,
            SplitReassemblyTimeoutUs = SplitReassemblyTimeout.Ticks / TimeSpan.TicksPerMicrosecond,
            MaxOrderingBufferMessagesPerChannel = MaxOrderingBufferMessagesPerChannel,
            MaxReceiveBufferBytes = MaxReceiveBufferBytesPerConnection,
        }.Validated();
}
