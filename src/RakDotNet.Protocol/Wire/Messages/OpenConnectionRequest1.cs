namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_OPEN_CONNECTION_REQUEST_1, the MTU probe: ID, offline magic, protocol version, then zero padding so that the
/// whole UDP payload is <c>MtuSize - 28</c> bytes (RakPeer.cpp:5758-5761, BitStream::PadWithZeroToByteLength at
/// BitStream.cpp:744-753). The MTU is not encoded as a field; it is the datagram length plus the UDP/IP header.
/// </summary>
/// <remarks>
/// <para>
/// This codec must be given the whole datagram: <see cref="TryRead"/> recovers <see cref="MtuSize"/> as the number
/// of bytes from the ID to the end of the input plus <see cref="RakNetConstants.UdpHeaderSize"/>, exactly as the
/// C++ server does (RakPeer.cpp:5183-5186), and consumes the padding without checking it is zero.
/// </para>
/// <para>
/// The server then caps the MTU it offers at <see cref="RakNetConstants.MaximumMtuSize"/>; that is peer logic, not
/// part of this codec. A foreign <see cref="ProtocolVersion"/> is decoded, not rejected: the server answers it with
/// ID_INCOMPATIBLE_PROTOCOL_VERSION (RakPeer.cpp:5137-5157).
/// </para>
/// <para>
/// <see cref="Write"/> needs a buffer of at least <c>MtuSize - 28</c> bytes.
/// </para>
/// </remarks>
public readonly record struct OpenConnectionRequest1(byte ProtocolVersion, int MtuSize) : IWireMessage<OpenConnectionRequest1>
{
    private const int HeaderLength = 1 + OfflineMagic.Length + 1;

    /// <summary>The smallest MTU whose padded datagram can still hold the header.</summary>
    public const int MinimumMtuSize = HeaderLength + RakNetConstants.UdpHeaderSize;

    public void Write(ref BitStreamWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MtuSize, MinimumMtuSize);
        MessageIdField.Write(ref writer, MessageId.OpenConnectionRequest1);
        OfflineMagic.Write(ref writer);
        writer.WriteByte(ProtocolVersion);
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        var padding = MtuSize - RakNetConstants.UdpHeaderSize - HeaderLength;
        while (padding > 0)
        {
            var chunk = Math.Min(padding, zeros.Length);
            writer.WriteBytes(zeros[..chunk]);
            padding -= chunk;
        }
    }

    public static bool TryRead(ref BitStreamReader reader, out OpenConnectionRequest1 value)
    {
        value = default;
        var r = reader;
        // RakPeer.cpp:5186 uses the received datagram length in bytes.
        var datagramLength = r.BitsRemaining / 8;
        if (
            !MessageIdField.TryRead(ref r, MessageId.OpenConnectionRequest1)
            || !OfflineMagic.TryRead(ref r)
            || !r.TryReadByte(out var protocolVersion)
            || !r.TryReadAlignedBytes(r.BitsRemaining / 8, out _)
        )
            return false;
        value = new OpenConnectionRequest1(protocolVersion, datagramLength + RakNetConstants.UdpHeaderSize);
        reader = r;
        return true;
    }
}
