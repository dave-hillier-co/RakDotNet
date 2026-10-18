using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// A borrowed view of one entry of the peer's ordered event stream: RakNet's Packet.
/// </summary>
/// <remarks>
/// <para>
/// Valid until the next read on <see cref="RakPeer.Events"/>, the next <see cref="RakPeer.Update"/>, or shutdown; an
/// event passed to a plugin hook is valid for the call. A ref struct, so it cannot be stored in a field, boxed,
/// captured or held across <c>await</c>. Every member checks a generation and throws
/// <see cref="ObjectDisposedException"/> after release; a span already taken from <see cref="Data"/> is not tracked.
/// Use <see cref="Detach"/> to keep it.
/// </para>
/// <para>
/// <see cref="Data"/> holds exactly the bytes of RakNet's Packet::data, including RakNet's rewrites: an
/// ID_UNCONNECTED_PONG is the ID, a 4-byte big-endian ping time and the response, without GUID and magic
/// (RakPeer.cpp:4682-4704); ID_ADVERTISE_SYSTEM and ID_OUT_OF_BAND_INTERNAL are the ID then the payload
/// (RakPeer.cpp:4706-4735); an ID_TIMESTAMP time is shifted in place by the clock differential
/// (RakPeer.cpp:1549-1555); receipt events are the ID then the receipt as 4 little-endian bytes
/// (ReliabilityLayer.cpp:760, 1871-1872).
/// </para>
/// </remarks>
public readonly ref struct PeerEvent
{
    public PeerEventKind Kind => throw new NotImplementedException();

    /// <summary>False only for kinds RakNet never produces (<see cref="PeerEventKind.ReceiptAbandoned"/>).</summary>
    public bool IsRakNetPacket => throw new NotImplementedException();

    /// <summary><c>Data[0]</c>, or the ID after the ID_TIMESTAMP header.</summary>
    public MessageId Id => throw new NotImplementedException();

    /// <summary>Null for offline, loopback and some injected events.</summary>
    public RakConnection? Connection => throw new NotImplementedException();

    /// <summary>Packet::guid</summary>
    public RakNetGuid RakNetGuid => throw new NotImplementedException();

    /// <summary>Packet::systemAddress</summary>
    public SystemAddress Address => throw new NotImplementedException();

    public int SocketIndex => throw new NotImplementedException();

    /// <summary><see cref="RakPeer.GetTime"/> when the entry was queued.</summary>
    public ulong ReceivedTime => throw new NotImplementedException();

    /// <summary>Packet::wasGeneratedLocally</summary>
    public bool IsLocal => throw new NotImplementedException();

    /// <summary>Packet::data. Empty when <see cref="IsRakNetPacket"/> is false.</summary>
    public ReadOnlySpan<byte> Data => throw new NotImplementedException();

    /// <summary>
    /// Packet::bitSize: for connected messages the bit length the reliability layer delivered, which can end mid-byte
    /// (RakPeer.cpp:6046, 6231); whole bytes for ID_CONNECTION_REQUEST_ACCEPTED (RakPeer.cpp:6186).
    /// </summary>
    public int BitLength => throw new NotImplementedException();

    /// <summary>The bytes after the ID, or after ID_TIMESTAMP, the time and the ID.</summary>
    public ReadOnlySpan<byte> Body => throw new NotImplementedException();

    /// <summary>A reader over <see cref="Data"/>, limited to <see cref="BitLength"/>.</summary>
    public BitStreamReader GetReader() => throw new NotImplementedException();

    /// <summary>A reader positioned at <see cref="Body"/>.</summary>
    public BitStreamReader GetBodyReader() => throw new NotImplementedException();

    public bool HasTimestamp => throw new NotImplementedException();

    /// <summary>The ID_TIMESTAMP time, already shifted into local time (ShiftIncomingTimestamp).</summary>
    public ulong Timestamp => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.Connected"/>: true for ID_NEW_INCOMING_CONNECTION.</summary>
    public bool IsIncoming => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.Disconnected"/> only.</summary>
    public DisconnectReason DisconnectReason => throw new NotImplementedException();

    /// <summary>
    /// <see cref="PeerEventKind.Disconnected"/>: the connection ended because this peer called Close. Information
    /// RakNet does not expose; it changes neither <see cref="Data"/> nor the plugin callbacks.
    /// </summary>
    public bool ClosedLocally => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.ConnectFailed"/> only.</summary>
    public ConnectFailure ConnectFailure => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.ConnectFailed"/> with <see cref="ConnectFailure.IncompatibleProtocolVersion"/>.</summary>
    public byte RemoteProtocolVersion => throw new NotImplementedException();

    /// <summary>The receipt events.</summary>
    public SendReceipt Receipt => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.UnconnectedPong"/> only.</summary>
    public UnconnectedPongView Pong => throw new NotImplementedException();

    /// <summary><see cref="PeerEventKind.DownloadProgress"/> only.</summary>
    public DownloadProgressView Progress => throw new NotImplementedException();

    /// <summary>
    /// Takes ownership of the event, removing it from automatic release. Detaching twice throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public OwnedPeerEvent Detach() => throw new NotImplementedException();

    /// <summary>An unpooled copy of <see cref="Data"/>.</summary>
    public byte[] ToArray() => throw new NotImplementedException();
}

/// <summary>The fields of an ID_UNCONNECTED_PONG event. Borrowed with its event.</summary>
public readonly ref struct UnconnectedPongView
{
    /// <summary>The ping time echoed by the remote, as RakNet's 4-byte TimeMS.</summary>
    public uint SentTime => throw new NotImplementedException();

    public TimeSpan RoundTrip => throw new NotImplementedException();

    /// <summary>The remote's offline ping response.</summary>
    public ReadOnlySpan<byte> Response => throw new NotImplementedException();
}

/// <summary>The fields of an ID_DOWNLOAD_PROGRESS event. Borrowed with its event.</summary>
public readonly ref struct DownloadProgressView
{
    public uint PartsArrived => throw new NotImplementedException();

    public uint PartCount => throw new NotImplementedException();

    public uint PartLength => throw new NotImplementedException();

    public ReadOnlySpan<byte> FirstPart => throw new NotImplementedException();
}
