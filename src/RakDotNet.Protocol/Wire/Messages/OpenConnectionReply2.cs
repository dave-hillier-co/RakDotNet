using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REPLY_2: ID, offline magic, server GUID, the client's address as the server sees it, the MTU,
/// then requiresSecurity as a single bit (RakPeer.cpp:5300-5305; parsed at 4876-4889). When the bit is set, the
/// server's 128-byte handshake answer follows at the next byte boundary (RakPeer.cpp:5405, read at 4894-4898).
/// </summary>
/// <remarks>
/// <para>
/// The security flag is a C++ <c>bool</c>, which BitStream writes as one bit (BitStream.h:1130-1136), so it is the
/// top bit of an otherwise zero byte: the datagram is 35 bytes without security and 163 with it.
/// </para>
/// <para>
/// <see cref="TryRead"/> returns false for a reply requiring security whose answer is cut short, with the fields before
/// it filled in and <see cref="Answer"/> empty, so the client can still act on the flag as RakPeer does.
/// </para>
/// </remarks>
public readonly record struct OpenConnectionReply2(
    ulong ServerGuid,
    IPEndPoint ClientAddress,
    ushort MtuSize,
    bool RequiresSecurity = false,
    ReadOnlyMemory<byte> Answer = default
) : IWireMessage<OpenConnectionReply2>
{
    /// <summary>cat::EasyHandshake::ANSWER_BYTES (EasyHandshake.hpp:215).</summary>
    public const int AnswerLength = 128;

    /// <exception cref="ArgumentOutOfRangeException"><see cref="RequiresSecurity"/> is set and <see cref="Answer"/> is
    /// not <see cref="AnswerLength"/> bytes.</exception>
    public void Write(ref BitStreamWriter writer)
    {
        if (RequiresSecurity)
            ArgumentOutOfRangeException.ThrowIfNotEqual(Answer.Length, AnswerLength, nameof(Answer));
        MessageIdField.Write(ref writer, MessageId.OpenConnectionReply2);
        OfflineMagic.Write(ref writer);
        writer.WriteUInt64(ServerGuid);
        writer.WriteAddress(ClientAddress);
        writer.WriteUInt16(MtuSize);
        writer.WriteBool(RequiresSecurity);
        if (RequiresSecurity)
            writer.WriteAlignedBytes(Answer.Span);
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
        {
            if (!r.TryReadAlignedBytes(AnswerLength, out var answer))
                return false;
            value = value with { Answer = answer.ToArray() };
        }
        else
        {
            // The flag is the last field; skip the rest of its byte.
            r.AlignToByte();
        }
        reader = r;
        return true;
    }
}
