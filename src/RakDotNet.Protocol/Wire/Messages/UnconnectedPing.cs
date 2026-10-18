namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_UNCONNECTED_PING or ID_UNCONNECTED_PING_OPEN_CONNECTIONS: ID, RakNet::Time (64-bit by default,
/// RakNetTime.h:22), offline magic, sender GUID (RakPeer.cpp:2101-2111).
/// </summary>
/// <param name="OpenConnectionsOnly">
/// True for ID_UNCONNECTED_PING_OPEN_CONNECTIONS, which a server answers only while it accepts incoming
/// connections (RakPeer.cpp:4638-4639).
/// </param>
/// <param name="SendPingTime">The sender's clock, echoed in the pong.</param>
/// <param name="ClientGuid">
/// The sender's GUID. The C++ server accepts a ping that stops after the magic (RakPeer.cpp:4636, 4646-4647), so a
/// missing or partial GUID decodes as null, and null encodes no GUID.
/// </param>
public readonly record struct UnconnectedPing(bool OpenConnectionsOnly, ulong SendPingTime, ulong? ClientGuid)
    : IWireMessage<UnconnectedPing>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, OpenConnectionsOnly ? MessageId.UnconnectedPingOpenConnections : MessageId.UnconnectedPing);
        writer.WriteUInt64(SendPingTime);
        OfflineMagic.Write(ref writer);
        if (ClientGuid is { } guid)
            writer.WriteUInt64(guid);
    }

    public static bool TryRead(ref BitStreamReader reader, out UnconnectedPing value)
    {
        value = default;
        var r = reader;
        if (!r.TryReadByte(out var id))
            return false;
        bool openConnectionsOnly;
        if (id == (byte)MessageId.UnconnectedPing)
            openConnectionsOnly = false;
        else if (id == (byte)MessageId.UnconnectedPingOpenConnections)
            openConnectionsOnly = true;
        else
            return false;
        if (!r.TryReadUInt64(out var time) || !OfflineMagic.TryRead(ref r))
            return false;
        // A partial GUID is left unread: BitStream::Read fails and the server keeps UNASSIGNED_RAKNET_GUID.
        ulong? guid = r.TryReadUInt64(out var g) ? g : null;
        value = new UnconnectedPing(openConnectionsOnly, time, guid);
        reader = r;
        return true;
    }
}
