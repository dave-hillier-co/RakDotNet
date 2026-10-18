using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// The ID_TIMESTAMP prefix: the ID then the sender's RakNet::Time as a big-endian u64. A receiving peer shifts the
/// time into its own clock in place before delivering the packet (RakPeer.cpp:1549-1555).
/// </summary>
public readonly record struct TimestampHeader(ulong Time) : IWireMessage<TimestampHeader>
{
    public void Write(ref BitStreamWriter writer) => throw new NotImplementedException();

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out TimestampHeader value) =>
        throw new NotImplementedException();
}

/// <summary>
/// RakPeer::WriteOutOfBandHeader: ID_OUT_OF_BAND_INTERNAL, the sender's GUID, then the offline magic
/// (RakPeer.cpp:2979-2985).
/// </summary>
public readonly record struct OutOfBandHeader(RakNetGuid Sender) : IWireMessage<OutOfBandHeader>
{
    public void Write(ref BitStreamWriter writer) => throw new NotImplementedException();

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out OutOfBandHeader value) =>
        throw new NotImplementedException();
}
