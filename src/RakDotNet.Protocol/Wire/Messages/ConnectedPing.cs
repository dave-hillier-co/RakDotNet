namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>ID_CONNECTED_PING: ID, the sender's RakNet::Time (RakPeer.cpp:4108-4110).</summary>
/// <remarks>
/// The C++ peer only answers a ping of exactly 9 bytes (RakPeer.cpp:6077), so <see cref="TryRead"/> fails if
/// anything follows the time.
/// </remarks>
public readonly record struct ConnectedPing(ulong SendPingTime) : IWireMessage<ConnectedPing>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.ConnectedPing);
        writer.WriteUInt64(SendPingTime);
    }

    public static bool TryRead(ref BitStreamReader reader, out ConnectedPing value)
    {
        value = default;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, MessageId.ConnectedPing) || !r.TryReadUInt64(out var time) || r.BitsRemaining != 0)
            return false;
        value = new ConnectedPing(time);
        reader = r;
        return true;
    }
}

/// <summary>ID_CONNECTED_PONG: ID, the echoed ping time, the responder's time (RakPeer.cpp:6085-6087).</summary>
/// <remarks>
/// The C++ peer only accepts a pong of exactly 17 bytes (RakPeer.cpp:6060), so <see cref="TryRead"/> fails if
/// anything follows the second time.
/// </remarks>
public readonly record struct ConnectedPong(ulong SendPingTime, ulong SendPongTime) : IWireMessage<ConnectedPong>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.ConnectedPong);
        writer.WriteUInt64(SendPingTime);
        writer.WriteUInt64(SendPongTime);
    }

    public static bool TryRead(ref BitStreamReader reader, out ConnectedPong value)
    {
        value = default;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.ConnectedPong)
            || !r.TryReadUInt64(out var pingTime)
            || !r.TryReadUInt64(out var pongTime)
            || r.BitsRemaining != 0
        )
            return false;
        value = new ConnectedPong(pingTime, pongTime);
        reader = r;
        return true;
    }
}

/// <summary>ID_DISCONNECTION_NOTIFICATION: the ID byte alone (RakPeer.cpp:3559-3560).</summary>
/// <remarks>The C++ peer checks only the ID (RakPeer.cpp:6095); trailing bytes are left unread.</remarks>
public readonly record struct DisconnectionNotification : IWireMessage<DisconnectionNotification>
{
    public void Write(ref BitStreamWriter writer) => MessageIdField.Write(ref writer, MessageId.DisconnectionNotification);

    public static bool TryRead(ref BitStreamReader reader, out DisconnectionNotification value)
    {
        value = default;
        var r = reader;
        if (!MessageIdField.TryRead(ref r, MessageId.DisconnectionNotification))
            return false;
        reader = r;
        return true;
    }
}
