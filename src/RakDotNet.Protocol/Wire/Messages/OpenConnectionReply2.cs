using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REPLY_2: ID, offline magic, server GUID, the client's address as the server sees it, the MTU,
/// then requiresSecurity as a single bit (RakPeer.cpp:5300-5305; parsed at 4876-4889).
/// </summary>
/// <remarks>
/// <para>
/// The security flag is a C++ <c>bool</c>, which BitStream writes as one bit (BitStream.h:1130-1136), so it is the
/// top bit of a final, otherwise zero byte and the datagram is 35 bytes.
/// </para>
/// <para>
/// RakNet security is not supported. When <see cref="RequiresSecurity"/> is set, the server's handshake answer
/// follows (RakPeer.cpp:5405); <see cref="TryRead"/> then returns false with the fields before it filled in, and
/// <see cref="Write"/> throws <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public readonly record struct OpenConnectionReply2(
    ulong ServerGuid,
    IPEndPoint ClientAddress,
    ushort MtuSize,
    bool RequiresSecurity = false
) : IWireMessage<OpenConnectionReply2>
{
    public void Write(ref BitStreamWriter writer)
    {
        if (RequiresSecurity)
            throw new NotSupportedException("RakNet security is not supported.");
        MessageIdField.Write(ref writer, MessageId.OpenConnectionReply2);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(ServerGuid);
        writer.WriteAddress(ClientAddress);
        writer.WriteUInt16(MtuSize);
        writer.WriteBool(false);
    }

    public static bool TryRead(ref BitStreamReader reader, out OpenConnectionReply2 value)
    {
        value = default;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionReply2)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadUInt64(out var guid)
            || !r.TryReadAddress(out var address)
            || !r.TryReadUInt16(out var mtu)
            || !r.TryReadBool(out var requiresSecurity)
        )
            return false;
        value = new OpenConnectionReply2(guid, address!, mtu, requiresSecurity);
        if (requiresSecurity)
            return false;
        // The flag is the last field; skip the rest of its byte.
        r.AlignToByte();
        reader = r;
        return true;
    }
}
