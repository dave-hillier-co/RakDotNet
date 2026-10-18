using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_ADVERTISE_SYSTEM as it appears on the wire. RakPeer::AdvertiseSystem (RakPeer.cpp:2683-2689) sends it
/// through SendOutOfBand, so the datagram is an out-of-band header (ID_OUT_OF_BAND_INTERNAL, sender GUID, offline
/// magic; RakPeer.cpp:2979-2984) followed by the ID_ADVERTISE_SYSTEM byte and the advertised data.
/// </summary>
/// <remarks>
/// The receiver (RakPeer.cpp:4706-4725) strips the header and hands the application ID_ADVERTISE_SYSTEM + data.
/// It only accepts datagrams shorter than <see cref="RakNetConstants.MaxOfflineDataLength"/> + 25 bytes, which
/// leaves at most <see cref="MaxDataLength"/> bytes of data; longer input fails to decode.
/// </remarks>
public sealed record AdvertiseSystem(ulong SenderGuid, ReadOnlyMemory<byte> Data) : IWireMessage<AdvertiseSystem>
{
    /// <summary>The longest data the C++ receiver accepts: the out-of-band payload, including the ID byte, is under 400 bytes.</summary>
    public const int MaxDataLength = RakNetConstants.MaxOfflineDataLength - 2;

    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.OutOfBandInternal);
        writer.WriteUInt64(SenderGuid);
        OfflineMagic.Write(ref writer);
        MessageIdField.Write(ref writer, MessageId.AdvertiseSystem);
        writer.WriteBytes(Data.Span);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out AdvertiseSystem value)
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OutOfBandInternal)
            || !r.TryReadUInt64(out var guid)
            || !OfflineMagic.TryRead(ref r)
            || !MessageIdField.TryRead(ref r, MessageId.AdvertiseSystem)
        )
            return false;
        var length = r.BitsRemaining / 8;
        if (length > MaxDataLength || !r.TryReadAlignedBytes(length, out var data))
            return false;
        value = new AdvertiseSystem(guid, data.ToArray());
        reader = r;
        return true;
    }
}
