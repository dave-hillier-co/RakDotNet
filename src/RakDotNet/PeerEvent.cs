using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

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
/// <para>Kind-specific members return <c>default</c> when the event is of another kind.</para>
/// </remarks>
public readonly ref struct PeerEvent
{
    private readonly EventSegment? _segment;
    private readonly int _generation;

    internal PeerEvent(EventSegment segment, int generation)
    {
        _segment = segment;
        _generation = generation;
    }

    public PeerEventKind Kind => Segment.Kind;

    /// <summary>False only for kinds RakNet never produces (<see cref="PeerEventKind.ReceiptAbandoned"/>).</summary>
    public bool IsRakNetPacket => Segment.Kind != PeerEventKind.ReceiptAbandoned;

    /// <summary><c>Data[0]</c>, or the ID after the ID_TIMESTAMP header.</summary>
    public MessageId Id
    {
        get
        {
            var data = Segment.Data;
            if (data.IsEmpty)
                return default;
            return data[0] == (byte)MessageId.Timestamp && data.Length > TimestampHeader.Length
                ? (MessageId)data[TimestampHeader.Length]
                : (MessageId)data[0];
        }
    }

    /// <summary>Null for offline, loopback and some injected events.</summary>
    public RakConnection? Connection => Segment.Connection;

    /// <summary>Packet::guid</summary>
    public RakNetGuid RakNetGuid => Segment.RakNetGuid;

    /// <summary>Packet::systemAddress</summary>
    public SystemAddress Address => Segment.Address;

    public int SocketIndex => Segment.SocketIndex;

    /// <summary><see cref="RakPeer.GetTime"/> when the entry was queued.</summary>
    public ulong ReceivedTime => Segment.ReceivedTime;

    /// <summary>
    /// Packet::wasGeneratedLocally: set only on the ID_CONNECTION_LOST of a silent local close (RakPeer.cpp:1676).
    /// Loopback and injected packets leave it clear, as AllocPacket does (RakPeer.cpp:165, 183).
    /// </summary>
    public bool IsLocal => Segment.IsLocal;

    /// <summary>Packet::data. Empty when <see cref="IsRakNetPacket"/> is false.</summary>
    public ReadOnlySpan<byte> Data => Segment.Data;

    /// <summary>
    /// Packet::bitSize: for connected messages the bit length the reliability layer delivered, which can end mid-byte
    /// (RakPeer.cpp:6046, 6231); whole bytes for ID_CONNECTION_REQUEST_ACCEPTED (RakPeer.cpp:6186).
    /// </summary>
    public int BitLength => Segment.BitLength;

    /// <summary>The bytes after the ID, or after ID_TIMESTAMP, the time and the ID.</summary>
    public ReadOnlySpan<byte> Body
    {
        get
        {
            var data = Segment.Data;
            var start = HasTimestampIn(data) ? TimestampHeader.Length + 1 : 1;
            return data.Length > start ? data[start..] : [];
        }
    }

    /// <summary>A reader over <see cref="Data"/>, limited to <see cref="BitLength"/>.</summary>
    public BitStreamReader GetReader()
    {
        var segment = Segment;
        return new BitStreamReader(segment.Data, segment.BitLength);
    }

    /// <summary>A reader positioned at <see cref="Body"/>.</summary>
    public BitStreamReader GetBodyReader() => new(Body);

    public bool HasTimestamp => HasTimestampIn(Segment.Data);

    /// <summary>The ID_TIMESTAMP time, already shifted into local time (ShiftIncomingTimestamp).</summary>
    public ulong Timestamp
    {
        get
        {
            var data = Segment.Data;
            return HasTimestampIn(data) ? System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(data[1..]) : 0;
        }
    }

    /// <summary><see cref="PeerEventKind.Connected"/>: true for ID_NEW_INCOMING_CONNECTION.</summary>
    public bool IsIncoming => Segment.IsIncoming;

    /// <summary><see cref="PeerEventKind.Disconnected"/> only.</summary>
    public DisconnectReason DisconnectReason => Segment.DisconnectReason;

    /// <summary>
    /// <see cref="PeerEventKind.Disconnected"/>: the connection ended because this peer called Close. Information
    /// RakNet does not expose; it changes neither <see cref="Data"/> nor the plugin callbacks.
    /// </summary>
    public bool ClosedLocally => Segment.ClosedLocally;

    /// <summary><see cref="PeerEventKind.ConnectFailed"/> only.</summary>
    public ConnectFailure ConnectFailure => Segment.ConnectFailure;

    /// <summary><see cref="PeerEventKind.ConnectFailed"/> with <see cref="ConnectFailure.IncompatibleProtocolVersion"/>.</summary>
    public byte RemoteProtocolVersion => Segment.RemoteProtocolVersion;

    /// <summary>The receipt events.</summary>
    public SendReceipt Receipt => Segment.Receipt;

    /// <summary><see cref="PeerEventKind.UnconnectedPong"/> only.</summary>
    public UnconnectedPongView Pong
    {
        get
        {
            var segment = Segment;
            return segment.Kind == PeerEventKind.UnconnectedPong ? new UnconnectedPongView(segment.Data, segment.ReceivedTime) : default;
        }
    }

    /// <summary><see cref="PeerEventKind.DownloadProgress"/> only.</summary>
    public DownloadProgressView Progress
    {
        get
        {
            var segment = Segment;
            return segment.Kind == PeerEventKind.DownloadProgress ? new DownloadProgressView(segment.Data) : default;
        }
    }

    /// <summary>
    /// Takes ownership of the event, removing it from automatic release. Detaching twice throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public OwnedPeerEvent Detach()
    {
        var segment = Segment;
        if (segment.IsOwned)
            throw new InvalidOperationException("The event is already detached.");
        segment.Pool.MarkOwned(segment);
        return new OwnedPeerEvent(segment, _generation);
    }

    /// <summary>
    /// Packet::data[0] = <paramref name="id"/>: the rewrite a plugin makes to a message it passes on, which later plugins
    /// and the reader then see (TwoWayAuthentication.cpp:375, Router2.cpp:1260). Writes <see cref="Data"/>[0] whatever it
    /// holds, as RakNet does.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The event is not a <see cref="PeerEventKind.Message"/>, or <paramref name="id"/> would make it another kind.
    /// </exception>
    public void RewriteId(MessageId id)
    {
        var segment = Segment;
        if (segment.Kind != PeerEventKind.Message || segment.Length == 0)
            throw new InvalidOperationException("Only a non-empty Message event can have its ID rewritten.");
        var data = segment.WritableData;
        var previous = data[0];
        data[0] = (byte)id;
        RakPeer.Classify(segment);
        if (segment.Kind == PeerEventKind.Message)
            return;
        data[0] = previous;
        segment.Kind = PeerEventKind.Message;
        segment.IsIncoming = false;
        segment.DisconnectReason = default;
        segment.Receipt = default;
        segment.ConnectFailure = default;
        segment.RemoteProtocolVersion = 0;
        throw new InvalidOperationException($"Rewriting the ID to {id} would change the kind of the event.");
    }

    /// <summary>An unpooled copy of <see cref="Data"/>.</summary>
    public byte[] ToArray() => Segment.Data.ToArray();

    private EventSegment Segment
    {
        get
        {
            var segment = _segment;
            if (segment is null || Volatile.Read(ref segment.Generation) != _generation)
                throw new ObjectDisposedException(nameof(PeerEvent), "The event was released: it is valid until the next read, Update or shutdown unless detached.");
            return segment;
        }
    }

    private static bool HasTimestampIn(ReadOnlySpan<byte> data) =>
        data.Length > TimestampHeader.Length && data[0] == (byte)MessageId.Timestamp;
}

/// <summary>The fields of an ID_UNCONNECTED_PONG event. Borrowed with its event.</summary>
public readonly ref struct UnconnectedPongView
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly ulong _receivedTime;

    internal UnconnectedPongView(ReadOnlySpan<byte> data, ulong receivedTime)
    {
        _data = data;
        _receivedTime = receivedTime;
    }

    /// <summary>The ping time echoed by the remote, as RakNet's 4-byte TimeMS.</summary>
    public uint SentTime => _data.Length >= 5 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(_data[1..]) : 0;

    public TimeSpan RoundTrip => _data.Length >= 5 ? TimeSpan.FromMilliseconds(unchecked((uint)_receivedTime - SentTime)) : TimeSpan.Zero;

    /// <summary>The remote's offline ping response.</summary>
    public ReadOnlySpan<byte> Response => _data.Length > 5 ? _data[5..] : [];
}

/// <summary>The fields of an ID_DOWNLOAD_PROGRESS event. Borrowed with its event.</summary>
/// <remarks>
/// ReliabilityLayer.cpp:3120-3131 writes the ID, then the parts arrived, the part count and the part length, each
/// memcpy'd in host order (little-endian on every supported platform), then the first part's bytes.
/// </remarks>
public readonly ref struct DownloadProgressView
{
    private readonly ReadOnlySpan<byte> _data;

    internal DownloadProgressView(ReadOnlySpan<byte> data) => _data = data;

    public uint PartsArrived => Field(0);

    public uint PartCount => Field(1);

    public uint PartLength => Field(2);

    public ReadOnlySpan<byte> FirstPart => _data.Length > 13 ? _data[13..] : [];

    private uint Field(int index) =>
        _data.Length >= 1 + (index + 1) * 4 ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(_data[(1 + index * 4)..]) : 0;
}
