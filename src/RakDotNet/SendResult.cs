using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>A send receipt number, as returned by RakPeer::Send and carried by receipt events.</summary>
public readonly record struct SendReceipt(uint Value)
{
    /// <summary>0: "assign one" when passed as <c>forceReceipt</c> (RakPeer.cpp:1326-1330); never assigned.</summary>
    public static SendReceipt None => default;
}

/// <summary>Whether a send was queued.</summary>
public enum SendStatus : byte
{
    Queued,

    /// <summary>The peer is shut down (RakPeer.cpp:1320-1321); the receipt is <see cref="SendReceipt.None"/>.</summary>
    NotActive,

    /// <summary>
    /// The target is not connected. The receipt was still taken from the counter, as RakNet assigns it before the
    /// update thread looks for the target (RakPeer.cpp:1349).
    /// </summary>
    NotConnected,
}

/// <summary>The synchronous result of a send.</summary>
public readonly record struct SendResult(SendStatus Status, SendReceipt Receipt)
{
    public bool IsQueued => Status == SendStatus.Queued;
}

/// <summary>How a tracked send ended.</summary>
public enum DeliveryStatus : byte
{
    /// <summary>ID_SND_RECEIPT_ACKED</summary>
    Acked,

    /// <summary>ID_SND_RECEIPT_LOSS; unreliable receipts only (ReliabilityLayer.cpp:1860-1876).</summary>
    Lost,

    /// <summary>The connection ended first.</summary>
    Abandoned,

    /// <summary>The send returned <see cref="SendStatus.NotActive"/> or <see cref="SendStatus.NotConnected"/>.</summary>
    NotSent,

    /// <summary>The peer shut down first.</summary>
    PeerShutdown,
}

/// <summary>The outcome of <see cref="RakPeer.SendAndTrackAsync"/> or <see cref="RakConnection.SendAndTrackAsync"/>.</summary>
/// <remarks><c>SendStatus</c> is the synchronous send result, which explains <see cref="DeliveryStatus.NotSent"/>.</remarks>
public readonly record struct DeliveryOutcome(DeliveryStatus Status, SendReceipt Receipt, SendStatus SendStatus);

/// <summary>
/// Writes a message straight into the pooled outgoing segment. The writer's capacity is at least the send's
/// <c>sizeHint</c> bytes, and writing beyond it throws. A static lambda allocates nothing.
/// </summary>
public delegate void MessageWriter<TState>(ref BitStreamWriter writer, TState state)
    where TState : allows ref struct;
