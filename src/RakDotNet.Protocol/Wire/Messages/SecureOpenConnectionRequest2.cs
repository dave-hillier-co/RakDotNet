using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REQUEST_2 to a server that reported hasSecurity in reply 1: ID, offline magic, the cookie from
/// that reply as a uint32, clientWroteChallenge as one byte, the 64-byte handshake challenge aligned when that byte is
/// non-zero, then the server's address as the client addressed it, the MTU and the client GUID
/// (RakPeer.cpp:4758-4853; parsed at 5208-5253).
/// </summary>
/// <remarks>
/// Nothing in the message says which layout it has: a server with security reads this one, a server without it
/// reads <see cref="OpenConnectionRequest2"/> (RakPeer.cpp:5211).
/// </remarks>
/// <param name="Cookie">The cookie the server sent in ID_OPEN_CONNECTION_REPLY_1.</param>
/// <param name="Challenge">The client's handshake challenge, or empty for a client without a server key, which
/// writes the flag 0 (RakPeer.cpp:4803-4808).</param>
/// <param name="ServerAddress">The server's address as the client addressed it.</param>
/// <param name="MtuSize">The MTU echoed from ID_OPEN_CONNECTION_REPLY_1.</param>
/// <param name="ClientGuid">The client's GUID.</param>
public sealed record SecureOpenConnectionRequest2(uint Cookie, ReadOnlyMemory<byte> Challenge, IPEndPoint ServerAddress, ushort MtuSize, ulong ClientGuid)
    : IWireMessage<SecureOpenConnectionRequest2>
{
    /// <summary>cat::EasyHandshake::CHALLENGE_BYTES (EasyHandshake.hpp:213).</summary>
    public const int ChallengeLength = 64;

    /// <exception cref="ArgumentOutOfRangeException"><see cref="Challenge"/> is neither empty nor
    /// <see cref="ChallengeLength"/> bytes.</exception>
    public void Write(ref BitStreamWriter writer)
    {
        if (!Challenge.IsEmpty)
            ArgumentOutOfRangeException.ThrowIfNotEqual(Challenge.Length, ChallengeLength, nameof(Challenge));
        MessageIdField.Write(ref writer, MessageId.OpenConnectionRequest2);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt32(Cookie);
        writer.WriteByte(Challenge.IsEmpty ? (byte)0 : (byte)1);
        if (!Challenge.IsEmpty)
            writer.WriteAlignedBytes(Challenge.Span);
        writer.WriteAddress(ServerAddress);
        writer.WriteUInt16(MtuSize);
        writer.WriteUInt64(ClientGuid);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out SecureOpenConnectionRequest2 value)
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionRequest2)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadUInt32(out var cookie)
            || !r.TryReadByte(out var wroteChallenge)
        )
            return false;
        var challenge = ReadOnlyMemory<byte>.Empty;
        if (wroteChallenge != 0)
        {
            if (!r.TryReadAlignedBytes(ChallengeLength, out var bytes))
                return false;
            challenge = bytes.ToArray();
        }
        if (!r.TryReadAddress(out var address) || !r.TryReadUInt16(out var mtu) || !r.TryReadUInt64(out var guid))
            return false;
        value = new SecureOpenConnectionRequest2(cookie, challenge, address!, mtu, guid);
        reader = r;
        return true;
    }
}
