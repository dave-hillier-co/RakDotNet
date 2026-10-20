using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using RakDotNet.Security;
using RakDotNet.Transport;

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
/// snapshots the cycle publishes and may be one cycle old. Commands from one thread are applied in call order, and all
/// the commands waiting at the start of a cycle are applied before it frames datagrams, so several sends share
/// datagrams as they do in RakNet.
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
public sealed partial class RakPeer : IPluginHost, IAsyncDisposable, IDisposable
{
    private const int Active = 0;
    private const int ShuttingDown = 1;
    private const int Stopped = 2;

    // RakPeer's update thread waits at most 10 ms for data (UpdateNetworkLoop, RakPeer.cpp:6352-6358).
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(10);

    // RakPeer::Shutdown polls for active systems every 15 ms (RakPeer.cpp:1082).
    private static readonly TimeSpan ShutdownPollInterval = TimeSpan.FromMilliseconds(15);

    private readonly RakPeerOptions _options;
    private readonly TimeProvider _time;
    private readonly long _startTimestamp;
    private readonly IDatagramTransport[] _transports;
    private readonly RakSocket[] _sockets;
    private readonly IPAddress[] _localAddresses;
    private readonly IPEndPoint[] _coreInternalAddresses;
    private volatile SystemAddress[] _internalAddresses;
    private readonly ConcurrentQueue<PeerCommand> _commands = new();
    private readonly ConcurrentQueue<DispatchOp> _dispatchOps = new();
    private readonly EventPool _pool = new();
    private readonly EventQueue _queue = new();
    private readonly PeerEventReader _reader;
    private readonly PluginCollection _plugins;
    private readonly RakPeerLowLevel _lowLevel;
    private readonly int _eventCapacity;
    private readonly Lock _attemptsGate = new();
    private readonly Lock _internalAddressesGate = new();
    private readonly Dictionary<SystemAddress, PendingConnect> _attempts = [];
    private readonly Lock _pingsGate = new();
    private readonly List<PendingPing> _pings = [];
    private readonly Lock _trackedGate = new();
    private readonly Stack<TrackedSend> _trackedPool = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _state;
    private Exception? _stopFault;
    private volatile bool _streamClosed;
    private uint _nextReceipt = 1;
    private int _dispatchThread;
    private EventSegment? _borrowed;
    private int _borrowedGeneration;
    private volatile ConnectionTable _table = ConnectionTable.Empty;
    private volatile byte[] _offlinePingResponse;
    private volatile int _maxIncomingConnections;
    private volatile SystemAddressBox _externalAddress = new(SystemAddress.Unassigned);
    private readonly BanList _banList;
    private volatile byte[] _incomingPassword;
    private long _defaultTimeoutMs;
    private long _unreliableTimeoutMs;
    private volatile bool _occasionalPing;
    private volatile int _splitMessageProgressInterval;
    private volatile bool _limitIpConnectionFrequency;
    private volatile bool _allowConnectionResponseIpMigration;
    private long _outgoingBandwidthLimit;

    private RakPeer(RakPeerOptions options, RakNetGuid guid, IDatagramTransport[] transports, IPAddress[] localAddresses,
                    IPEndPoint[] internalAddresses, PeerCore core, int eventCapacity)
    {
        _options = options;
        _time = options.TimeProvider;
        _startTimestamp = _time.GetTimestamp();
        RakNetGuid = guid;
        _transports = transports;
        _sockets = [.. transports.Select((t, i) => new RakSocket(i, t))];
        _localAddresses = localAddresses;
        _core = core;
        _coreInternalAddresses = internalAddresses;
        _internalAddresses = [.. internalAddresses.Select(a => (SystemAddress)a)];
        _eventCapacity = eventCapacity;
        _offlinePingResponse = options.OfflinePingResponse.ToArray();
        _maxIncomingConnections = Math.Min(options.MaxIncomingConnections, options.MaxConnections);
        _banList = new BanList(this, core.Bans);
        _incomingPassword = options.IncomingPassword.ToArray();
        _defaultTimeoutMs = (long)options.DefaultTimeout.TotalMilliseconds;
        _unreliableTimeoutMs = (long)options.UnreliableTimeout.TotalMilliseconds;
        _occasionalPing = options.OccasionalPing;
        _splitMessageProgressInterval = options.SplitMessageProgressInterval;
        _limitIpConnectionFrequency = options.LimitIpConnectionFrequency;
        _allowConnectionResponseIpMigration = options.AllowConnectionResponseIpMigration;
        _outgoingBandwidthLimit = options.PerConnectionOutgoingBandwidthLimit;
        _reader = new PeerEventReader(this);
        _plugins = new PluginCollection(this);
        _lowLevel = new RakPeerLowLevel(this, options.NetworkSimulator);
        _simulator.Configure(options.NetworkSimulator);
    }

    /// <summary>Binds every <see cref="RakPeerOptions.Bindings"/> entry, attaches the option plugins and starts the I/O cycle.</summary>
    /// <exception cref="RakStartupException">The environment refused a binding.</exception>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    public static RakPeer Start(RakPeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var transports = Bind(options);
        try
        {
            var guid = options.RakNetGuid ?? DrawGuid(options.Random);
            var localAddresses = HostAddresses();
            // The core reads ipList whenever it builds a handshake message, so SetInternalAddress changes this array in
            // the I/O cycle.
            var internalAddresses = LocalAddressList.Build(localAddresses, transports[0].LocalAddress.Port).ToArray();
            var core = new PeerCore(new PeerOptions
            {
                Guid = guid.Value,
                MaxConnections = options.MaxConnections,
                MaxIncomingConnections = options.MaxIncomingConnections,
                LocalAddresses = internalAddresses,
                IncomingPassword = options.IncomingPassword.ToArray(),
                TimeoutMs = (long)options.DefaultTimeout.TotalMilliseconds,
                UnreliableTimeoutMs = (long)options.UnreliableTimeout.TotalMilliseconds,
                SplitMessageProgressInterval = options.SplitMessageProgressInterval,
                LimitIpConnectionFrequency = options.LimitIpConnectionFrequency,
                AllowConnectionResponseIpMigration = options.AllowConnectionResponseIpMigration,
                OutgoingBandwidthLimit = (uint)options.PerConnectionOutgoingBandwidthLimit,
                SessionLimits = options.Limits.ToSessionLimits(),
            })
            {
                OccasionalPing = options.OccasionalPing,
            };
            core.SetOfflinePingResponse(options.OfflinePingResponse.Span);
            var peer = new RakPeer(options, guid, transports, [.. localAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)],
                                   internalAddresses, core, options.Limits.EventQueueCapacity);
            peer.StartPlugins();
            peer.StartLoop();
            return peer;
        }
        catch
        {
            foreach (var transport in transports)
                transport.Dispose();
            throw;
        }
    }

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
    public async ValueTask ShutdownAsync(TimeSpan blockDuration, byte orderingChannel = 0,
                                         Priority notificationPriority = Priority.Low,
                                         CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, ShuttingDown, Active) != Active)
        {
            await _stopped.Task.ConfigureAwait(false);
            return;
        }

        CloseStream();
        try
        {
            if (blockDuration > TimeSpan.Zero)
            {
                Enqueue(new PeerCommand { Kind = CommandKind.NotifyShutdown, Channel = orderingChannel, Priority = notificationPriority });
                var started = _time.GetTimestamp();
                RunCycleIfManual();
                while (_time.GetElapsedTime(started) < blockDuration && (_activeSystemCount > 0 || !_commands.IsEmpty))
                {
                    await Task.Delay(ShutdownPollInterval, _time, cancellationToken).ConfigureAwait(false);
                    RunCycleIfManual();
                }
            }
        }
        finally
        {
            Stop(fault: null);
        }
    }

    /// <summary><see cref="ShutdownAsync"/> with no block, synchronously. Idempotent.</summary>
    public void Dispose() => ShutdownAsync(TimeSpan.Zero).AsTask().GetAwaiter().GetResult();

    /// <summary><see cref="ShutdownAsync"/> for <see cref="RakPeerOptions.ShutdownBlockDuration"/>. Idempotent.</summary>
    public ValueTask DisposeAsync() => ShutdownAsync(_options.ShutdownBlockDuration);

    public bool IsActive => Volatile.Read(ref _state) == Active;

    /// <summary>
    /// <see cref="PeerThreading.Manual"/> only: runs one I/O cycle on the caller (RunUpdateCycle). Part of the dispatch
    /// step, so it cannot run concurrently with a read; throws <see cref="InvalidOperationException"/> in Background mode.
    /// </summary>
    public void Update()
    {
        if (_options.Threading != PeerThreading.Manual)
            throw new InvalidOperationException("Update runs the I/O cycle only in PeerThreading.Manual; the background loop runs it otherwise.");
        ThrowIfStopped();
        EnterDispatch();
        try
        {
            ReleaseBorrowed();
            RunCycle();
        }
        finally
        {
            ExitDispatch();
        }
    }

    /// <summary>The one ordered event stream.</summary>
    public PeerEventReader Events => _reader;

    /// <summary>GetMyGUID</summary>
    public RakNetGuid RakNetGuid { get; }

    /// <summary>Every timer reads this.</summary>
    public TimeProvider TimeProvider => _time;

    /// <summary>RakNet::GetTime in milliseconds, from <see cref="TimeProvider"/>.</summary>
    public ulong GetTime() => (ulong)(NowUs / 1000);

    /// <summary>One per <see cref="SocketBinding"/>, indexed by binding.</summary>
    public IReadOnlyList<RakSocket> Sockets => _sockets;

    /// <summary>GetNumberOfAddresses / GetLocalIP</summary>
    public IReadOnlyList<IPAddress> LocalAddresses => _localAddresses;

    /// <summary>IsLocalIP: 127.0.0.1 or an address of this host (RakPeer.cpp:2636-2657).</summary>
    public bool IsLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.Equals(IPAddress.Loopback) || Array.IndexOf(_localAddresses, address) >= 0;
    }

    /// <summary>GetInternalID(UNASSIGNED_SYSTEM_ADDRESS, i)</summary>
    public IReadOnlyList<SystemAddress> InternalAddresses => _internalAddresses;

    /// <summary>
    /// SetInternalID: replaces ipList[<paramref name="index"/>] (RakPeer.cpp:2336-2340), the list later handshakes send
    /// and <see cref="InternalAddresses"/> shows. Index 0 is also the loopback address.
    /// </summary>
    public void SetInternalAddress(SystemAddress address, int index = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, RakNetConstants.MaximumNumberOfInternalIds);
        ThrowIfStopped();
        lock (_internalAddressesGate)
        {
            var updated = (SystemAddress[])_internalAddresses.Clone();
            updated[index] = address;
            _internalAddresses = updated;
        }
        Enqueue(new PeerCommand { Kind = CommandKind.SetInternalAddress, Target = address, Value = index });
    }

    /// <summary>GetExternalID(UNASSIGNED_SYSTEM_ADDRESS): the first address a remote reported for this peer.</summary>
    public SystemAddress ExternalAddress => _externalAddress.Value;

    /// <summary>GetMaximumNumberOfPeers</summary>
    public int MaxConnections => _options.MaxConnections;

    /// <summary>Clamped to <see cref="MaxConnections"/> at start (RakPeer.cpp:605).</summary>
    public int MaxIncomingConnections
    {
        get => _maxIncomingConnections;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ThrowIfStopped();
            _maxIncomingConnections = value;
            Enqueue(new PeerCommand { Kind = CommandKind.SetMaxIncoming, Value = value });
        }
    }

    /// <summary>
    /// NumberOfConnections: the connections whose <see cref="RakConnection.State"/> is Connected, as RakNet counts only
    /// CONNECTED systems (RakPeer.cpp:920-926, 1832-1850). A connection that is disconnecting is not counted.
    /// </summary>
    public int ConnectionCount => _table.Connected.Length;

    /// <summary>
    /// GetConnectionList / GetSystemList: the connections whose <see cref="RakConnection.State"/> is Connected
    /// (RakPeer.cpp:1832-1850). <see cref="TryGetConnection(RakNetGuid, out RakConnection?)"/> still finds a connection
    /// while it is disconnecting.
    /// </summary>
    public ConnectionList Connections => new(_table.Connected);

    public bool TryGetConnection(RakNetGuid rakNetGuid, [NotNullWhen(true)] out RakConnection? connection) =>
        _table.ByGuid.TryGetValue(rakNetGuid, out connection);

    public bool TryGetConnection(SystemAddress address, [NotNullWhen(true)] out RakConnection? connection) =>
        _table.ByAddress.TryGetValue(address, out connection);

    /// <summary>GetSystemAddressFromIndex / GetGUIDFromIndex.</summary>
    public bool TryGetConnectionByIndex(int systemIndex, [NotNullWhen(true)] out RakConnection? connection)
    {
        connection = Array.Find(_table.All, c => c.SystemIndex == systemIndex);
        return connection is not null;
    }

    /// <summary>GetGuidFromSystemAddress: this peer's own GUID for <see cref="SystemAddress.Unassigned"/> (RakPeer.cpp:2396).</summary>
    public RakNetGuid GetGuidFromAddress(SystemAddress address)
    {
        if (!address.IsAssigned)
            return RakNetGuid;
        return TryGetConnection(address, out var connection) ? connection.RakNetGuid : RakNetGuid.Unassigned;
    }

    /// <summary>GetSystemAddressFromGuid</summary>
    public SystemAddress GetAddressFromGuid(RakNetGuid rakNetGuid) =>
        TryGetConnection(rakNetGuid, out var connection) ? connection.Address : SystemAddress.Unassigned;

    /// <summary>GetConnectionState, including <see cref="ConnectionState.Pending"/> attempts.</summary>
    public ConnectionState GetConnectionState(AddressOrGuid target)
    {
        if (target.IsUndefined)
            return ConnectionState.NotConnected;
        if (target.RakNetGuid.IsAssigned)
            return TryGetConnection(target.RakNetGuid, out var byGuid) ? byGuid.State : ConnectionState.NotConnected;
        lock (_attemptsGate)
        {
            if (_attempts.TryGetValue(target.Address, out var attempt))
                return attempt.State;
        }
        return TryGetConnection(target.Address, out var byAddress) ? byAddress.State : ConnectionState.NotConnected;
    }

    /// <summary>
    /// Checks the connection table and the attempt registry, then queues the attempt. The outcome later arrives on the
    /// stream as Connected or ConnectFailed.
    /// </summary>
    public ConnectAttemptResult Connect(SystemAddress remote, ConnectOptions? options = null) =>
        StartConnect(remote, options ?? DefaultConnectOptions, completion: null, out _);

    /// <summary>
    /// <see cref="Connect"/>, completing when the I/O cycle produces the Connected or ConnectFailed event, not when it
    /// is read; the event still appears on the stream. A synchronous rejection completes it at once with
    /// <see cref="ConnectResult.AttemptResult"/> set. Cancelling throws <see cref="OperationCanceledException"/> and stops
    /// the attempt: by <see cref="CancelConnectionAttempt"/> while queued, by a silent close once it has a table entry,
    /// which for a system not yet connected produces no event.
    /// </summary>
    public ValueTask<ConnectResult> ConnectAsync(SystemAddress remote, ConnectOptions? options = null,
                                                 CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<ConnectResult>(cancellationToken);
        var completion = new TaskCompletionSource<ConnectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = StartConnect(remote, options ?? DefaultConnectOptions, completion, out var pending);
        if (result != ConnectAttemptResult.Started)
            return new ValueTask<ConnectResult>(new ConnectResult(result, failure: null, connection: null, remoteProtocolVersion: 0));
        if (cancellationToken.CanBeCanceled)
        {
            pending!.Token = cancellationToken;
            pending.Registration = cancellationToken.UnsafeRegister(static (state, token) =>
            {
                var (peer, attempt) = ((RakPeer, PendingConnect))state!;
                peer.TryEnqueue(new PeerCommand { Kind = CommandKind.AbortConnect, State = attempt, Value = 0 }, token);
            }, (this, pending));
        }
        return new ValueTask<ConnectResult>(completion.Task);
    }

    /// <summary>Resolves <paramref name="host"/> with <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>, then as the address overload.</summary>
    public async ValueTask<ConnectResult> ConnectAsync(string host, int port, ConnectOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, port, cancellationToken).ConfigureAwait(false);
        if (address is not { } remote)
            return new ConnectResult(ConnectAttemptResult.CannotResolveDomainName, failure: null, connection: null, remoteProtocolVersion: 0);
        return await ConnectAsync(remote, options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a queued attempt with no stream event (RakPeer.cpp:1686-1710). Does nothing once the attempt has an entry
    /// in the connection table.
    /// </summary>
    /// <remarks>
    /// As in RakNet, where the attempt leaves requestedConnectionQueue under its mutex at once, the address is free for
    /// a new <see cref="Connect"/> as soon as this returns. An attempt that the I/O cycle finds already has a table entry
    /// carries on.
    /// </remarks>
    public void CancelConnectionAttempt(SystemAddress remote)
    {
        ThrowIfStopped();
        PendingConnect? attempt;
        lock (_attemptsGate)
        {
            if (!_attempts.TryGetValue(remote, out attempt) || attempt.State != ConnectionState.Pending)
                return;
            _attempts.Remove(remote);
        }
        Enqueue(new PeerCommand { Kind = CommandKind.CancelConnect, Target = remote, State = attempt });
    }

    /// <summary>
    /// CloseConnection. With <paramref name="notify"/>, a connected system goes to Disconnecting and
    /// ID_DISCONNECTION_NOTIFICATION is enqueued once its outgoing data drains (RakPeer.cpp:5873-5903). Without, a
    /// connected system's ID_CONNECTION_LOST is enqueued at once with IsLocal set (RakPeer.cpp:1667-1678); a system
    /// not yet connected produces no event.
    /// </summary>
    public void Close(AddressOrGuid target, bool notify = true, byte orderingChannel = 0,
                      Priority notificationPriority = Priority.Low)
    {
        if (target.IsUndefined)
            throw new ArgumentException("The target is undefined.", nameof(target));
        ValidateChannelAndPriority(orderingChannel, notificationPriority);
        ThrowIfStopped();
        if (notify && TryGetConnection(target, out var connection))
            connection.MarkDisconnecting();
        Enqueue(new PeerCommand { Kind = CommandKind.Close, Target = target, Flag = notify, Channel = orderingChannel, Priority = notificationPriority });
    }

    /// <summary>
    /// RakPeer::Send. Takes a receipt from the peer-wide counter, or uses <paramref name="forceReceipt"/>
    /// (RakPeer.cpp:1326-1330), even when the target is not connected. A send to this peer becomes loopback
    /// (RakPeer.cpp:1332-1346).
    /// </summary>
    /// <remarks>
    /// Only the *WithAckReceipt reliabilities produce receipt events: a reliable receipt ends with ReceiptAcked, an
    /// unreliable one with ReceiptAcked or, once its datagram's retransmission timeout passes without an ACK,
    /// ReceiptLost (ReliabilityLayer.cpp:1860-1876). An unreliable message still unsent when
    /// <see cref="UnreliableTimeout"/> passes is discarded with no RakNet packet (ReliabilityLayer.cpp:1745-1793), so its
    /// receipt ends with ReceiptAbandoned, as does a receipt whose connection ends or whose target the I/O cycle cannot
    /// find; at shutdown <see cref="PeerEventReader.Completion"/> ends it. No socket I/O happens on the
    /// caller's thread: <see cref="Priority.Immediate"/> wakes the loop at once in Background mode and goes out first in
    /// the next <see cref="Update"/> in Manual mode.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// An empty message, <paramref name="orderingChannel"/> of 32 or more, an undefined enum value or an undefined target
    /// (RakNet asserts, RakPeer.cpp:1311-1315).
    /// </exception>
    public SendResult Send(OutgoingMessage message, AddressOrGuid target, Reliability reliability,
                           Priority priority = Priority.High, byte orderingChannel = 0,
                           SendReceipt forceReceipt = default)
    {
        ValidateSend(message.BitLength, reliability, priority, orderingChannel);
        if (target.IsUndefined)
            throw new ArgumentException("The target is undefined.", nameof(target));
        var payload = Rent(message);
        return SendCore(payload, message.BitLength, target, broadcast: false, reliability, priority, orderingChannel, forceReceipt, tracker: null);
    }

    /// <summary>A send whose bytes <paramref name="write"/> writes straight into the pooled outgoing segment.</summary>
    public SendResult Send<TState>(AddressOrGuid target, Reliability reliability, Priority priority,
                                   byte orderingChannel, int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeHint);
        if (target.IsUndefined)
            throw new ArgumentException("The target is undefined.", nameof(target));
        var (payload, bitLength) = Write(sizeHint, state, write);
        ValidateSend(bitLength, reliability, priority, orderingChannel, payload);
        return SendCore(payload, bitLength, target, broadcast: false, reliability, priority, orderingChannel, default, tracker: null);
    }

    /// <summary>
    /// RakNet's <c>broadcast = true</c>: every connected system except <paramref name="except"/>, one shared receipt and
    /// one receipt event per recipient.
    /// </summary>
    public SendResult Broadcast(OutgoingMessage message, Reliability reliability,
                                Priority priority = Priority.High, byte orderingChannel = 0,
                                AddressOrGuid except = default, SendReceipt forceReceipt = default)
    {
        ValidateSend(message.BitLength, reliability, priority, orderingChannel);
        var payload = Rent(message);
        return SendCore(payload, message.BitLength, except, broadcast: true, reliability, priority, orderingChannel, forceReceipt, tracker: null);
    }

    /// <inheritdoc cref="Broadcast(OutgoingMessage, Reliability, Priority, byte, AddressOrGuid, SendReceipt)"/>
    public SendResult Broadcast<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                        int sizeHint, TState state, MessageWriter<TState> write,
                                        AddressOrGuid except = default)
        where TState : allows ref struct
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeHint);
        var (payload, bitLength) = Write(sizeHint, state, write);
        ValidateSend(bitLength, reliability, priority, orderingChannel, payload);
        return SendCore(payload, bitLength, except, broadcast: true, reliability, priority, orderingChannel, default, tracker: null);
    }

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
                                                        CancellationToken cancellationToken = default)
    {
        var tracked = reliability switch
        {
            Reliability.Unreliable or Reliability.UnreliableWithAckReceipt => Reliability.UnreliableWithAckReceipt,
            Reliability.Reliable or Reliability.ReliableWithAckReceipt => Reliability.ReliableWithAckReceipt,
            Reliability.ReliableOrdered or Reliability.ReliableOrderedWithAckReceipt => Reliability.ReliableOrderedWithAckReceipt,
            Reliability.UnreliableSequenced or Reliability.ReliableSequenced =>
                throw new ArgumentException("RakNet has no receipt flavour of the sequenced reliabilities.", nameof(reliability)),
            _ => throw new ArgumentOutOfRangeException(nameof(reliability), reliability, null),
        };
        ValidateSend(message.BitLength, tracked, priority, orderingChannel);
        if (target.IsUndefined)
            throw new ArgumentException("The target is undefined.", nameof(target));
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<DeliveryOutcome>(cancellationToken);

        var tracker = RentTracker();
        var version = tracker.Version;
        var task = tracker.Task;
        var payload = Rent(message);
        var result = SendCore(payload, message.BitLength, target, broadcast: false, tracked, priority, orderingChannel, default, tracker);
        if (!result.IsQueued)
        {
            tracker.TryComplete(version, new DeliveryOutcome(DeliveryStatus.NotSent, result.Receipt, result.Status));
            return task;
        }
        if (cancellationToken.CanBeCanceled)
        {
            tracker.Registration = cancellationToken.UnsafeRegister(static (state, token) =>
            {
                var (source, sourceVersion) = ((TrackedSend, short))state!;
                source.TryFail(sourceVersion, new OperationCanceledException(token));
            }, (tracker, version));
        }
        return task;
    }

    /// <summary>Queues the bytes as a local packet through the injection path, as RakPeer::SendLoopback (RakPeer.cpp:1354-1364).</summary>
    public void SendLoopback(ReadOnlySpan<byte> message)
    {
        ThrowIfStopped();
        Inject(message, LoopbackAddress, RakNetGuid, atHead: false);
    }

    /// <summary>GetNextSendReceipt</summary>
    public SendReceipt PeekNextReceipt() => new(Volatile.Read(ref _nextReceipt));

    /// <summary>IncrementNextSendReceipt</summary>
    public SendReceipt ReserveReceipt()
    {
        // RakPeer.cpp:1279-1287: never 0.
        while (true)
        {
            var current = Volatile.Read(ref _nextReceipt);
            var next = current == uint.MaxValue ? 1 : current + 1;
            if (Interlocked.CompareExchange(ref _nextReceipt, next, current) == current)
                return new SendReceipt(current);
        }
    }

    /// <summary>
    /// PushBackPacket with a copy of <paramref name="data"/>. With <paramref name="atHead"/> the next read returns it
    /// (RakPeer.cpp:2838-2841). Plugins get OnInjected first.
    /// </summary>
    public void Inject(ReadOnlySpan<byte> data, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false)
    {
        ThrowIfStopped();
        var segment = _pool.Rent();
        segment.SetData(data);
        segment.Address = source;
        segment.RakNetGuid = sourceGuid;
        segment.Connection = sourceGuid.IsAssigned && TryGetConnection(sourceGuid, out var connection) ? connection : null;
        Classify(segment);
        InjectSegment(segment, atHead);
    }

    /// <summary>PushBackPacket, consuming <paramref name="owned"/> without a copy.</summary>
    public void Inject(OwnedPeerEvent owned, bool atHead = false) => Inject(owned, null, atHead);

    /// <summary>
    /// PushBackPacket of a received packet whose address and GUID were changed (RakPeer.cpp:2826-2843): consumes
    /// <paramref name="owned"/> without a copy, keeping its bytes, bit length and <see cref="PeerEvent.IsLocal"/>.
    /// </summary>
    public void Inject(OwnedPeerEvent owned, SystemAddress source, RakNetGuid sourceGuid, bool atHead = false) =>
        Inject(owned, (source, sourceGuid), atHead);

    private void Inject(OwnedPeerEvent owned, (SystemAddress Address, RakNetGuid Guid)? source, bool atHead)
    {
        ThrowIfStopped();
        var segment = owned.Handle;
        if (segment is null || !segment.TryTake(owned.HandleGeneration))
            throw new ObjectDisposedException(nameof(OwnedPeerEvent), "The owned event was disposed or already consumed.");
        if (source is { } from)
        {
            segment.Address = from.Address;
            segment.RakNetGuid = from.Guid;
            segment.Connection = from.Guid.IsAssigned && TryGetConnection(from.Guid, out var connection) ? connection : null;
        }
        if (!ReferenceEquals(segment.Pool, _pool))
        {
            var copy = _pool.Rent();
            copy.SetData(segment.Data);
            copy.BitLength = segment.BitLength;
            copy.Address = segment.Address;
            copy.RakNetGuid = segment.RakNetGuid;
            copy.IsLocal = segment.IsLocal;
            Classify(copy);
            segment.Pool.Reuse(segment);
            segment.Pool.Return(segment);
            segment = copy;
        }
        else
        {
            _pool.Reuse(segment);
        }
        InjectSegment(segment, atHead);
    }

    /// <summary>Ping(host, port, ...): an unconnected ping, answered by an UnconnectedPong event.</summary>
    public bool PingUnconnected(SystemAddress remote, bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0)
    {
        ValidateSocketIndex(socketIndex);
        ThrowIfStopped();
        if (!remote.IsAssigned || remote.AddressFamily != _sockets[socketIndex].AddressFamily)
            return false;
        Enqueue(new PeerCommand { Kind = CommandKind.PingUnconnected, Target = remote, Flag = onlyReplyOnAcceptingConnections, Value = socketIndex });
        return true;
    }

    /// <inheritdoc cref="PingUnconnected"/>
    public async ValueTask<bool> PingUnconnectedAsync(string host, int port, bool onlyReplyOnAcceptingConnections = false,
                                                      int socketIndex = 0, CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, port, cancellationToken).ConfigureAwait(false);
        return address is { } remote && PingUnconnected(remote, onlyReplyOnAcceptingConnections, socketIndex);
    }

    /// <summary>An unconnected ping whose pong completes the task; the pong still appears on the stream.</summary>
    /// <exception cref="TimeoutException">No pong arrived within <paramref name="timeout"/> on the peer's clock.</exception>
    public async ValueTask<PongResult> PingAsync(SystemAddress remote, TimeSpan timeout,
                                                 bool onlyReplyOnAcceptingConnections = false, int socketIndex = 0,
                                                 CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var pending = new PendingPing(remote);
        lock (_pingsGate)
            _pings.Add(pending);
        pending.Timer = _time.CreateTimer(static state =>
        {
            var (peer, ping) = ((RakPeer, PendingPing))state!;
            peer.EndPing(ping, static p => p.Completion.TrySetException(new TimeoutException($"No pong from {p.Address}.")));
        }, (this, pending), timeout, Timeout.InfiniteTimeSpan);
        if (cancellationToken.CanBeCanceled)
        {
            pending.Registration = cancellationToken.UnsafeRegister(static (state, token) =>
            {
                var (peer, ping) = ((RakPeer, PendingPing))state!;
                peer.EndPing(ping, p => p.Completion.TrySetCanceled(token));
            }, (this, pending));
        }

        bool sent;
        try
        {
            sent = PingUnconnected(remote, onlyReplyOnAcceptingConnections, socketIndex);
        }
        catch
        {
            // Shut down, or a bad socket index: the registration must not outlive the call.
            EndPing(pending, static p => p.Completion.TrySetCanceled());
            throw;
        }
        if (!sent)
            EndPing(pending, static p => p.Completion.TrySetException(new ArgumentException($"{p.Address} cannot be pinged from this socket.")));
        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>SetOfflinePingResponse. Copied on set; 400 bytes or more throws (RakPeer.cpp:2271).</summary>
    public ReadOnlyMemory<byte> OfflinePingResponse
    {
        get => _offlinePingResponse;
        set
        {
            ValidateOfflinePingResponse(value, nameof(value));
            ThrowIfStopped();
            var copy = value.ToArray();
            _offlinePingResponse = copy;
            Enqueue(new PeerCommand { Kind = CommandKind.SetOfflinePingResponse, State = copy });
        }
    }

    /// <summary>
    /// AdvertiseSystem: an out-of-band ID_ADVERTISE_SYSTEM followed by <paramref name="data"/> (RakPeer.cpp:2683-2689),
    /// received as an AdvertiseSystem event. Sent on the next I/O cycle; false when the address cannot be reached
    /// from the socket.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="data"/> is 399 bytes or more, which the receiver drops.</exception>
    public bool AdvertiseSystem(SystemAddress remote, ReadOnlySpan<byte> data = default, int socketIndex = 0)
    {
        if (data.Length + 1 >= RakNetConstants.MaxOfflineDataLength)
            throw new ArgumentException($"Advertised data must be under {RakNetConstants.MaxOfflineDataLength - 1} bytes (RakPeer.cpp:4708-4710).", nameof(data));
        Span<byte> payload = stackalloc byte[data.Length + 1];
        payload[0] = (byte)MessageId.AdvertiseSystem;
        data.CopyTo(payload[1..]);
        return SendOutOfBand(remote, payload, socketIndex);
    }

    /// <inheritdoc cref="AdvertiseSystem"/>
    public async ValueTask<bool> AdvertiseSystemAsync(string host, int port, ReadOnlyMemory<byte> data = default,
                                                      int socketIndex = 0, CancellationToken cancellationToken = default)
    {
        var address = await ResolveAsync(host, port, cancellationToken).ConfigureAwait(false);
        return address is { } remote && AdvertiseSystem(remote, data.Span, socketIndex);
    }

    /// <summary>SetIncomingPassword. Copied on set; more than 255 bytes throws. Applies to later connection requests.</summary>
    public ReadOnlyMemory<byte> IncomingPassword
    {
        get => _incomingPassword;
        set
        {
            if (value.Length > 255)
                throw new ArgumentException("The incoming password is at most 255 bytes.", nameof(value));
            ThrowIfStopped();
            var copy = value.ToArray();
            _incomingPassword = copy;
            Enqueue(new PeerCommand { Kind = CommandKind.SetIncomingPassword, State = copy });
        }
    }

    /// <summary>SetTimeoutTime(t, UNASSIGNED_SYSTEM_ADDRESS): the default and every live connection (RakPeer.cpp:2526-2538).</summary>
    public TimeSpan DefaultTimeout
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _defaultTimeoutMs));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ThrowIfStopped();
            var ms = (long)value.TotalMilliseconds;
            Interlocked.Exchange(ref _defaultTimeoutMs, ms);
            Enqueue(new PeerCommand { Kind = CommandKind.SetDefaultTimeout, Value = ms });
        }
    }

    /// <summary>
    /// SetUnreliableTimeout (RakPeer.cpp:2719-2725): unreliable messages still queued this long after they were sent
    /// are discarded unsent, on every connection. <see cref="TimeSpan.Zero"/> keeps them until sent.
    /// </summary>
    public TimeSpan UnreliableTimeout
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _unreliableTimeoutMs));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
            ThrowIfStopped();
            var ms = (long)value.TotalMilliseconds;
            Interlocked.Exchange(ref _unreliableTimeoutMs, ms);
            Enqueue(new PeerCommand { Kind = CommandKind.SetUnreliableTimeout, Value = ms });
        }
    }

    /// <summary>SetOccasionalPing: keep pinging every connection every 5 s rather than only until its first pong (RakPeer.cpp:5924).</summary>
    public bool OccasionalPing
    {
        get => _occasionalPing;
        set
        {
            ThrowIfStopped();
            _occasionalPing = value;
            Enqueue(new PeerCommand { Kind = CommandKind.SetOccasionalPing, Flag = value });
        }
    }

    /// <summary>SetSplitMessageProgressInterval; progress arrives as DownloadProgress events every this many parts, 0 never.</summary>
    public int SplitMessageProgressInterval
    {
        get => _splitMessageProgressInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ThrowIfStopped();
            _splitMessageProgressInterval = value;
            Enqueue(new PeerCommand { Kind = CommandKind.SetSplitMessageProgressInterval, Value = value });
        }
    }

    /// <summary>
    /// SetLimitIPConnectionFrequency: refuse a new connection from an address other than this host's own that took a
    /// slot less than 100 ms ago, with ID_IP_RECENTLY_CONNECTED (RakPeer.cpp:3604-3625).
    /// </summary>
    public bool LimitIpConnectionFrequency
    {
        get => _limitIpConnectionFrequency;
        set
        {
            ThrowIfStopped();
            _limitIpConnectionFrequency = value;
            Enqueue(new PeerCommand { Kind = CommandKind.SetLimitIpConnectionFrequency, Flag = value });
        }
    }

    /// <summary>AllowConnectionResponseIPMigration: accept ID_CONNECTION_REQUEST_ACCEPTED from a system not waiting for one (RakPeer.cpp:6133-6137).</summary>
    public bool AllowConnectionResponseIpMigration
    {
        get => _allowConnectionResponseIpMigration;
        set
        {
            ThrowIfStopped();
            _allowConnectionResponseIpMigration = value;
            Enqueue(new PeerCommand { Kind = CommandKind.SetAllowConnectionResponseIpMigration, Flag = value });
        }
    }

    /// <summary>
    /// SetPerConnectionOutgoingBandwidthLimit, in bits per second; 0 is unlimited. New data on a connection waits once it
    /// sent more than this in the last second; resends and acknowledgements are not limited (ReliabilityLayer.cpp:2002-2006).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Negative, or more than RakNet's unsigned 32-bit limit.</exception>
    public long PerConnectionOutgoingBandwidthLimit
    {
        get => Interlocked.Read(ref _outgoingBandwidthLimit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, uint.MaxValue);
            ThrowIfStopped();
            Interlocked.Exchange(ref _outgoingBandwidthLimit, value);
            Enqueue(new PeerCommand { Kind = CommandKind.SetOutgoingBandwidthLimit, Value = value });
        }
    }

    /// <summary>The ban list (AddToBanList, RemoveFromBanList, ClearBanList, IsBanned).</summary>
    public BanList BanList => _banList;

    /// <summary>The security exception list; applies to incoming connections only.</summary>
    public AddressPatternCollection SecurityExceptions => throw new NotImplementedException();

    /// <summary>Whether <see cref="RakPeerOptions.Security"/> was set at start.</summary>
    public bool IsSecurityEnabled => _options.Security is not null;

    /// <summary>GetStatisticsList: copies up to <paramref name="destination"/>'s length and returns the count copied.</summary>
    public int CopyStatistics(Span<ConnectionStatistics> destination)
    {
        var all = _table.All;
        var count = Math.Min(all.Length, destination.Length);
        var now = NowUs;
        for (var i = 0; i < count; i++)
            destination[i] = new ConnectionStatistics(all[i], all[i].Counters.Snapshot(now));
        return count;
    }

    public PluginCollection Plugins => _plugins;

    /// <summary>Out-of-band sends, TTL, address migration, datagram filtering, the network simulator and observers.</summary>
    public RakPeerLowLevel LowLevel => _lowLevel;

    public PoolStatistics Pools => _pool.Statistics;

    RakPeer? IPluginHost.Peer => this;

    internal long NowUs => (long)_time.GetElapsedTime(_startTimestamp).TotalMicroseconds;

    internal TimeSpan PluginUpdateInterval => _options.PluginUpdateInterval;

    internal bool IsStreamClosed => _streamClosed;

    internal int QueuedEventCount => _queue.Count;

    /// <summary>Whether a read could return something: queued entries or work the next dispatch step applies.</summary>
    internal bool HasDispatchWork => _queue.Count > 0 || !_dispatchOps.IsEmpty;

    /// <summary>ipList[0], the address RakPeer::GetLoopbackAddress returns (RakPeer.cpp:4029-4036).</summary>
    private SystemAddress LoopbackAddress => _internalAddresses[0];

    private static ConnectOptions DefaultConnectOptions { get; } = new();

    internal void ThrowIfStopped() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _state) == Stopped, this);

    /// <summary>SendOutOfBand (RakPeer.cpp:2997-3044): the out-of-band header and the payload, on the next cycle.</summary>
    internal bool SendOutOfBand(SystemAddress remote, ReadOnlySpan<byte> payload, int socketIndex)
    {
        if (payload.Length >= RakNetConstants.MaxOfflineDataLength)
            throw new ArgumentException($"An out-of-band payload must be under {RakNetConstants.MaxOfflineDataLength} bytes (RakPeer.cpp:4708-4710).", nameof(payload));
        var datagram = ArrayPool<byte>.Shared.Rent(OutOfBandHeader.Length + payload.Length);
        var writer = new BitStreamWriter(datagram);
        new OutOfBandHeader(RakNetGuid).Write(ref writer);
        writer.WriteBytes(payload);
        return SendRaw(remote, datagram, writer.BytesWritten, ttl: 0, socketIndex);
    }

    /// <summary>SendTTL (RakPeer.cpp:2731-2753): two bytes that open a NAT mapping without reaching the host.</summary>
    internal bool SendTtl(SystemAddress remote, int ttl, int socketIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ttl);
        var datagram = ArrayPool<byte>.Shared.Rent(2);
        datagram[0] = 0;
        datagram[1] = 1;
        return SendRaw(remote, datagram, 2, ttl, socketIndex);
    }

    internal void SetNetworkSimulator(NetworkSimulatorOptions? options)
    {
        ThrowIfStopped();
        Enqueue(new PeerCommand { Kind = CommandKind.SetNetworkSimulator, State = options });
    }

    /// <summary>Ping(SystemAddress): one unreliable ID_CONNECTED_PING on the next cycle (RakPeer.cpp:2073-2076).</summary>
    internal void PingConnected(RakConnection connection)
    {
        ThrowIfStopped();
        Enqueue(new PeerCommand { Kind = CommandKind.PingConnected, Target = connection.RakNetGuid });
    }

    /// <summary>ChangeSystemAddress (RakPeer.cpp:2846-2856), applied on the next cycle.</summary>
    internal void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress address)
    {
        if (!rakNetGuid.IsAssigned)
            throw new ArgumentException("The GUID is unassigned.", nameof(rakNetGuid));
        if (!address.IsAssigned)
            throw new ArgumentException("The address is unassigned.", nameof(address));
        ThrowIfStopped();
        Enqueue(new PeerCommand { Kind = CommandKind.ChangeSystemAddress, Target = rakNetGuid, State = new SystemAddressBox(address) });
    }

    internal void SetConnectionTimeout(RakConnection connection, long timeoutMs)
    {
        ThrowIfStopped();
        Enqueue(new PeerCommand { Kind = CommandKind.SetConnectionTimeout, Target = connection.RakNetGuid, Value = timeoutMs });
    }

    internal void RequestAttach(IRakPlugin plugin)
    {
        if (InDispatchOnThisThread)
            _plugins.Apply(plugin);
        else
            QueueDispatchOp(new DispatchOp(plugin, null, null, false));
    }

    internal void RequestDetach(IRakPlugin plugin)
    {
        if (InDispatchOnThisThread)
            _plugins.Remove(plugin);
        else
            QueueDispatchOp(new DispatchOp(null, plugin, null, false));
    }

    private static void Validate(RakPeerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxConnections, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxIncomingConnections, nameof(options));
        if (options.Bindings is null || options.Bindings.Count == 0 || options.Bindings.Any(b => b is null))
            throw new ArgumentException("At least one socket binding is needed.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.TimeProvider);
        ArgumentNullException.ThrowIfNull(options.Transport);
        ArgumentNullException.ThrowIfNull(options.Random);
        ValidateOfflinePingResponse(options.OfflinePingResponse, nameof(options));
        if (options.IncomingPassword.Length > 255)
            throw new ArgumentException("The incoming password is at most 255 bytes.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.DefaultTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.PluginUpdateInterval, TimeSpan.Zero, nameof(options));

        ArgumentOutOfRangeException.ThrowIfLessThan(options.UnreliableTimeout, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.SplitMessageProgressInterval, nameof(options));
        ArgumentOutOfRangeException.ThrowIfNegative(options.PerConnectionOutgoingBandwidthLimit, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.PerConnectionOutgoingBandwidthLimit, uint.MaxValue, nameof(options));
        ArgumentNullException.ThrowIfNull(options.Limits);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.Limits.EventQueueCapacity, nameof(options));
        _ = options.Limits.ToSessionLimits();
        NetworkSimulator.Validate(options.NetworkSimulator, nameof(options));

        // Milestone 7: the secure handshake.
        if (options.Security is not null || options.SecurityExceptions.Count > 0)
            throw new NotImplementedException("Secure connections are not implemented.");
    }

    private static void ValidateOfflinePingResponse(ReadOnlyMemory<byte> response, string parameter)
    {
        if (response.Length >= RakNetConstants.MaxOfflineDataLength)
            throw new ArgumentException($"The offline ping response must be under {RakNetConstants.MaxOfflineDataLength} bytes (RakPeer.cpp:2271).", parameter);
    }

    private static IDatagramTransport[] Bind(RakPeerOptions options)
    {
        var transports = new List<IDatagramTransport>();
        for (var i = 0; i < options.Bindings.Count; i++)
        {
            var binding = options.Bindings[i];
            if (binding.Transport is { } adopted)
            {
                transports.Add(adopted);
                continue;
            }
            if (options.Transport.TryBind(binding, out var transport, out var error, out var socketError))
            {
                transports.Add(transport);
                continue;
            }
            foreach (var bound in transports)
                bound.Dispose();
            throw new RakStartupException(error, i, socketError);
        }
        return [.. transports];
    }

    // RakPeer::GenerateGUID (RakPeer.cpp:4525-4528), redrawn until neither 0 nor unassigned (RakPeer.cpp:382).
    private static RakNetGuid DrawGuid(RandomSource random)
    {
        while (true)
        {
            var guid = new RakNetGuid(random.NextUInt64());
            if (guid.IsAssigned && guid.Value != 0)
                return guid;
        }
    }

    // RakPeer gets ipList from gethostbyname of the host name (RakNetSocket2_Windows_Linux.cpp:70-103); a host that does
    // not resolve has no internal addresses.
    private static IPAddress[] HostAddresses()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName());
        }
        catch (SocketException)
        {
            return [];
        }
    }

    internal static async ValueTask<SystemAddress?> ResolveAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        if (IPAddress.TryParse(host, out var literal))
            return new SystemAddress(literal, port);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            var ipv4 = Array.Find(addresses, a => a.AddressFamily == AddressFamily.InterNetwork);
            return ipv4 is null ? null : (SystemAddress?)new SystemAddress(ipv4, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static void ValidateSend(int bitLength, Reliability reliability, Priority priority, byte orderingChannel, byte[]? rented = null)
    {
        try
        {
            if (bitLength <= 0)
                throw new ArgumentException("A message holds at least one bit; RakNet asserts on an empty send.", nameof(bitLength));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)reliability, ReliabilityExtensions.NumberOfReliabilities, nameof(reliability));
            ValidateChannelAndPriority(orderingChannel, priority);
        }
        catch when (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
            throw;
        }
    }

    private static void ValidateChannelAndPriority(byte orderingChannel, Priority priority)
    {
        // NUMBER_OF_ORDERED_STREAMS (RakNetDefines.h) and NUMBER_OF_PRIORITIES.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(orderingChannel, 32, nameof(orderingChannel));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)priority, (int)Priority.Low, nameof(priority));
    }

    private void ValidateSocketIndex(int socketIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(socketIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(socketIndex, _sockets.Length);
    }

    private static byte[] Rent(OutgoingMessage message)
    {
        var payload = ArrayPool<byte>.Shared.Rent(Math.Max(1, message.ByteLength));
        message.CopyTo(payload);
        return payload;
    }

    private static (byte[] Payload, int BitLength) Write<TState>(int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct
    {
        var payload = ArrayPool<byte>.Shared.Rent(sizeHint);
        try
        {
            var writer = new BitStreamWriter(payload.AsSpan(0, sizeHint));
            write(ref writer, state);
            return (payload, writer.BitsWritten);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(payload);
            throw;
        }
    }

    private SendResult SendCore(byte[] payload, int bitLength, AddressOrGuid target, bool broadcast, Reliability reliability,
                                Priority priority, byte orderingChannel, SendReceipt forceReceipt, TrackedSend? tracker)
    {
        if (Volatile.Read(ref _state) == Stopped)
        {
            ArrayPool<byte>.Shared.Return(payload);
            ThrowIfStopped();
        }
        if (Volatile.Read(ref _state) != Active)
        {
            // RakPeer.cpp:1320-1321: no receipt once the peer is ending.
            ArrayPool<byte>.Shared.Return(payload);
            return new SendResult(SendStatus.NotActive, SendReceipt.None);
        }

        var receipt = forceReceipt != SendReceipt.None ? forceReceipt : ReserveReceipt();
        if (!broadcast && IsLoopbackTarget(target))
        {
            // RakPeer.cpp:1332-1346: the message, then for a receipt reliability ID_SND_RECEIPT_ACKED carrying
            // sendReceiptSerial, which by now is the next receipt rather than the one used.
            var bytes = (bitLength + 7) / 8;
            Inject(payload.AsSpan(0, bytes), LoopbackAddress, RakNetGuid, atHead: false);
            ArrayPool<byte>.Shared.Return(payload);
            if (reliability.HasAckReceipt())
            {
                if (tracker is not null)
                {
                    tracker.TryComplete(tracker.Version, new DeliveryOutcome(DeliveryStatus.Acked, receipt, SendStatus.Queued));
                }
                else
                {
                    Span<byte> acked = stackalloc byte[5];
                    acked[0] = (byte)MessageId.SndReceiptAcked;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(acked[1..], PeekNextReceipt().Value);
                    Inject(acked, LoopbackAddress, RakNetGuid, atHead: false);
                }
            }
            return new SendResult(SendStatus.Queued, receipt);
        }

        if (!broadcast && !TryGetConnection(target, out _))
        {
            ArrayPool<byte>.Shared.Return(payload);
            return new SendResult(SendStatus.NotConnected, receipt);
        }

        Enqueue(new PeerCommand
        {
            Kind = broadcast ? CommandKind.Broadcast : CommandKind.Send,
            Target = target,
            Payload = payload,
            BitLength = bitLength,
            Reliability = reliability,
            Priority = priority,
            Channel = orderingChannel,
            Receipt = receipt,
            State = tracker,
            Version = tracker?.Version ?? 0,
        });
        return new SendResult(SendStatus.Queued, receipt);
    }

    // RakPeer::IsLoopbackAddress(target, true) (RakPeer.cpp:4006-4027).
    private bool IsLoopbackTarget(AddressOrGuid target)
    {
        if (target.RakNetGuid.IsAssigned)
            return target.RakNetGuid == RakNetGuid;
        var address = target.Address;
        foreach (var local in _internalAddresses)
        {
            if (!local.IsAssigned)
                break;
            if (local == address)
                return true;
        }
        return address == ExternalAddress;
    }

    private bool TryGetConnection(AddressOrGuid target, [NotNullWhen(true)] out RakConnection? connection)
    {
        if (target.RakNetGuid.IsAssigned)
            return TryGetConnection(target.RakNetGuid, out connection);
        if (target.Address.IsAssigned)
            return TryGetConnection(target.Address, out connection);
        connection = null;
        return false;
    }

    private ConnectAttemptResult StartConnect(SystemAddress remote, ConnectOptions options, TaskCompletionSource<ConnectResult>? completion,
                                              out PendingConnect? pending)
    {
        ArgumentNullException.ThrowIfNull(options);
        pending = null;
        // RakPeer::Connect returns INVALID_PARAMETER for these (RakPeer.cpp:993-1001).
        if (!remote.IsAssigned || remote.Port == 0)
            throw new ArgumentException("The remote address and port must be assigned.", nameof(remote));
        if (options.Password.Length > 255)
            throw new ArgumentException("The password is at most 255 bytes.", nameof(options));
        ValidateSocketIndex(options.SocketIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AttemptCount, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.AttemptInterval, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Timeout, TimeSpan.Zero, nameof(options));
        if (options.Security.Mode != PublicKeyMode.InsecureConnection)
            throw new NotImplementedException("Secure connections are not implemented.");
        ThrowIfStopped();

        lock (_attemptsGate)
        {
            if (TryGetConnection(remote, out _))
                return ConnectAttemptResult.AlreadyConnectedToEndpoint;
            if (_attempts.ContainsKey(remote))
                return ConnectAttemptResult.ConnectionAttemptAlreadyInProgress;
            pending = new PendingConnect(remote, remote.ToIPEndPoint(), options, completion);
            _attempts.Add(remote, pending);
        }
        Enqueue(new PeerCommand { Kind = CommandKind.Connect, Target = remote, State = pending });
        return ConnectAttemptResult.Started;
    }

    // Queues a datagram that bypasses the core; the payload is rented and returned once sent.
    private bool SendRaw(SystemAddress remote, byte[] datagram, int length, int ttl, int socketIndex)
    {
        try
        {
            ValidateSocketIndex(socketIndex);
            ThrowIfStopped();
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(datagram);
            throw;
        }
        if (!remote.IsAssigned || remote.AddressFamily != _sockets[socketIndex].AddressFamily)
        {
            ArrayPool<byte>.Shared.Return(datagram);
            return false;
        }
        Enqueue(new PeerCommand { Kind = CommandKind.SendRaw, Target = remote, Payload = datagram, BitLength = length * 8, Ttl = ttl, Value = socketIndex });
        return true;
    }

    private void EndPing(PendingPing ping, Action<PendingPing> complete)
    {
        lock (_pingsGate)
        {
            if (!_pings.Remove(ping))
                return;
        }
        ping.Dispose();
        complete(ping);
    }

    /// <summary>
    /// Queues a command for the I/O cycle. A command that passed its state check before shutdown but arrives after
    /// shutdown drained the queue is ended here, as shutdown ends the ones it drained.
    /// </summary>
    internal void Enqueue(PeerCommand command)
    {
        _commands.Enqueue(command);
        // Stop publishes Stopped with a full fence before it drains, and the enqueue above is one too, so either Stop's
        // drain sees this command or this read sees Stopped.
        if (Volatile.Read(ref _state) == Stopped)
        {
            EndPending(_stopFault);
            return;
        }
        Wake();
    }

    private void TryEnqueue(PeerCommand command, CancellationToken token)
    {
        if (Volatile.Read(ref _state) == Stopped)
        {
            if (command.State is PendingConnect attempt)
                attempt.Cancel(token);
            return;
        }
        Enqueue(command);
    }

    private TrackedSend RentTracker()
    {
        lock (_trackedGate)
        {
            if (_trackedPool.TryPop(out var tracker))
                return tracker;
        }
        return new TrackedSend(ReturnTracker);
    }

    private void ReturnTracker(TrackedSend tracker)
    {
        lock (_trackedGate)
            _trackedPool.Push(tracker);
    }

    private sealed class SystemAddressBox(SystemAddress value)
    {
        public SystemAddress Value { get; } = value;
    }

    /// <summary>The published connection table: rebuilt by the I/O cycle when a connection is added or removed.</summary>
    private sealed class ConnectionTable(RakConnection[] all)
    {
        public static ConnectionTable Empty { get; } = new([]);

        public RakConnection[] All { get; } = all;

        /// <summary>The entries that were Connected when the table was published.</summary>
        public RakConnection[] Connected { get; } = Array.FindAll(all, c => c.State == ConnectionState.Connected);

        public Dictionary<RakNetGuid, RakConnection> ByGuid { get; } = all.ToDictionary(c => c.RakNetGuid);

        public Dictionary<SystemAddress, RakConnection> ByAddress { get; } = all.ToDictionary(c => c.Address);
    }
}
