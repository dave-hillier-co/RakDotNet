using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REPLY_1 from a server with security on: ID, offline magic, server GUID, hasSecurity as one
/// non-zero byte, the cookie as a uint32, the server's public key as aligned bytes, then the MTU as a uint16
/// (RakPeer.cpp:5164-5186; parsed at 4742-4755, 4767-4769 and 4845).
/// </summary>
/// <remarks>
/// <see cref="OpenConnectionReply1"/> reads the same message from a server without security; each codec rejects the
/// other's form, so a reply is tried as one, then the other.
/// </remarks>
public sealed record SecureOpenConnectionReply1(ulong ServerGuid, uint Cookie, ReadOnlyMemory<byte> ServerPublicKey, ushort MtuSize)
    : IWireMessage<SecureOpenConnectionReply1>
{
    /// <summary>cat::EasyHandshake::PUBLIC_KEY_BYTES (EasyHandshake.hpp:211), the length of a server public key.</summary>
    public const int PublicKeyLength = 64;

    public void Write(ref BitStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(ServerPublicKey.Length, PublicKeyLength);
        MessageIdField.Write(ref writer, MessageId.OpenConnectionReply1);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(ServerGuid);
        writer.WriteByte(1); // RakPeer.cpp:5170 - HasCookie yes
        writer.WriteUInt32(Cookie);
        writer.WriteAlignedBytes(ServerPublicKey.Span);
        writer.WriteUInt16(MtuSize);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out SecureOpenConnectionReply1 value)
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionReply1)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadUInt64(out var guid)
            || !r.TryReadByte(out var hasSecurity)
            || hasSecurity == 0
            || !r.TryReadUInt32(out var cookie)
            || !r.TryReadAlignedBytes(PublicKeyLength, out var publicKey)
            || !r.TryReadUInt16(out var mtu)
        )
            return false;
        value = new SecureOpenConnectionReply1(guid, cookie, publicKey.ToArray(), mtu);
        reader = r;
        return true;
    }
}
