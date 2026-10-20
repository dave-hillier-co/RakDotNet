using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REQUEST_2: ID, offline magic, the server's address as the client addressed it, the MTU
/// echoed from ID_OPEN_CONNECTION_REPLY_1, client GUID (RakPeer.cpp:4758-4853; parsed at 5203-5253).
/// </summary>
/// <remarks>
/// This is the request to a server without security. When the server reported hasSecurity in reply 1, the client
/// inserts a cookie, a challenge flag and optionally a challenge right after the magic (RakPeer.cpp:4760-4821), the
/// form <see cref="SecureOpenConnectionRequest2"/> reads and writes. Nothing in the message says which form it is
/// (the server knows from its own configuration, RakPeer.cpp:5211).
/// </remarks>
public readonly record struct OpenConnectionRequest2(IPEndPoint ServerAddress, ushort MtuSize, ulong ClientGuid)
    : IWireMessage<OpenConnectionRequest2>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.OpenConnectionRequest2);
        OfflineMagic.Write(ref writer);
        writer.WriteAddress(ServerAddress);
        writer.WriteUInt16(MtuSize);
        writer.WriteUInt64(ClientGuid);
    }

    public static bool TryRead(ref BitStreamReader reader, out OpenConnectionRequest2 value)
    {
        value = default;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionRequest2)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadAddress(out var address)
            || !r.TryReadUInt16(out var mtu)
            || !r.TryReadUInt64(out var guid)
        )
            return false;
        value = new OpenConnectionRequest2(address!, mtu, guid);
        reader = r;
        return true;
    }
}
