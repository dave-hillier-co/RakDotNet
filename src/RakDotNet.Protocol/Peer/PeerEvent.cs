using System.Buffers.Binary;
using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Peer;

/// <summary>
/// One entry of a peer's ordered event stream: connection events and received messages together, in the order
/// RakPeer::Receive would return them.
/// </summary>
/// <remarks>
/// Each event knows the <see cref="Packet"/> RakPeer would hand the application for it (Packet::data), so a
/// Receive()-style facade only has to forward it. Typed properties carry what the packet means.
/// </remarks>
/// <param name="Remote">The remote system's address (Packet::systemAddress).</param>
/// <param name="Guid">The remote system's GUID, or <see cref="Wire.Messages.RakNetConstants.UnassignedGuid"/> (Packet::guid).</param>
internal abstract record PeerEvent(IPEndPoint Remote, ulong Guid)
{
    public IPEndPoint Remote { get; private protected set; } = Remote;

    public ulong Guid { get; private protected set; } = Guid;

    /// <summary>The packet data RakPeer would return, starting with its message ID.</summary>
    public abstract ReadOnlyMemory<byte> Packet { get; }

    public MessageId Id => (MessageId)Packet.Span[0];
}

/// <summary>ID_UNCONNECTED_PING or ID_UNCONNECTED_PING_OPEN_CONNECTIONS was answered (RakPeer.cpp:4672-4678).</summary>
internal sealed record UnconnectedPingReceived(IPEndPoint Remote, ulong Guid, bool OpenConnectionsOnly) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet =>
        new[] { (byte)(OpenConnectionsOnly ? MessageId.UnconnectedPingOpenConnections : MessageId.UnconnectedPing) };
}

/// <summary>
/// ID_UNCONNECTED_PONG arrived. RakPeer rewrites it for the application as ID, the ping time as a 4-byte big-endian
/// TimeMS, then the response data (RakPeer.cpp:4682-4704).
/// </summary>
internal sealed record UnconnectedPongReceived(IPEndPoint Remote, ulong Guid, ulong SendPingTime, ReadOnlyMemory<byte> ResponseData)
    : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet
    {
        get
        {
            var packet = new byte[1 + sizeof(uint) + ResponseData.Length];
            packet[0] = (byte)MessageId.UnconnectedPong;
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1), (uint)SendPingTime);
            ResponseData.Span.CopyTo(packet.AsSpan(1 + sizeof(uint)));
            return packet;
        }
    }
}

/// <summary>
/// A connection attempt ended without a connection. <see cref="Reason"/> is the packet's ID: ID_CONNECTION_ATTEMPT_FAILED
/// when no reply came or no slot was free, the rejection the remote system sent (ID_ALREADY_CONNECTED,
/// ID_NO_FREE_INCOMING_CONNECTIONS, ID_INCOMPATIBLE_PROTOCOL_VERSION, ID_CONNECTION_BANNED, ID_IP_RECENTLY_CONNECTED,
/// ID_INVALID_PASSWORD), or a security mismatch found in the offline handshake (ID_OUR_SYSTEM_REQUIRES_SECURITY,
/// ID_PUBLIC_KEY_MISMATCH, ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY).
/// </summary>
/// <remarks>
/// The three security reasons leave the attempt running, as in RakPeer, so they can repeat, one per reply, and are
/// followed by ID_CONNECTION_ATTEMPT_FAILED once the attempts run out.
/// </remarks>
/// <param name="Remote">The address the attempt was made to.</param>
/// <param name="Guid">The remote GUID when the reply carried one.</param>
/// <param name="Data">The packet: the ID alone for offline rejections, timeouts, ID_OUR_SYSTEM_REQUIRES_SECURITY and
/// ID_PUBLIC_KEY_MISMATCH (RakPeer.cpp:5119-5124, 5723-5727, 4795-4800, 4832-4837); the ID and 0 for
/// ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY (RakPeer.cpp:4922-4928); the whole message for ID_INVALID_PASSWORD
/// (RakPeer.cpp:6112-6118).</param>
internal sealed record ConnectionAttemptFailed(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Data) : PeerEvent(Remote, Guid)
{
    public MessageId Reason => (MessageId)Data.Span[0];

    public override ReadOnlyMemory<byte> Packet => Data;
}

/// <summary>
/// This peer's connection attempt was accepted: ID_CONNECTION_REQUEST_ACCEPTED, delivered whole (RakPeer.cpp:6184-6191).
/// </summary>
/// <param name="Remote">The server.</param>
/// <param name="Guid">The server's GUID.</param>
/// <param name="Data">The ID_CONNECTION_REQUEST_ACCEPTED message as received.</param>
/// <param name="ExternalAddress">This peer's address as the remote system sees it.</param>
internal sealed record OutgoingConnectionCompleted(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Data, IPEndPoint ExternalAddress)
    : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => Data;
}

/// <summary>
/// A remote system finished connecting to this peer: ID_NEW_INCOMING_CONNECTION, delivered whole
/// (RakPeer.cpp:6044-6051).
/// </summary>
/// <param name="Remote">The client.</param>
/// <param name="Guid">The client's GUID.</param>
/// <param name="Data">The ID_NEW_INCOMING_CONNECTION message as received.</param>
internal sealed record IncomingConnectionCompleted(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Data) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => Data;
}

/// <summary>An application message arrived (RakPeer.cpp:6226-6237).</summary>
/// <remarks>
/// The core reuses its instances: one handed back through <see cref="PeerCore.Recycle"/> may be delivered again for
/// another message, so a consumer that recycles an event must be done with it and its data. One that is never
/// recycled keeps its data.
/// </remarks>
/// <param name="Remote">The sender.</param>
/// <param name="Guid">The sender's GUID.</param>
/// <param name="Data">The message, starting with its ID byte.</param>
internal sealed record MessageReceived(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Data) : PeerEvent(Remote, Guid)
{
    private byte[] _buffer = [];

    public ReadOnlyMemory<byte> Data { get; private set; } = Data;

    public override ReadOnlyMemory<byte> Packet => Data;

    /// <summary>The length in bits the reliability layer delivered (Packet::bitSize, RakPeer.cpp:6231); whole bytes unless set.</summary>
    public int BitLength { get; internal set; } = Data.Length * 8;

    /// <summary>Makes this the event for another message, copied into its own buffer.</summary>
    internal void CopyFrom(IPEndPoint remote, ulong guid, ReadOnlySpan<byte> data, int bitLength)
    {
        if (_buffer.Length < data.Length)
            _buffer = new byte[Math.Max(data.Length, 64)];
        data.CopyTo(_buffer);
        Remote = remote;
        Guid = guid;
        Data = _buffer.AsMemory(0, data.Length);
        BitLength = bitLength;
    }
}

/// <summary>
/// ID_SND_RECEIPT_ACKED: a message sent with an ack-receipt reliability was acknowledged. The serial follows the ID in
/// host byte order, little-endian on every supported platform (memcpy at ReliabilityLayer.cpp:2494).
/// </summary>
internal sealed record SendReceiptAcked(IPEndPoint Remote, ulong Guid, uint Receipt) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => ReceiptPacket(MessageId.SndReceiptAcked, Receipt);

    internal static byte[] ReceiptPacket(MessageId id, uint receipt)
    {
        var packet = new byte[1 + sizeof(uint)];
        packet[0] = (byte)id;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), receipt);
        return packet;
    }
}

/// <summary>ID_SND_RECEIPT_LOSS: an UNRELIABLE_WITH_ACK_RECEIPT message was not acknowledged in time.</summary>
internal sealed record SendReceiptLost(IPEndPoint Remote, ulong Guid, uint Receipt) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => SendReceiptAcked.ReceiptPacket(MessageId.SndReceiptLoss, Receipt);
}

/// <summary>
/// An UNRELIABLE_WITH_ACK_RECEIPT message was discarded unsent after waiting longer than the unreliable timeout
/// (ReliabilityLayer.cpp:1745-1793). RakNet reports nothing for it, so this event has no RakNet packet; it lets the
/// layer above end the receipt.
/// </summary>
internal sealed record SendReceiptDiscarded(IPEndPoint Remote, ulong Guid, uint Receipt) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => ReadOnlyMemory<byte>.Empty;
}

/// <summary>
/// ID_DISCONNECTION_NOTIFICATION: the connection closed cleanly, because the remote system said so and its notification
/// was acknowledged, or because this peer closed it and its own notification was delivered (RakPeer.cpp:5889-5913).
/// </summary>
internal sealed record Disconnected(IPEndPoint Remote, ulong Guid) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => new[] { (byte)MessageId.DisconnectionNotification };
}

/// <summary>
/// ID_CONNECTION_LOST: a connected system stopped acknowledging, or this peer closed the connection without
/// notification (RakPeer.cpp:1667-1678, 5900-5901).
/// </summary>
internal sealed record ConnectionLost(IPEndPoint Remote, ulong Guid) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => new[] { (byte)MessageId.ConnectionLost };
}

/// <summary>
/// ID_ADVERTISE_SYSTEM arrived out of band (RakPeer.cpp:4719-4724): the packet is the ID followed by
/// <see cref="Data"/>, with the out-of-band header removed.
/// </summary>
/// <param name="Remote">The advertising system.</param>
/// <param name="Guid">Its GUID, from the out-of-band header.</param>
/// <param name="Data">What it advertised.</param>
internal sealed record AdvertiseSystemReceived(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Data) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => Prefixed(MessageId.AdvertiseSystem, Data.Span);

    internal static byte[] Prefixed(MessageId id, ReadOnlySpan<byte> data)
    {
        var packet = new byte[1 + data.Length];
        packet[0] = (byte)id;
        data.CopyTo(packet.AsSpan(1));
        return packet;
    }
}

/// <summary>
/// Out-of-band data other than ID_ADVERTISE_SYSTEM arrived (RakPeer.cpp:4726-4729): the packet is
/// ID_OUT_OF_BAND_INTERNAL followed by <see cref="Payload"/>.
/// </summary>
/// <param name="Remote">The sender.</param>
/// <param name="Guid">Its GUID, from the out-of-band header.</param>
/// <param name="Payload">The data sent, starting with its own message ID.</param>
internal sealed record OutOfBandReceived(IPEndPoint Remote, ulong Guid, ReadOnlyMemory<byte> Payload) : PeerEvent(Remote, Guid)
{
    public override ReadOnlyMemory<byte> Packet => AdvertiseSystemReceived.Prefixed(MessageId.OutOfBandInternal, Payload.Span);
}
