using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_UNCONNECTED_PONG: ID, echoed ping time (64-bit), server GUID, offline magic, then the server's offline ping
/// response as raw bytes up to the end of the datagram (RakPeer.cpp:4650-4657).
/// </summary>
/// <remarks>
/// The response data has no length prefix; it runs to the end of the input. The C++ client drops pongs whose
/// datagram is 33 + <see cref="RakNetConstants.MaxOfflineDataLength"/> bytes or more (RakPeer.cpp:4682), so
/// <see cref="TryRead(ref BitStreamReader, out UnconnectedPong)"/> accepts at most 399 bytes of response data.
/// </remarks>
public sealed record UnconnectedPong(ulong SendPingTime, ulong ServerGuid, ReadOnlyMemory<byte> ResponseData)
    : IWireMessage<UnconnectedPong>
{
    /// <summary>The largest response the C++ client accepts.</summary>
    public const int DefaultMaxResponseDataLength = RakNetConstants.MaxOfflineDataLength - 1;

    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.UnconnectedPong);
        writer.WriteUInt64(SendPingTime);
        writer.WriteUInt64(ServerGuid);
        OfflineMagic.Write(ref writer);
        writer.WriteBytes(ResponseData.Span);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out UnconnectedPong value) =>
        TryRead(ref reader, out value, DefaultMaxResponseDataLength);

    /// <summary>Reads a pong, accepting up to <paramref name="maxResponseDataLength"/> bytes of response data.</summary>
    public static bool TryRead(
        ref BitStreamReader reader,
        [MaybeNullWhen(false)] out UnconnectedPong value,
        int maxResponseDataLength
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxResponseDataLength);
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.UnconnectedPong)
            || !r.TryReadUInt64(out var time)
            || !r.TryReadUInt64(out var guid)
            || !OfflineMagic.TryRead(ref r)
        )
            return false;
        var length = r.BitsRemaining / 8;
        if (length > maxResponseDataLength || !r.TryReadAlignedBytes(length, out var data))
            return false;
        value = new UnconnectedPong(time, guid, data.ToArray());
        reader = r;
        return true;
    }
}
