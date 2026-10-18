using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>
/// Iterates the encapsulated messages in the body of a data datagram, slicing each payload from the input.
/// </summary>
/// <remarks>
/// Mirrors the parse loop in ReliabilityLayer::HandleSocketReceiveFromConnectedPlayer (ReliabilityLayer.cpp:888-1494):
/// messages are read until fewer than 32 bits remain (line 2690), and the first malformed message ends the datagram
/// while the messages before it stand. <see cref="IsMalformed"/> tells the two endings apart.
/// </remarks>
public struct EncapsulatedMessageReader
{
    private readonly ReadOnlyMemory<byte> _body;
    private int _offset;

    /// <param name="body">The datagram after its <see cref="DatagramHeader"/>. Messages start byte-aligned.</param>
    public EncapsulatedMessageReader(ReadOnlyMemory<byte> body)
    {
        _body = body;
    }

    /// <summary>True when reading stopped at a message that failed to decode.</summary>
    public bool IsMalformed { get; private set; }

    /// <summary>Bytes of the body consumed by the messages read so far.</summary>
    public readonly int BytesConsumed => _offset;

    public bool TryReadNext([NotNullWhen(true)] out EncapsulatedMessage? message)
    {
        message = null;
        if (IsMalformed || (_body.Length - _offset) * 8 < EncapsulatedMessage.MinimumBitsToAttempt)
            return false;
        if (!EncapsulatedMessage.TryRead(_body[_offset..], out message, out var bytesRead))
        {
            IsMalformed = true;
            return false;
        }
        _offset += bytesRead;
        return true;
    }

    /// <summary>
    /// Reads the <see cref="DatagramHeader"/> of <paramref name="datagram"/> and, when it is a data datagram, returns a
    /// reader over its messages.
    /// </summary>
    public static bool TryOpenDatagram(
        ReadOnlyMemory<byte> datagram,
        out DatagramHeader header,
        out EncapsulatedMessageReader messages
    )
    {
        messages = default;
        var reader = new BitStreamReader(datagram.Span);
        if (!DatagramHeader.TryRead(ref reader, out header) || header.Kind != DatagramKind.Data)
            return false;
        messages = new EncapsulatedMessageReader(datagram[(reader.BitPosition >> 3)..]);
        return true;
    }

    /// <summary>
    /// Adds every message in <paramref name="body"/> to <paramref name="messages"/>. Returns false when a malformed
    /// message ended the body; the messages before it have been added.
    /// </summary>
    public static bool TryReadAll(ReadOnlyMemory<byte> body, ICollection<EncapsulatedMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var reader = new EncapsulatedMessageReader(body);
        while (reader.TryReadNext(out var message))
            messages.Add(message);
        return !reader.IsMalformed;
    }
}
