using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_CONNECTION_REQUEST_ACCEPTED: ID, the client's address as the server sees it, the client's SystemIndex
/// (uint16), the server's internal address list, the request time echoed from ID_CONNECTION_REQUEST, the server's
/// time (RakPeer.cpp:3540-3554; parsed at 6152-6162).
/// </summary>
/// <remarks>
/// The list length is MAXIMUM_NUMBER_OF_INTERNAL_IDS (10 by default, RakNetDefines.h:75-77, but a C++ build may
/// override it). <see cref="Write"/> writes <see cref="InternalAddresses"/> as given, so build it with
/// <see cref="InternalAddressList.Pad"/>; <see cref="TryRead(ref BitStreamReader, out ConnectionRequestAccepted, int)"/>
/// takes the count to expect.
/// </remarks>
public sealed record ConnectionRequestAccepted(
    IPEndPoint ClientAddress,
    ushort SystemIndex,
    IReadOnlyList<IPEndPoint> InternalAddresses,
    ulong RequestTime,
    ulong ReplyTime
) : IWireMessage<ConnectionRequestAccepted>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.ConnectionRequestAccepted);
        writer.WriteAddress(ClientAddress);
        writer.WriteUInt16(SystemIndex);
        InternalAddressList.Write(ref writer, InternalAddresses);
        writer.WriteUInt64(RequestTime);
        writer.WriteUInt64(ReplyTime);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out ConnectionRequestAccepted value) =>
        TryRead(ref reader, out value, RakNetConstants.MaximumNumberOfInternalIds);

    public static bool TryRead(
        ref BitStreamReader reader,
        [MaybeNullWhen(false)] out ConnectionRequestAccepted value,
        int internalAddressCount
    )
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.ConnectionRequestAccepted)
            || !r.TryReadAddress(out var clientAddress)
            || !r.TryReadUInt16(out var systemIndex)
            || !InternalAddressList.TryRead(ref r, internalAddressCount, out var internalAddresses)
            || !r.TryReadUInt64(out var requestTime)
            || !r.TryReadUInt64(out var replyTime)
        )
            return false;
        value = new ConnectionRequestAccepted(clientAddress!, systemIndex, internalAddresses, requestTime, replyTime);
        reader = r;
        return true;
    }
}
