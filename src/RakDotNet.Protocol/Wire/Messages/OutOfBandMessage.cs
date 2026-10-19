using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// An ID_OUT_OF_BAND_INTERNAL datagram, as RakPeer::SendOutOfBand sends it (RakPeer.cpp:2997-3040): the out-of-band
/// header (ID_OUT_OF_BAND_INTERNAL, sender GUID, offline magic; WriteOutOfBandHeader, RakPeer.cpp:2979-2984) followed by
/// the caller's bytes. <see cref="AdvertiseSystem"/> is the form whose payload starts with ID_ADVERTISE_SYSTEM.
/// </summary>
/// <remarks>
/// The receiver (RakPeer.cpp:4706-4730) only accepts datagrams longer than the 25-byte header and shorter than
/// <see cref="RakNetConstants.MaxOfflineDataLength"/> + 25 bytes, so <see cref="TryRead"/> requires a payload of 1 to
/// <see cref="MaxPayloadLength"/> bytes.
/// </remarks>
/// <param name="SenderGuid">The sender's GUID.</param>
/// <param name="Payload">Everything after the header, starting with the inner message ID.</param>
public sealed record OutOfBandMessage(ulong SenderGuid, ReadOnlyMemory<byte> Payload) : IWireMessage<OutOfBandMessage>
{
    /// <summary>The longest payload the C++ receiver accepts.</summary>
    public const int MaxPayloadLength = RakNetConstants.MaxOfflineDataLength - 1;

    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.OutOfBandInternal);
        writer.WriteUInt64(SenderGuid);
        OfflineMagic.Write(ref writer);
        writer.WriteBytes(Payload.Span);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out OutOfBandMessage value)
    {
        value = null;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, MessageId.OutOfBandInternal) || !r.TryReadUInt64(out var guid) || !OfflineMagic.TryRead(ref r))
            return false;
        var length = r.BitsRemaining / 8;
        if (length is < 1 or > MaxPayloadLength || !r.TryReadAlignedBytes(length, out var payload))
            return false;
        value = new OutOfBandMessage(guid, payload.ToArray());
        reader = r;
        return true;
    }
}
