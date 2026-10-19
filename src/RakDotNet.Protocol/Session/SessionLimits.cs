namespace RakDotNet.Protocol.Session;

/// <summary>
/// The receive-side hardening limits of one connection (docs/plan.md). RakNet has none of them; each is set well above
/// what a RakNet sender produces, so only a broken or hostile remote system reaches one. The public form, with the
/// reasoning behind every default, is <c>RakDotNet.PeerLimits</c>.
/// </summary>
/// <remarks>
/// Reaching a limit closes the connection (<see cref="ConnectionDied"/> with a <see cref="ConnectionDeathCause"/>),
/// except that an UNRELIABLE_SEQUENCED message that finds its ordering buffer full is dropped. Split parts are always
/// reliable, and reliable messages are acknowledged as their datagram arrives, so dropping one would lose it for good
/// and stall its ordering channel; closing is the only outcome that does not silently break the reliable stream.
/// </remarks>
internal sealed record SessionLimits
{
    /// <summary>
    /// The bytes charged for each buffered message or split part on top of its payload, for the objects that hold it,
    /// so that many tiny entries cannot use more memory than <see cref="MaxReceiveBufferBytes"/> allows.
    /// </summary>
    public const int BufferedEntryOverheadBytes = 64;

    /// <summary>
    /// The most disjoint ranges of datagram numbers waiting to be NAKed. RakNet sends one NAK datagram per update
    /// (ReliabilityLayer.cpp:1815-1825), and one holds a few hundred ranges, so this is many updates' worth. Numbers
    /// beyond it are not NAKed; the retransmission timeout resends their messages anyway.
    /// </summary>
    public const int MaxPendingNakRanges = 4_096;

    /// <summary>The largest <see cref="MaxSplitMessageBytes"/>: a message's bit length is a BitSize_t, a uint32 (RakNetTypes.h:101).</summary>
    public const int LargestSplitMessageBytes = int.MaxValue / 8;

    public static SessionLimits Default { get; } = new();

    /// <summary>The most parts a split message may declare.</summary>
    public int MaxSplitCount { get; init; } = 262_144;

    /// <summary>
    /// The longest message split parts may add up to. At most <see cref="LargestSplitMessageBytes"/>, so that the
    /// reassembled message's bit length fits an int.
    /// </summary>
    public int MaxSplitMessageBytes { get; init; } = 128 * 1024 * 1024;

    /// <summary>The most split messages that may be incomplete at once.</summary>
    public int MaxConcurrentSplits { get; init; } = 1_024;

    /// <summary>How long an incomplete split message may go without a new part.</summary>
    public long SplitReassemblyTimeoutUs { get; init; } = 60_000_000;

    /// <summary>The most messages one ordering channel may hold while it waits for a missing one.</summary>
    public int MaxOrderingBufferMessagesPerChannel { get; init; } = 65_536;

    /// <summary>
    /// The most bytes the connection may hold in incomplete split messages and ordering buffers together, each entry
    /// charged its payload plus <see cref="BufferedEntryOverheadBytes"/>.
    /// </summary>
    public long MaxReceiveBufferBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Throws when a limit is below one, or <see cref="MaxSplitMessageBytes"/> is over <see cref="LargestSplitMessageBytes"/>.</summary>
    public SessionLimits Validated()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSplitCount, nameof(MaxSplitCount));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSplitMessageBytes, nameof(MaxSplitMessageBytes));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSplitMessageBytes, LargestSplitMessageBytes, nameof(MaxSplitMessageBytes));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxConcurrentSplits, nameof(MaxConcurrentSplits));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SplitReassemblyTimeoutUs, nameof(SplitReassemblyTimeoutUs));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxOrderingBufferMessagesPerChannel, nameof(MaxOrderingBufferMessagesPerChannel));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxReceiveBufferBytes, nameof(MaxReceiveBufferBytes));
        return this;
    }
}

/// <summary>Why a session closed its connection.</summary>
internal enum ConnectionDeathCause
{
    /// <summary>Reliable messages went unacknowledged with nothing heard for the timeout (ReliabilityLayer.cpp:1799-1808).</summary>
    AckTimeout,

    /// <summary>KillConnection was called.</summary>
    Killed,

    /// <summary>A split part declared more than <see cref="SessionLimits.MaxSplitCount"/> parts.</summary>
    SplitCountExceeded,

    /// <summary>A split message grew, or declared it would grow, past <see cref="SessionLimits.MaxSplitMessageBytes"/>.</summary>
    SplitMessageTooLarge,

    /// <summary>A new split message arrived while <see cref="SessionLimits.MaxConcurrentSplits"/> were incomplete.</summary>
    TooManyConcurrentSplits,

    /// <summary>An incomplete split message went <see cref="SessionLimits.SplitReassemblyTimeoutUs"/> without a new part.</summary>
    SplitReassemblyTimedOut,

    /// <summary>A reliable message had to wait on a channel already holding the most messages it may.</summary>
    OrderingBufferFull,

    /// <summary>A message or part had to be held with <see cref="SessionLimits.MaxReceiveBufferBytes"/> already used.</summary>
    ReceiveBufferFull,
}
