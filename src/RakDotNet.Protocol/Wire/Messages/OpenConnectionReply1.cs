namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REPLY_1: ID, offline magic, server GUID, hasSecurity as one byte, then the MTU as a uint16
/// (RakPeer.cpp:5164-5186; parsed at 4742-4755 and 4845).
/// </summary>
/// <remarks>
/// RakNet security is not supported. When <see cref="HasSecurity"/> is set, a cookie and the server's public key
/// sit between the flag and the MTU (RakPeer.cpp:5170-5176); <see cref="TryRead"/> then returns false with
/// <see cref="ServerGuid"/> and <see cref="HasSecurity"/> filled in so the caller can report why, and
/// <see cref="Write"/> throws <see cref="NotSupportedException"/>.
/// </remarks>
public readonly record struct OpenConnectionReply1(ulong ServerGuid, ushort MtuSize, bool HasSecurity = false)
    : IWireMessage<OpenConnectionReply1>
{
    public void Write(ref BitStreamWriter writer)
    {
        if (HasSecurity)
            throw new NotSupportedException("RakNet security is not supported.");
        MessageIdField.Write(ref writer, MessageId.OpenConnectionReply1);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(ServerGuid);
        writer.WriteByte(0); // RakPeer.cpp:5180 - HasCookie no
        writer.WriteUInt16(MtuSize);
    }

    public static bool TryRead(ref BitStreamReader reader, out OpenConnectionReply1 value)
    {
        value = default;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionReply1)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadUInt64(out var guid)
            || !r.TryReadByte(out var hasSecurity)
        )
            return false;
        // RakPeer.cpp:4752 treats any non-zero byte as security.
        if (hasSecurity != 0)
        {
            value = new OpenConnectionReply1(guid, 0, HasSecurity: true);
            return false;
        }
        if (!r.TryReadUInt16(out var mtu))
            return false;
        value = new OpenConnectionReply1(guid, mtu);
        reader = r;
        return true;
    }
}
