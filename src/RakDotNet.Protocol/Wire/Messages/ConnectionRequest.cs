using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_CONNECTION_REQUEST, sent reliably once the offline handshake is done: ID, client GUID, RakNet::Time,
/// doSecurity as one byte, then the connection password as the remaining bytes (RakPeer.cpp:5006-5031; parsed by
/// ParseConnectionRequestPacket at RakPeer.cpp:3447-3456 and 3519-3520). It carries no offline magic.
/// </summary>
/// <remarks>
/// RakNet security is not supported. When <see cref="DoSecurity"/> is set, a proof and an optional identity sit
/// between the flag and the password (RakPeer.cpp:5013-5025); <see cref="TryRead"/> then returns false with the
/// GUID, time and flag filled in, and <see cref="Write"/> throws <see cref="NotSupportedException"/>.
/// </remarks>
public sealed record ConnectionRequest(ulong ClientGuid, ulong RequestTime, ReadOnlyMemory<byte> Password)
    : IWireMessage<ConnectionRequest>
{
    public bool DoSecurity { get; init; }

    public void Write(ref BitStreamWriter writer)
    {
        if (DoSecurity)
            throw new NotSupportedException("RakNet security is not supported.");
        MessageIdField.Write(ref writer, MessageId.ConnectionRequest);
        writer.WriteUInt64(ClientGuid);
        writer.WriteUInt64(RequestTime);
        writer.WriteByte(0); // RakPeer.cpp:5027
        writer.WriteBytes(Password.Span);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out ConnectionRequest value)
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.ConnectionRequest)
            || !r.TryReadUInt64(out var guid)
            || !r.TryReadUInt64(out var time)
            || !r.TryReadByte(out var doSecurity)
        )
            return false;
        if (doSecurity != 0)
        {
            value = new ConnectionRequest(guid, time, ReadOnlyMemory<byte>.Empty) { DoSecurity = true };
            return false;
        }
        // RakPeer.cpp:3519-3520: the password is every byte after the flag.
        if (!r.TryReadAlignedBytes(r.BitsRemaining / 8, out var password))
            return false;
        value = new ConnectionRequest(guid, time, password.ToArray());
        reader = r;
        return true;
    }
}
