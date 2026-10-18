using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// ID_NEW_INCOMING_CONNECTION, the client's answer to ID_CONNECTION_REQUEST_ACCEPTED: ID, the server's address as
/// the client sees it, the client's internal address list, then two times (RakPeer.cpp:6193-6199; parsed at
/// 6015-6026).
/// </summary>
/// <param name="ServerAddress">The address the client connected to.</param>
/// <param name="InternalAddresses">The client's own addresses; see <see cref="InternalAddressList"/>.</param>
/// <param name="SendPingTime">The server's time echoed from ID_CONNECTION_REQUEST_ACCEPTED's reply time.</param>
/// <param name="SendPongTime">The client's time when it sent this message.</param>
/// <remarks>
/// The server treats the two times as a ping/pong pair (OnConnectedPong at RakPeer.cpp:6026), hence the names.
/// </remarks>
public sealed record NewIncomingConnection(
    IPEndPoint ServerAddress,
    IReadOnlyList<IPEndPoint> InternalAddresses,
    ulong SendPingTime,
    ulong SendPongTime
) : IWireMessage<NewIncomingConnection>
{
    public void Write(ref BitStreamWriter writer)
    {
        MessageIdField.Write(ref writer, MessageId.NewIncomingConnection);
        writer.WriteAddress(ServerAddress);
        InternalAddressList.Write(ref writer, InternalAddresses);
        writer.WriteUInt64(SendPingTime);
        writer.WriteUInt64(SendPongTime);
    }

    public static bool TryRead(ref BitStreamReader reader, [MaybeNullWhen(false)] out NewIncomingConnection value) =>
        TryRead(ref reader, out value, RakNetConstants.MaximumNumberOfInternalIds);

    public static bool TryRead(
        ref BitStreamReader reader,
        [MaybeNullWhen(false)] out NewIncomingConnection value,
        int internalAddressCount
    )
    {
        value = null;
        var r = reader;
        if (
            !MessageIdField.TryRead(ref r, MessageId.NewIncomingConnection)
            || !r.TryReadAddress(out var serverAddress)
            || !InternalAddressList.TryRead(ref r, internalAddressCount, out var internalAddresses)
            || !r.TryReadUInt64(out var pingTime)
            || !r.TryReadUInt64(out var pongTime)
        )
            return false;
        value = new NewIncomingConnection(serverAddress!, internalAddresses, pingTime, pongTime);
        reader = r;
        return true;
    }
}
