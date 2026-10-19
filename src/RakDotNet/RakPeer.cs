using System.Diagnostics.CodeAnalysis;
using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>A RakNet peer: RakPeerInterface in .NET form.</summary>
/// <remarks>
/// <para>
/// A peer starts once: <see cref="Start"/> returns an active peer or throws, and shutdown is final. After shutdown every
/// command throws <see cref="ObjectDisposedException"/> and every query returns its last value.
/// </para>
/// <para>
/// Commands (sends, closes, connects, setters, <see cref="BanList"/>, <see cref="Plugins"/>, <see cref="LowLevel"/>)
/// are thread-safe and never block: they enqueue for the I/O cycle and take effect on the next cycle. Queries read
/// snapshots the cycle publishes and may be one cycle old. Commands from one thread are applied in call order.
/// </para>
/// <para>
/// Hostnames are <see cref="string"/> and the host overloads resolve them with
/// <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>; RakNet resolves on the caller, blocking. No member
/// takes a hostname as bytes. Offline sends (PingUnconnected, AdvertiseSystem, the LowLevel out-of-band and TTL sends)
/// return whether the datagram was sent.
/// </para>
/// <para>
/// Byte settings (<see cref="OfflinePingResponse"/>, <see cref="IncomingPassword"/>) are copied on assignment and their
/// getters return library-owned read-only memory. Continuations of the async members run on the thread pool
/// (RunContinuationsAsynchronously), so user code never runs inside the I/O cycle, and no SynchronizationContext is
/// captured.
/// </para>
/// </remarks>
public sealed class RakPeer : IPluginHost, IAsyncDisposable, IDisposable
{
    private RakPeer() { }

    /// <summary>Binds every <see cref="RakPeerOptions.Bindings"/> entry, attaches the option plugins and starts the I/O cycle.</summary>
    /// <exception cref="RakStartupException">The environment refused a binding.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static RakPeer Start(RakPeerOptions options) => throw new NotImplementedException();

    /// <summary>
    /// RakPeer::Shutdown (RakPeer.cpp:1041-1181). The stream stops delivering; when <paramref name="blockDuration"/> is
    /// positive every connection is notified and the peer waits, on its <see cref="TimeProvider"/>, until none is active
    /// or the duration elapses (running the cycle itself in Manual mode); plugins get OnShutdown; the sockets close and
    /// undelivered entries are discarded, with no Disconnected events added (RakPeer.cpp:1177-1181).
    /// </summary>
    /// <remarks>
    /// Pending ConnectAsync, PingAsync and host overloads throw <see cref="OperationCanceledException"/>;
    /// SendAndTrackAsync completes with <see cref="DeliveryStatus.PeerShutdown"/>; Closed tasks complete.
    /// </remarks>
    public ValueTask ShutdownAsync(TimeSpan blockDuration, byte orderingChannel = 0,
                                   Priority notificationPriority = Priority.Low,
                                   CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary><see cref="ShutdownAsync"/> with no block, synchronously. Idempotent.</summary>
    public void Dispose() => throw new NotImplementedException();

    /// <summary><see cref="ShutdownAsync"/> for <see cref="RakPeerOptions.ShutdownBlockDuration"/>. Idempotent.</summary>
    public ValueTask DisposeAsync() => throw new NotImplementedException();

    public bool IsActive => throw new NotImplementedException();

    /// <summary>
    /// <see cref="PeerThreading.Manual"/> only: runs one I/O cycle on the caller (RunUpdateCycle). Part of the dispatch
    /// step, so it cannot run concurrently with a read; throws <see cref="InvalidOperationException"/> in Background mode.
    /// </summary>
    public void Update() => throw new NotImplementedException();

    /// <summary>The one ordered event stream.</summary>
    public PeerEventReader Events => throw new NotImplementedException();

    /// <summary>GetMyGUID</summary>
    public RakNetGuid RakNetGuid => throw new NotImplementedException();

    /// <summary>Every timer reads this.</summary>
    public TimeProvider TimeProvider => throw new NotImplementedException();

    /// <summary>RakNet::GetTime in milliseconds, from <see cref="TimeProvider"/>.</summary>
    public ulong GetTime() => throw new NotImplementedException();

    /// <summary>One per <see cref="SocketBinding"/>, indexed by binding.</summary>
    public IReadOnlyList<RakSocket> Sockets => throw new NotImplementedException();

    /// <summary>GetNumberOfAddresses / GetLocalIP</summary>
    public IReadOnlyList<IPAddress> LocalAddresses => throw new NotImplementedException();

    /// <summary>IsLocalIP</summary>
    public bool IsLocalAddress(IPAddress address) => throw new NotImplementedException();

    /// <summary>GetInternalID(UNASSIGNED_SYSTEM_ADDRESS, i)</summary>
    public IReadOnlyList<SystemAddress> InternalAddresses => throw new NotImplementedException();

    /// <summary>SetInternalID</summary>
    public void SetInternalAddress(SystemAddress address, int index = 0) => throw new NotImplementedException();

    /// <summary>GetExternalID(UNASSIGNED_SYSTEM_ADDRESS): the first address a remote reported for this peer.</summary>
    public SystemAddress ExternalAddress => throw new NotImplementedException();

    /// <summary>GetMaximumNumberOfPeers</summary>
    public int MaxConnections => throw new NotImplementedException();

    /// <summary>Clamped to <see cref="MaxConnections"/> at start (RakPeer.cpp:605).</summary>
    public int MaxIncomingConnections
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>NumberOfConnections</summary>
    public int ConnectionCount => throw new NotImplementedException();

    /// <summary>GetConnectionList / GetSystemList.</summary>
    public ConnectionList Connections => throw new NotImplementedException();

    public bool TryGetConnection(RakNetGuid rakNetGuid, [NotNullWhen(true)] out RakConnection? connection) =>
        throw new NotImplementedException();

    public bool TryGetConnection(SystemAddress address, [NotNullWhen(true)] out RakConnection? connection) =>
        throw new NotImplementedException();

    /// <summary>GetSystemAddressFromIndex / GetGUIDFromIndex.</summary>
    public bool TryGetConnectionByIndex(int systemIndex, [NotNullWhen(true)] out RakConnection? connection) =>
        throw new NotImplementedException();

    /// <summary>GetGuidFromSystemAddress: this peer's own GUID for <see cref="SystemAddress.Unassigned"/> (RakPeer.cpp:2396).</summary>
    public RakNetGuid GetGuidFromAddress(SystemAddress address) => throw new NotImplementedException();

    /// <summary>GetSystemAddressFromGuid</summary>
    public SystemAddress GetAddressFromGuid(RakNetGuid rakNetGuid) => throw new NotImplementedException();

    /// <summary>GetConnectionState, including <see cref="ConnectionState.Pending"/> attempts.</summary>
    public ConnectionState GetConnectionState(AddressOrGuid target) => throw new NotImplementedException();

    /// <summary>
    /// Checks the connection table and the attempt registry, then queues the attempt. The outcome later arrives on the
    /// stream as Connected or ConnectFailed.
    /// </summary>
    public ConnectAttemptResult Connect(SystemAddress remote, ConnectOptions? options = null) => throw new NotImplementedException();

    /// <summary>
    /// <see cref="Connect"/>, completing when the I/O cycle produces the Connected or ConnectFailed event, not when it
    /// is read; the event still appears on the stream. A synchronous rejection completes it at once with
    /// <see cref="ConnectResult.AttemptResult"/> set. Cancelling throws <see cref="OperationCanceledException"/> and stops
    /// the attempt: by <see cref="CancelConnectionAttempt"/> while queued, by a silent close once it has a table entry,
    /// which for a system not yet connected produces no event.
    /// </summary>
    public ValueTask<ConnectResult> ConnectAsync(SystemAddress remote, ConnectOptions? options = null,
                                                 CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>Resolves <paramref name="host"/> with <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>, then as the address overload.</summary>
    public ValueTask<ConnectResult> ConnectAsync(string host, int port, ConnectOptions? options = null,
                                                 CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>
    /// Removes a queued attempt with no stream event (RakPeer.cpp:1686-1710). Does nothing once the attempt has an entry
    /// in the connection table.
    /// </summary>
    public void CancelConnectionAttempt(SystemAddress remote) => throw new NotImplementedException();

    /// <summary>
    /// CloseConnection. With <paramref name="notify"/>, a connected system goes to Disconnecting and
    /// ID_DISCONNECTION_NOTIFICATION is enqueued once its outgoing data drains (RakPeer.cpp:5873-5903). Without, a
    /// connected system's ID_CONNECTION_LOST is enqueued at once with IsLocal set (RakPeer.cpp:1667-1678); a system
    /// not yet connected produces no event.
    /// </summary>
    public void Close(AddressOrGuid target, bool notify = true, byte orderingChannel = 0,
                      Priority notificationPriority = Priority.Low) => throw new NotImplementedException();

    /// <summary>
    /// RakPeer::Send. Takes a receipt from the peer-wide counter, or uses <paramref name="forceReceipt"/>
    /// (RakPeer.cpp:1326-1330), even when the target is not connected. A send to this peer becomes loopback
    /// (RakPeer.cpp:1332-1346).
    /// </summary>
    /// <remarks>
    /// Only the *WithAckReceipt reliabilities produce receipt events: a reliable receipt ends with ReceiptAcked, an
    /// unreliable one with ReceiptAcked or, once <see cref="UnreliableTimeout"/> passes without an ACK, ReceiptLost
    /// (ReliabilityLayer.cpp:1860-1876). A receipt whose connection ends, or whose target the I/O cycle cannot find, ends
    /// with ReceiptAbandoned; at shutdown <see cref="PeerEventReader.Completion"/> ends it. No socket I/O happens on the
    /// caller's thread: <see cref="Priority.Immediate"/> wakes the loop at once in Background mode and goes out first in
    /// the next <see cref="Update"/> in Manual mode.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An empty message, <paramref name="orderingChannel"/> of 32 or more, an undefined enum value or an undefined target
    /// (RakNet asserts, RakPeer.cpp:1311-1315).
    /// </exception>
    public SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability,
                           Priority priority = Priority.High, byte orderingChannel = 0,
                           SendReceipt forceReceipt = default) => throw new NotImplementedException();

    /// <summary>A send whose bytes <paramref name="write"/> writes straight into the pooled outgoing segment.</summary>
    public SendResult Send<TState>(AddressOrGuid target, Reliability reliability, Priority priority,
                                   byte orderingChannel, int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct => throw new NotImplementedException();

    /// <summary>
    /// RakNet's <c>broadcast = true</c>: every connected system except <paramref name="except"/>, one shared receipt and
    /// one receipt event per recipient.
    /// </summary>
    public SendResult Broadcast(OutgoingMessage message, Reliability reliability,
                                Priority priority = Priority.High, byte orderingChannel = 0,
                                AddressOrGuid except = default, SendReceipt forceReceipt = default) =>
        throw new NotImplementedException();

    /// <inheritdoc cref="Broadcast(OutgoingMessage, Reliability, Priority, byte, AddressOrGuid, SendReceipt)"/>
    public SendResult Broadcast<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                        int sizeHint, TState state, MessageWriter<TState> write,
                                        AddressOrGuid except = default)
        where TState : allows ref struct => throw new NotImplementedException();

    /// <summary>
    /// A send whose receipt completes the returned task instead of appearing on the stream. Accepts a reliability with a
    /// receipt flavour (Unreliable, Reliable, ReliableOrdered or their *WithAckReceipt forms) and sends the receipt
    /// flavour; the sequenced reliabilities, which RakNet gives no receipt flavour, throw <see cref="ArgumentException"/>.
    /// </summary>
    /// <remarks>
    /// The task is backed by a pooled IValueTaskSource returned to the pool in GetResult, so it is awaited once.
    /// </remarks>
    public ValueTask<DeliveryOutcome> SendAndTrackAsync(OutgoingMessage message, AddressOrGuid target,
                                                        Reliability reliability, Priority priority = Priority.High,
                                                        byte orderingChannel = 0,
                                                        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>Queues the bytes as a local packet through the injection path, as RakPeer::SendLoopback (RakPeer.cpp:1354-1364).</summary>
    public void SendLoopback(ReadOnlySpan<byte> message) => throw new NotImplementedException();

    /// <summary>GetNextSendReceipt</summary>
    public SendReceipt PeekNextReceipt() => throw new NotImplementedException();

    /// <summary>IncrementNextSendReceipt</summary>
    public SendReceipt ReserveReceipt() => throw new NotImplementedException();

    /// <summary>
    /// PushBackPacket with a copy of <paramref name="data"/>. With <paramref name="atHead"/> the next read returns it
    /// (RakPeer.cpp:2838-2841). Plugins get OnInjected first.
    /// </summary>
    public void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false) =>
        throw new NotImplementedException();

    /// <summary>PushBackPacket, consuming <paramref name="owned"/> without a copy.</summary>
    public void Inject(OwnedPeerEvent owned, bool atHead = false) => throw new NotImplementedException();

    /// <summary>Ping(host, port, ...): an unconnected ping, answered by an UnconnectedPong event.</summary>
    public bool PingUnconnected(SystemAddress remote, bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0) =>
        throw new NotImplementedException();

    /// <inheritdoc cref="PingUnconnected"/>
    public ValueTask<bool> PingUnconnectedAsync(string host, int port, bool onlyReplyOnAcceptingConnections = false,
                                                int socketIndex = 0, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>An unconnected ping whose pong completes the task; the pong still appears on the stream.</summary>
    public ValueTask<PongResult> PingAsync(SystemAddress remote, TimeSpan timeout,
                                           bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0,
                                           CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>SetOfflinePingResponse. Copied on set; more than 400 bytes throws (RakPeer.cpp:127).</summary>
    public ReadOnlyMemory<byte> OfflinePingResponse
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>AdvertiseSystem; received as an AdvertiseSystem event.</summary>
    public bool AdvertiseSystem(SystemAddress remote, ReadOnlySpan<byte> data = default, int socketIndex = 0) =>
        throw new NotImplementedException();

    /// <inheritdoc cref="AdvertiseSystem"/>
    public ValueTask<bool> AdvertiseSystemAsync(string host, int port, ReadOnlyMemory<byte> data = default,
                                                int socketIndex = 0, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>SetIncomingPassword. Copied on set; more than 255 bytes throws.</summary>
    public ReadOnlyMemory<byte> IncomingPassword
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetTimeoutTime(t, UNASSIGNED_SYSTEM_ADDRESS): the default and every live connection (RakPeer.cpp:2526-2538).</summary>
    public TimeSpan DefaultTimeout
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetUnreliableTimeout</summary>
    public TimeSpan UnreliableTimeout
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetOccasionalPing</summary>
    public bool OccasionalPing
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetSplitMessageProgressInterval; progress arrives as DownloadProgress events.</summary>
    public int SplitMessageProgressInterval
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetLimitIPConnectionFrequency</summary>
    public bool LimitIpConnectionFrequency
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>AllowConnectionResponseIPMigration</summary>
    public bool AllowConnectionResponseIpMigration
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>SetPerConnectionOutgoingBandwidthLimit, in bits per second; 0 is unlimited.</summary>
    public long PerConnectionOutgoingBandwidthLimit
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    public BanList BanList => throw new NotImplementedException();

    /// <summary>The security exception list; applies to incoming connections only.</summary>
    public AddressPatternCollection SecurityExceptions => throw new NotImplementedException();

    /// <summary>Whether <see cref="RakPeerOptions.Security"/> was set at start.</summary>
    public bool IsSecurityEnabled => throw new NotImplementedException();

    /// <summary>GetStatisticsList: copies up to <paramref name="destination"/>'s length and returns the count copied.</summary>
    public int CopyStatistics(Span<ConnectionStatistics> destination) => throw new NotImplementedException();

    public PluginCollection Plugins => throw new NotImplementedException();

    /// <summary>Out-of-band sends, TTL, address migration, datagram filtering, the network simulator and observers.</summary>
    public RakPeerLowLevel LowLevel => throw new NotImplementedException();

    public PoolStatistics Pools => throw new NotImplementedException();

    RakPeer? IPluginHost.Peer => this;
}
