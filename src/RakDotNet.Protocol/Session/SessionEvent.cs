namespace RakDotNet.Protocol.Session;

/// <summary>
/// Something that happened on a connection, in the order it happened: the contents of the C++ outputQueue
/// (ReliabilityLayer.cpp, Receive at 1507-1530) plus the dead-connection flag.
/// </summary>
internal abstract record SessionEvent;

/// <summary>A message arrived and its ordering and sequencing allow it through.</summary>
/// <param name="Payload">The message, including its message ID byte. The session owns a copy.</param>
/// <param name="BitLength">The payload length in bits, as sent.</param>
internal sealed record MessageDelivered(ReadOnlyMemory<byte> Payload, int BitLength) : SessionEvent
{
    public bool Equals(MessageDelivered? other) =>
        other is not null && BitLength == other.BitLength && Payload.Span.SequenceEqual(other.Payload.Span);

    public override int GetHashCode() => BitLength;
}

/// <summary>
/// The remote system acknowledged a message sent with an ack-receipt reliability: ID_SND_RECEIPT_ACKED
/// (ReliabilityLayer.cpp:757-762, 2486-2496).
/// </summary>
internal sealed record ReceiptAcknowledged(uint Receipt) : SessionEvent;

/// <summary>
/// An UNRELIABLE_WITH_ACK_RECEIPT message was not acknowledged within the retransmission timeout:
/// ID_SND_RECEIPT_LOSS (ReliabilityLayer.cpp:1860-1881).
/// </summary>
internal sealed record ReceiptLost(uint Receipt) : SessionEvent;

/// <summary>
/// The connection is dead: reliable messages went unacknowledged with nothing heard for the timeout
/// (ReliabilityLayer.cpp:1799-1808), it was killed, or the remote system exceeded a <see cref="SessionLimits"/> limit.
/// Raised once; nothing is sent or received afterwards.
/// </summary>
internal sealed record ConnectionDied(ConnectionDeathCause Cause = ConnectionDeathCause.AckTimeout) : SessionEvent;

/// <summary>
/// An UNRELIABLE_WITH_ACK_RECEIPT message was discarded unsent because it waited in the send queue longer than the
/// unreliable timeout (ReliabilityLayer.cpp:1745-1793). RakNet reports nothing for it; this event lets the peer
/// complete the receipt without inventing an ID_SND_RECEIPT_LOSS RakNet would not send.
/// </summary>
internal sealed record ReceiptDiscarded(uint Receipt) : SessionEvent;
