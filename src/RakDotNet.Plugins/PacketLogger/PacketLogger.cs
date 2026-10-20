using System.Globalization;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// PacketLogger: writes a comma-separated line for every datagram sent and received, every message a connection sends,
/// resends and receives, every acknowledgement, every reliability-layer notification and every injected packet.
/// </summary>
/// <remarks>
/// <para>
/// Ported from PacketLogger.cpp, line formats included, on <see cref="INetworkObserver"/> and <see cref="IRakPlugin"/>.
/// Lines are passed to <see cref="AddToLog"/>, which by default writes them with <see cref="WriteLog"/> to the
/// <see cref="TextWriter"/> given at construction, <see cref="Console.Out"/> by default as RAKNET_DEBUG_PRINTF writes to
/// stdout. Datagram, frame, ack and notification lines are produced in the I/O cycle and injection lines on the dispatch
/// step, so the writer is synchronised; <see cref="ThreadsafePacketLogger"/> instead writes every line on the dispatch
/// step.
/// </para>
/// <para>
/// RakNet's quirks are kept: <see cref="PrintAcks"/> is stored and never read, <see cref="WriteMiscellaneous"/> logs only
/// its message, the clock column is empty except on Windows, and a pushed-back user message ID prints as
/// <c>(null)</c>. A message that is neither ordered nor sequenced logs ordering index 0 where RakNet logs a stale value.
/// </para>
/// </remarks>
public class PacketLogger : IRakPlugin, INetworkObserver
{
    private const int MaxAffixLength = 255;

    private const string Header =
        "Clock,S|R,Typ,Reliable#,Frm #,PktID,BitLn,Time     ,Local IP:Port   ,RemoteIP:Port,SPID,SPIN,SPCO,OI,Suffix,Miscellaneous\n";

    // PacketLogger::BaseIDTOString's own table (PacketLogger.cpp:244-381), which names IDs as RakNet 4.081 did.
    private static readonly string[] IdTable =
    [
        "ID_CONNECTED_PING",
        "ID_UNCONNECTED_PING",
        "ID_UNCONNECTED_PING_OPEN_CONNECTIONS",
        "ID_CONNECTED_PONG",
        "ID_DETECT_LOST_CONNECTIONS",
        "ID_OPEN_CONNECTION_REQUEST_1",
        "ID_OPEN_CONNECTION_REPLY_1",
        "ID_OPEN_CONNECTION_REQUEST_2",
        "ID_OPEN_CONNECTION_REPLY_2",
        "ID_CONNECTION_REQUEST",
        "ID_REMOTE_SYSTEM_REQUIRES_PUBLIC_KEY",
        "ID_OUR_SYSTEM_REQUIRES_SECURITY",
        "ID_PUBLIC_KEY_MISMATCH",
        "ID_OUT_OF_BAND_INTERNAL",
        "ID_SND_RECEIPT_ACKED",
        "ID_SND_RECEIPT_LOSS",
        "ID_CONNECTION_REQUEST_ACCEPTED",
        "ID_CONNECTION_ATTEMPT_FAILED",
        "ID_ALREADY_CONNECTED",
        "ID_NEW_INCOMING_CONNECTION",
        "ID_NO_FREE_INCOMING_CONNECTIONS",
        "ID_DISCONNECTION_NOTIFICATION",
        "ID_CONNECTION_LOST",
        "ID_CONNECTION_BANNED",
        "ID_INVALID_PASSWORD",
        "ID_INCOMPATIBLE_PROTOCOL_VERSION",
        "ID_IP_RECENTLY_CONNECTED",
        "ID_TIMESTAMP",
        "ID_UNCONNECTED_PONG",
        "ID_ADVERTISE_SYSTEM",
        "ID_DOWNLOAD_PROGRESS",
        "ID_REMOTE_DISCONNECTION_NOTIFICATION",
        "ID_REMOTE_CONNECTION_LOST",
        "ID_REMOTE_NEW_INCOMING_CONNECTION",
        "ID_FILE_LIST_TRANSFER_HEADER",
        "ID_FILE_LIST_TRANSFER_FILE",
        "ID_FILE_LIST_REFERENCE_PUSH_ACK",
        "ID_DDT_DOWNLOAD_REQUEST",
        "ID_TRANSPORT_STRING",
        "ID_REPLICA_MANAGER_CONSTRUCTION",
        "ID_REPLICA_MANAGER_SCOPE_CHANGE",
        "ID_REPLICA_MANAGER_SERIALIZE",
        "ID_REPLICA_MANAGER_DOWNLOAD_STARTED",
        "ID_REPLICA_MANAGER_DOWNLOAD_COMPLETE",
        "ID_RAKVOICE_OPEN_CHANNEL_REQUEST",
        "ID_RAKVOICE_OPEN_CHANNEL_REPLY",
        "ID_RAKVOICE_CLOSE_CHANNEL",
        "ID_RAKVOICE_DATA",
        "ID_AUTOPATCHER_GET_CHANGELIST_SINCE_DATE",
        "ID_AUTOPATCHER_CREATION_LIST",
        "ID_AUTOPATCHER_DELETION_LIST",
        "ID_AUTOPATCHER_GET_PATCH",
        "ID_AUTOPATCHER_PATCH_LIST",
        "ID_AUTOPATCHER_REPOSITORY_FATAL_ERROR",
        "ID_AUTOPATCHER_CANNOT_DOWNLOAD_ORIGINAL_UNMODIFIED_FILES",
        "ID_AUTOPATCHER_FINISHED_INTERNAL",
        "ID_AUTOPATCHER_FINISHED",
        "ID_AUTOPATCHER_RESTART_APPLICATION",
        "ID_NAT_PUNCHTHROUGH_REQUEST",
        "ID_NAT_CONNECT_AT_TIME",
        "ID_NAT_GET_MOST_RECENT_PORT",
        "ID_NAT_CLIENT_READY",
        "ID_NAT_TARGET_NOT_CONNECTED",
        "ID_NAT_TARGET_UNRESPONSIVE",
        "ID_NAT_CONNECTION_TO_TARGET_LOST",
        "ID_NAT_ALREADY_IN_PROGRESS",
        "ID_NAT_PUNCHTHROUGH_FAILED",
        "ID_NAT_PUNCHTHROUGH_SUCCEEDED",
        "ID_READY_EVENT_SET",
        "ID_READY_EVENT_UNSET",
        "ID_READY_EVENT_ALL_SET",
        "ID_READY_EVENT_QUERY",
        "ID_LOBBY_GENERAL",
        "ID_RPC_REMOTE_ERROR",
        "ID_RPC_PLUGIN",
        "ID_FILE_LIST_REFERENCE_PUSH",
        "ID_READY_EVENT_FORCE_ALL_SET",
        "ID_ROOMS_EXECUTE_FUNC",
        "ID_ROOMS_LOGON_STATUS",
        "ID_ROOMS_HANDLE_CHANGE",
        "ID_LOBBY2_SEND_MESSAGE",
        "ID_LOBBY2_SERVER_ERROR",
        "ID_FCM2_NEW_HOST",
        "ID_FCM2_REQUEST_FCMGUID",
        "ID_FCM2_RESPOND_CONNECTION_COUNT",
        "ID_FCM2_INFORM_FCMGUID",
        "ID_FCM2_UPDATE_MIN_TOTAL_CONNECTION_COUNT",
        "ID_FCM2_VERIFIED_JOIN_START",
        "ID_FCM2_VERIFIED_JOIN_CAPABLE",
        "ID_FCM2_VERIFIED_JOIN_FAILED",
        "ID_FCM2_VERIFIED_JOIN_ACCEPTED",
        "ID_FCM2_VERIFIED_JOIN_REJECTED",
        "ID_UDP_PROXY_GENERAL",
        "ID_SQLite3_EXEC",
        "ID_SQLite3_UNKNOWN_DB",
        "ID_SQLLITE_LOGGER",
        "ID_NAT_TYPE_DETECTION_REQUEST",
        "ID_NAT_TYPE_DETECTION_RESULT",
        "ID_ROUTER_2_INTERNAL",
        "ID_ROUTER_2_FORWARDING_NO_PATH",
        "ID_ROUTER_2_FORWARDING_ESTABLISHED",
        "ID_ROUTER_2_REROUTED",
        "ID_TEAM_BALANCER_INTERNAL",
        "ID_TEAM_BALANCER_REQUESTED_TEAM_FULL",
        "ID_TEAM_BALANCER_REQUESTED_TEAM_LOCKED",
        "ID_TEAM_BALANCER_TEAM_REQUESTED_CANCELLED",
        "ID_TEAM_BALANCER_TEAM_ASSIGNED",
        "ID_LIGHTSPEED_INTEGRATION",
        "ID_XBOX_LOBBY",
        "ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_SUCCESS",
        "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS",
        "ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_FAILURE",
        "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_FAILURE",
        "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_TIMEOUT",
        "ID_TWO_WAY_AUTHENTICATION_NEGOTIATION",
        "ID_CLOUD_POST_REQUEST",
        "ID_CLOUD_RELEASE_REQUEST",
        "ID_CLOUD_GET_REQUEST",
        "ID_CLOUD_GET_RESPONSE",
        "ID_CLOUD_UNSUBSCRIBE_REQUEST",
        "ID_CLOUD_SERVER_TO_SERVER_COMMAND",
        "ID_CLOUD_SUBSCRIPTION_NOTIFICATION",
        "ID_LIB_VOICE",
        "ID_RELAY_PLUGIN",
        "ID_NAT_REQUEST_BOUND_ADDRESSES",
        "ID_NAT_RESPOND_BOUND_ADDRESSES",
        "ID_FCM2_UPDATE_USER_CONTEXT",
        "ID_RESERVED_3",
        "ID_RESERVED_4",
        "ID_RESERVED_5",
        "ID_RESERVED_6",
        "ID_RESERVED_7",
        "ID_RESERVED_8",
        "ID_RESERVED_9",
        "ID_USER_PACKET_ENUM",
    ];

    private readonly TextWriter _writer;
    private string _prefix = "";
    private string _suffix = "";

    /// <summary>A logger writing to <see cref="Console.Out"/>.</summary>
    public PacketLogger()
        : this(Console.Out)
    {
    }

    /// <summary>A logger writing each line, then <c>\n</c>, to <paramref name="writer"/>.</summary>
    public PacketLogger(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = TextWriter.Synchronized(writer);
    }

    /// <summary>SetPrintID: name message IDs rather than print their numbers. Default true.</summary>
    public bool PrintId { get; set; } = true;

    /// <summary>SetPrintAcks. Stored and never read, as in RakNet: acknowledgements are always logged.</summary>
    public bool PrintAcks { get; set; } = true;

    /// <summary>SetLogDirectMessages: log every datagram sent and received. Default true.</summary>
    public bool LogDirectMessages { get; set; } = true;

    /// <summary>SetPrefix: written before the direction column; at most 255 characters are kept.</summary>
    public string Prefix
    {
        get => _prefix;
        set => _prefix = Truncate(value);
    }

    /// <summary>SetSuffix: written after the ordering index column; at most 255 characters are kept.</summary>
    public string Suffix
    {
        get => _suffix;
        set => _suffix = Truncate(value);
    }

    /// <summary>The host this logger is attached to, or null.</summary>
    protected IPluginHost? Host { get; private set; }

    /// <summary>
    /// BaseIDTOString: the name of a RakNet message ID, or null from ID_USER_PACKET_ENUM up.
    /// </summary>
    public static string? BaseIdToString(byte id) => id >= (byte)MessageId.UserPacketEnum ? null : IdTable[id];

    /// <summary>
    /// FormatLine with a message ID (PacketLogger.cpp:48-71): the ID is printed by name, as "(SPLIT PACKET)" for a part
    /// of a split message, or without <see cref="PrintId"/> as a number padded to five.
    /// </summary>
    public virtual string FormatLine(string direction, string type, uint reliableMessageNumber, uint frame, byte messageId,
                                     int bitLength, ulong time, SystemAddress local, SystemAddress remote, uint splitPacketId,
                                     uint splitPacketIndex, uint splitPacketCount, uint orderingIndex)
    {
        string idToPrint;
        if (!PrintId)
            idToPrint = messageId.ToString("D", CultureInfo.InvariantCulture).PadLeft(5);
        else if (splitPacketCount > 0 && splitPacketCount != uint.MaxValue)
            idToPrint = "(SPLIT PACKET)";
        else
            idToPrint = IdToString(messageId);
        return FormatLine(direction, type, reliableMessageNumber, frame, idToPrint, bitLength, time, local, remote,
            splitPacketId, splitPacketIndex, splitPacketCount, orderingIndex);
    }

    /// <summary>
    /// FormatLine with the ID text (PacketLogger.cpp:73-106):
    /// <c>clock,prefix+direction,type,reliable#,frame,id,bits,time,local,remote,splitId,splitIndex,splitCount,orderingIndex,suffix,</c>.
    /// A reliable number of 0xFFFFFFFF prints as N/A, and the split and ordering columns print 0xFFFFFFFF as -1.
    /// </summary>
    public virtual string FormatLine(string direction, string type, uint reliableMessageNumber, uint frame, string idToPrint,
                                     int bitLength, ulong time, SystemAddress local, SystemAddress remote, uint splitPacketId,
                                     uint splitPacketIndex, uint splitPacketCount, uint orderingIndex)
    {
        var invariant = CultureInfo.InvariantCulture;
        var reliable = reliableMessageNumber == uint.MaxValue ? "N/A" : Pad5(reliableMessageNumber);
        return string.Create(invariant,
            $"{GetLocalTime()},{_prefix}{direction},{type},{reliable},{Pad5(frame)},{idToPrint},{(uint)bitLength},{time},{Text(local)},{Text(remote)},{(int)splitPacketId},{(int)splitPacketIndex},{(int)splitPacketCount},{(int)orderingIndex},{_suffix},");
    }

    /// <summary>LogHeader: the column names.</summary>
    public virtual void LogHeader() => AddToLog(Header);

    /// <summary>WriteLog: writes the line and <c>\n</c>. Override to send lines elsewhere.</summary>
    public virtual void WriteLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _writer.Write(line + "\n");
    }

    /// <summary>
    /// WriteMiscellaneous (PacketLogger.cpp:202-223), which formats an informational line and then logs only
    /// <paramref name="message"/>.
    /// </summary>
    public virtual void WriteMiscellaneous(string type, string message) => AddToLog(message);

    /// <summary>OnDirectSocketSend (PacketLogger.cpp:107-115).</summary>
    public virtual void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote)
    {
        if (!LogDirectMessages || datagram.IsEmpty)
            return;
        // RakNet::GetTimeMS(): the 32-bit clock.
        AddToLog(FormatLine("Snd", "Raw", 0, 0, datagram[0], datagram.Length * 8, (uint)Now, ExternalAddressFor(remote), remote,
            uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue));
    }

    /// <summary>OnDirectSocketReceive (PacketLogger.cpp:122-130).</summary>
    public virtual void OnDatagramReceived(ReadOnlySpan<byte> datagram, SystemAddress remote)
    {
        if (!LogDirectMessages || datagram.IsEmpty)
            return;
        AddToLog(FormatLine("Rcv", "Raw", 0, 0, datagram[0], datagram.Length * 8, Now, FirstInternalAddress, remote,
            uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue));
    }

    /// <summary>OnReliabilityLayerNotification (PacketLogger.cpp:131-142): the message is the type column.</summary>
    public virtual void OnReliabilityNotification(ReadOnlySpan<char> message, int bitLength, SystemAddress remote, bool isError) =>
        AddToLog(FormatLine(isError ? "RcvErr" : "RcvWrn", message.ToString(), 0, 0, "", bitLength, Now, FirstInternalAddress,
            remote, uint.MaxValue, uint.MaxValue, uint.MaxValue, uint.MaxValue));

    /// <summary>OnAck (PacketLogger.cpp:143-159).</summary>
    public virtual void OnAck(uint messageNumber, SystemAddress remote, ulong timeMs) =>
        AddToLog(string.Create(CultureInfo.InvariantCulture,
            $"{GetLocalTime()},Rcv,Ack,{(int)messageNumber},,,,{timeMs},{Text(ExternalAddressFor(remote))},{Text(remote)},,,,,,"));

    /// <summary>OnPushBackPacket (PacketLogger.cpp:160-177).</summary>
    public virtual void OnInjected(ReadOnlySpan<byte> data, int bitLength, SystemAddress source)
    {
        var id = data.IsEmpty ? null : BaseIdToString(data[0]);
        AddToLog(string.Create(CultureInfo.InvariantCulture,
            $"{GetLocalTime()},Lcl,PBP,,,{id ?? "(null)"},{bitLength},{(uint)Now},{Text(ExternalAddressFor(source))},{Text(source)},,,,,,"));
    }

    /// <summary>
    /// OnInternalPacket (PacketLogger.cpp:178-195): "Nrm", or "Tms" with the ID after the 8-byte time for a timestamped
    /// message; the reliable number is N/A for the unreliable types.
    /// </summary>
    public virtual void OnFrame(in FrameInfo frame)
    {
        var reliableMessageNumber = frame.Reliability is Reliability.Unreliable or Reliability.UnreliableSequenced
                                        or Reliability.UnreliableWithAckReceipt
            ? uint.MaxValue
            : frame.MessageNumber.Value;
        var data = frame.Data;
        var local = frame.Connection is { } connection ? connection.ExternalAddress : ExternalAddressFor(frame.Remote);
        var isTimestamp = !data.IsEmpty && data[0] == (byte)MessageId.Timestamp;
        // data[1 + sizeof(RakNet::Time)]; a message too short to hold it logs ID 0 rather than reading past its end.
        var id = isTimestamp ? (data.Length > 9 ? data[9] : (byte)0) : (data.IsEmpty ? (byte)0 : data[0]);
        AddToLog(FormatLine(frame.IsSend ? "Snd" : "Rcv", isTimestamp ? "Tms" : "Nrm", reliableMessageNumber, frame.FrameNumber,
            id, frame.BitLength, frame.TimeMs, local, frame.Remote, frame.SplitId, frame.SplitIndex, frame.SplitCount,
            frame.OrderingIndex.Value));
    }

    public virtual void OnAttached(IPluginHost host) => Host = host;

    public virtual void OnDetached() => Host = null;

    public virtual void OnUpdate()
    {
    }

    /// <summary>AddToLog: every line passes through here on its way to <see cref="WriteLog"/>.</summary>
    protected virtual void AddToLog(string line) => WriteLog(line);

    /// <summary>UserIDTOString: the text for an ID from ID_USER_PACKET_ENUM up; its number by default.</summary>
    protected virtual string UserIdToString(byte id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>IDTOString: <see cref="BaseIdToString"/>, or <see cref="UserIdToString"/> for a user ID.</summary>
    protected string IdToString(byte id) => BaseIdToString(id) ?? UserIdToString(id);

    /// <summary>
    /// GetLocalTime (PacketLogger.cpp:276-311): on Windows the local date and time as strftime's "%x %X" in the C locale
    /// followed by "." and the microseconds; empty elsewhere.
    /// </summary>
    protected virtual string GetLocalTime()
    {
        if (!OperatingSystem.IsWindows())
            return "";
        var now = (Host?.TimeProvider ?? TimeProvider.System).GetLocalNow();
        var microseconds = (int)(now.Ticks % TimeSpan.TicksPerSecond / TimeSpan.TicksPerMicrosecond);
        return string.Create(CultureInfo.InvariantCulture, $"{now:MM/dd/yy HH:mm:ss}.{microseconds}");
    }

    // RakNet::GetTime, on the host's clock.
    private ulong Now => Host?.GetTime() ?? 0;

    // GetInternalID(UNASSIGNED_SYSTEM_ADDRESS).
    private SystemAddress FirstInternalAddress =>
        Host?.Peer is { } peer && peer.InternalAddresses.Count > 0 ? peer.InternalAddresses[0] : SystemAddress.Unassigned;

    // GetExternalID(remote): our address as the connection with that system sees it, or UNASSIGNED_SYSTEM_ADDRESS.
    private SystemAddress ExternalAddressFor(SystemAddress remote) =>
        Host?.Peer is { } peer && peer.TryGetConnection(remote, out var connection) ? connection.ExternalAddress : SystemAddress.Unassigned;

    // SystemAddress::ToString(true): the port after RakNet's '|'.
    private static string Text(SystemAddress address) => address.ToString(writePort: true);

    private static string Pad5(uint value) => value.ToString(CultureInfo.InvariantCulture).PadLeft(5);

    // strncpy(prefix, _prefix, 255).
    private static string Truncate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > MaxAffixLength ? value[..MaxAffixLength] : value;
    }
}
