namespace RakDotNet.Protocol.Wire;

/// <summary>
/// How a message is delivered. Values are positional in the C++ enum (PacketPriority.h:46-85, PacketReliability)
/// and are written on the wire in 3 bits.
/// </summary>
public enum Reliability : byte
{
    /// <summary>UNRELIABLE</summary>
    Unreliable = 0,

    /// <summary>UNRELIABLE_SEQUENCED</summary>
    UnreliableSequenced = 1,

    /// <summary>RELIABLE</summary>
    Reliable = 2,

    /// <summary>RELIABLE_ORDERED</summary>
    ReliableOrdered = 3,

    /// <summary>RELIABLE_SEQUENCED</summary>
    ReliableSequenced = 4,

    /// <summary>UNRELIABLE_WITH_ACK_RECEIPT. Never on the wire; sent as <see cref="Unreliable"/>.</summary>
    UnreliableWithAckReceipt = 5,

    /// <summary>RELIABLE_WITH_ACK_RECEIPT. Never on the wire; sent as <see cref="Reliable"/>.</summary>
    ReliableWithAckReceipt = 6,

    /// <summary>RELIABLE_ORDERED_WITH_ACK_RECEIPT. Never on the wire; sent as <see cref="ReliableOrdered"/>.</summary>
    ReliableOrderedWithAckReceipt = 7,
}

/// <summary>Send priority. Values are positional in the C++ enum (PacketPriority.h:21-40, PacketPriority).</summary>
public enum Priority : byte
{
    /// <summary>IMMEDIATE_PRIORITY</summary>
    Immediate = 0,

    /// <summary>HIGH_PRIORITY</summary>
    High = 1,

    /// <summary>MEDIUM_PRIORITY</summary>
    Medium = 2,

    /// <summary>LOW_PRIORITY</summary>
    Low = 3,
}

public static class ReliabilityExtensions
{
    /// <summary>Number of values in the C++ PacketReliability enum (NUMBER_OF_RELIABILITIES).</summary>
    public const int NumberOfReliabilities = 8;

    /// <summary>
    /// True for the types that are resent and carry a reliable message number.
    /// ReliabilityLayer.cpp:2634-2640 (WriteToBitStreamFromInternalPacket).
    /// </summary>
    public static bool IsReliable(this Reliability reliability) =>
        reliability
            is Reliability.Reliable
                or Reliability.ReliableOrdered
                or Reliability.ReliableSequenced
                or Reliability.ReliableWithAckReceipt
                or Reliability.ReliableOrderedWithAckReceipt;

    /// <summary>True for RELIABLE_ORDERED and RELIABLE_ORDERED_WITH_ACK_RECEIPT.</summary>
    public static bool IsOrdered(this Reliability reliability) =>
        reliability is Reliability.ReliableOrdered or Reliability.ReliableOrderedWithAckReceipt;

    /// <summary>
    /// True for the types that carry a sequencing index. ReliabilityLayer.cpp:2643-2648.
    /// </summary>
    public static bool IsSequenced(this Reliability reliability) =>
        reliability is Reliability.UnreliableSequenced or Reliability.ReliableSequenced;

    /// <summary>
    /// True for the types that carry an ordering index and ordering channel: ordered and sequenced.
    /// ReliabilityLayer.cpp:2650-2658.
    /// </summary>
    public static bool HasOrderingChannel(this Reliability reliability) =>
        reliability.IsOrdered() || reliability.IsSequenced();

    /// <summary>True for the *_WITH_ACK_RECEIPT variants (PacketPriority.h:45, "I check on >= UNRELIABLE_WITH_ACK_RECEIPT").</summary>
    public static bool HasAckReceipt(this Reliability reliability) =>
        reliability is >= Reliability.UnreliableWithAckReceipt and <= Reliability.ReliableOrderedWithAckReceipt;

    /// <summary>
    /// The reliability RakNet writes on the wire: ack-receipt variants become their base types. The mapping happens
    /// only when the encapsulated message header is serialised (ReliabilityLayer.cpp:2619-2626); the sender keeps the
    /// receipt variant locally to generate ID_SND_RECEIPT_ACKED.
    /// </summary>
    public static Reliability ToWireReliability(this Reliability reliability) =>
        reliability switch
        {
            Reliability.UnreliableWithAckReceipt => Reliability.Unreliable,
            Reliability.ReliableWithAckReceipt => Reliability.Reliable,
            Reliability.ReliableOrderedWithAckReceipt => Reliability.ReliableOrdered,
            _ => reliability,
        };

    /// <summary>
    /// True for the five base types, the only ones RakNet ever puts on the wire. The C++ decoder only rejects
    /// values &gt;= NUMBER_OF_RELIABILITIES (ReliabilityLayer.cpp:2762), but ack-receipt values never arrive from a
    /// RakNet peer and would decode asymmetrically there (6 skips the message number, 7 reads the ordering fields).
    /// </summary>
    public static bool IsValidOnWire(this Reliability reliability) => reliability <= Reliability.ReliableSequenced;
}
