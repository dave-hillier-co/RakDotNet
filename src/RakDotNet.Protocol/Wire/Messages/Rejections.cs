namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>The common layout of the offline connection rejections: ID, offline magic, server GUID.</summary>
internal static class Rejection
{
    public static void Write(ref BitStreamWriter writer, MessageId id, ulong serverGuid)
    {
        MessageIdField.Write(ref writer, id);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(serverGuid);
    }

    public static bool TryRead(ref BitStreamReader reader, MessageId id, out ulong serverGuid)
    {
        serverGuid = 0;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, id) || !OfflineMagic.TryRead(ref r) || !r.TryReadUInt64(out serverGuid))
            return false;
        reader = r;
        return true;
    }
}

/// <summary>
/// ID_ALREADY_CONNECTED: ID, offline magic, server GUID (RakPeer.cpp:5334-5336). Sent in reply to
/// ID_OPEN_CONNECTION_REQUEST_2 when the address or GUID is already in use; parsed at RakPeer.cpp:5084-5091.
/// </summary>
public readonly record struct AlreadyConnected(ulong ServerGuid) : IWireMessage<AlreadyConnected>
{
    public void Write(ref BitStreamWriter writer) => Rejection.Write(ref writer, MessageId.AlreadyConnected, ServerGuid);

    public static bool TryRead(ref BitStreamReader reader, out AlreadyConnected value)
    {
        var ok = Rejection.TryRead(ref reader, MessageId.AlreadyConnected, out var guid);
        value = ok ? new AlreadyConnected(guid) : default;
        return ok;
    }
}

/// <summary>
/// ID_NO_FREE_INCOMING_CONNECTIONS: ID, offline magic, server GUID (RakPeer.cpp:5351-5353).
/// </summary>
public readonly record struct NoFreeIncomingConnections(ulong ServerGuid) : IWireMessage<NoFreeIncomingConnections>
{
    public void Write(ref BitStreamWriter writer) =>
        Rejection.Write(ref writer, MessageId.NoFreeIncomingConnections, ServerGuid);

    public static bool TryRead(ref BitStreamReader reader, out NoFreeIncomingConnections value)
    {
        var ok = Rejection.TryRead(ref reader, MessageId.NoFreeIncomingConnections, out var guid);
        value = ok ? new NoFreeIncomingConnections(guid) : default;
        return ok;
    }
}

/// <summary>
/// ID_CONNECTION_BANNED: ID, offline magic, server GUID (RakPeer.cpp:4556-4558). Sent in reply to any datagram from
/// a banned address.
/// </summary>
public readonly record struct ConnectionBanned(ulong ServerGuid) : IWireMessage<ConnectionBanned>
{
    public void Write(ref BitStreamWriter writer) => Rejection.Write(ref writer, MessageId.ConnectionBanned, ServerGuid);

    public static bool TryRead(ref BitStreamReader reader, out ConnectionBanned value)
    {
        var ok = Rejection.TryRead(ref reader, MessageId.ConnectionBanned, out var guid);
        value = ok ? new ConnectionBanned(guid) : default;
        return ok;
    }
}

/// <summary>
/// ID_IP_RECENTLY_CONNECTED: ID, offline magic, server GUID (RakPeer.cpp:5371-5373).
/// </summary>
public readonly record struct IpRecentlyConnected(ulong ServerGuid) : IWireMessage<IpRecentlyConnected>
{
    public void Write(ref BitStreamWriter writer) => Rejection.Write(ref writer, MessageId.IpRecentlyConnected, ServerGuid);

    public static bool TryRead(ref BitStreamReader reader, out IpRecentlyConnected value)
    {
        var ok = Rejection.TryRead(ref reader, MessageId.IpRecentlyConnected, out var guid);
        value = ok ? new IpRecentlyConnected(guid) : default;
        return ok;
    }
}

/// <summary>
/// ID_INCOMPATIBLE_PROTOCOL_VERSION: ID, the server's protocol version, offline magic, server GUID
/// (RakPeer.cpp:5141-5144). Unlike the other rejections the magic is at offset 2.
/// </summary>
/// <remarks>
/// The C++ client only treats this as an offline message when the datagram is exactly 26 bytes
/// (RakPeer.cpp:4619-4620), so <see cref="TryRead"/> fails if anything follows the GUID.
/// </remarks>
public readonly record struct IncompatibleProtocolVersion(byte ProtocolVersion, ulong ServerGuid)
    : IWireMessage<IncompatibleProtocolVersion>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.IncompatibleProtocolVersion);
        writer.WriteByte(ProtocolVersion);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(ServerGuid);
    }

    public static bool TryRead(ref BitStreamReader reader, out IncompatibleProtocolVersion value)
    {
        value = default;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.IncompatibleProtocolVersion)
            || !r.TryReadByte(out var version)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadUInt64(out var guid)
            || r.BitsRemaining != 0
        )
            return false;
        value = new IncompatibleProtocolVersion(version, guid);
        reader = r;
        return true;
    }
}
