using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// The ID_TIMESTAMP prefix: the ID then the sender's RakNet::Time as a big-endian u64. A receiving peer shifts the
/// time into its own clock in place before delivering the packet (RakPeer.cpp:1549-1555).
/// </summary>
public readonly record struct TimestampHeader(ulong Time) : IWireMessage<TimestampHeader>
{
    /// <summary>The ID and the time.</summary>
    public const int Length = 1 + sizeof(ulong);

    public void Write(ref BitStreamWriter writer)
    {
        writer.WriteByte((byte)MessageId.Timestamp);
        writer.WriteUInt64(Time);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out TimestampHeader value)
    {
        value = default;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, MessageId.Timestamp) || !r.TryReadUInt64(out var time))
            return false;
        value = new TimestampHeader(time);
        reader = r;
        return true;
    }
}

/// <summary>
/// RakPeer::WriteOutOfBandHeader: ID_OUT_OF_BAND_INTERNAL, the sender's GUID, then the offline magic
/// (RakPeer.cpp:2979-2985).
/// </summary>
public readonly record struct OutOfBandHeader(RakNetGuid Sender) : IWireMessage<OutOfBandHeader>
{
    /// <summary>The ID, the GUID and the magic.</summary>
    public const int Length = 1 + sizeof(ulong) + OfflineMagic.Length;

    public void Write(ref BitStreamWriter writer)
    {
        writer.WriteByte((byte)MessageId.OutOfBandInternal);
        writer.WriteUInt64(Sender.Value);
        OfflineMagic.Write(ref writer);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out OutOfBandHeader value)
    {
        value = default;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, MessageId.OutOfBandInternal) || !r.TryReadUInt64(out var guid) || !OfflineMagic.TryRead(ref r))
            return false;
        value = new OutOfBandHeader(new RakNetGuid(guid));
        reader = r;
        return true;
    }
}
