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
/// (ReliabilityLayer.cpp:1799-1808), or it was killed. Raised once; nothing is sent afterwards.
/// </summary>
internal sealed record ConnectionDied : SessionEvent;
