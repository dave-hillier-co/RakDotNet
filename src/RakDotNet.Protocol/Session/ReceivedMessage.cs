using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// A received message on its way to the application: an unsplit <see cref="EncapsulatedMessage"/>, or one rebuilt from
/// its split parts, which can be far longer than one encapsulated message can carry.
/// </summary>
/// <param name="Reliability">As sent on the wire.</param>
/// <param name="Payload">The message, <c>ceil(BitLength / 8)</c> bytes.</param>
/// <param name="BitLength">The message length in bits.</param>
/// <param name="SequencingIndex">For sequenced reliabilities.</param>
/// <param name="OrderingIndex">For ordered and sequenced reliabilities.</param>
/// <param name="OrderingChannel">For ordered and sequenced reliabilities, 0 to 31.</param>
internal sealed record ReceivedMessage(
    Reliability Reliability,
    ReadOnlyMemory<byte> Payload,
    int BitLength,
    UInt24 SequencingIndex = default,
    UInt24 OrderingIndex = default,
    byte OrderingChannel = 0
)
{
    public static ReceivedMessage From(EncapsulatedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ReceivedMessage(
            message.Reliability,
            message.Payload,
            message.PayloadBitLength,
            message.SequencingIndex,
            message.OrderingIndex,
            message.OrderingChannel
        );
    }
}
