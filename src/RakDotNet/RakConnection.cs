using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;
using RakDotNet.Security;
using RakDotNet.Transport;

namespace RakDotNet;

/// <summary>One entry of RakNet's remote system list.</summary>
/// <remarks>
/// <para>
/// Created when the I/O cycle reports the connection: for an outgoing attempt with ID_CONNECTION_REQUEST_ACCEPTED, for an
/// incoming one with ID_NEW_INCOMING_CONNECTION. Before that an outgoing attempt is visible only as
/// <see cref="ConnectionState.Pending"/> or <see cref="ConnectionState.Connecting"/> and its ConnectAsync task.
/// </para>
/// <para>
/// Never reused: a later connection from the same address or GUID gets a new object, so identity is safe as a dictionary
/// key. After the connection ends it stays readable with <see cref="State"/> Disconnected, every property keeps its last
/// value, and <see cref="Send"/> returns <see cref="SendStatus.NotConnected"/>. In a cross-connection RakNet handles the
/// second request on the existing entry (RakPeer.cpp:5981-5988), so there is one object per entry.
/// </para>
/// <para>Properties are snapshots the I/O cycle publishes and may be one cycle old.</para>
/// </remarks>
public sealed class RakConnection
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile int _state = (int)ConnectionState.Connected;
    private volatile int _systemIndex;
    private volatile int _mtu;
    private long _timeoutMs;
    private volatile int _lastPing = ushort.MaxValue;
    private volatile int _lowestPing = ushort.MaxValue;
    private long _clockDifferential;
    private volatile IReadOnlyList<SystemAddress> _remoteInternalAddresses = [];
    private IReadOnlyList<System.Net.IPEndPoint>? _internalAddressesSource;

    internal RakConnection(RakPeer peer, RakNetGuid rakNetGuid, SystemAddress address, bool isIncoming, RakSocket socket,
                           SystemAddress externalAddress, long nowUs)
    {
        Peer = peer;
        RakNetGuid = rakNetGuid;
        Address = address;
        IsIncoming = isIncoming;
        Socket = socket;
        ExternalAddress = externalAddress;
        Counters = new ConnectionCounters(nowUs);
    }

    public RakPeer Peer { get; }

    public RakNetGuid RakNetGuid { get; }

    /// <summary>Changes only through <see cref="RakPeerLowLevel.ChangeSystemAddress"/>.</summary>
    public SystemAddress Address { get; }

    /// <summary>GetIndexFromSystemAddress</summary>
    public int SystemIndex => _systemIndex;

    public bool IsIncoming { get; }

    /// <summary>GetSocket(target)</summary>
    public RakSocket Socket { get; }

    public ConnectionState State => (ConnectionState)_state;

    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>GetMTUSize(target); accounts for authenticated-encryption overhead on a secure link.</summary>
    public int Mtu => _mtu;

    /// <summary>SetTimeoutTime / GetTimeoutTime(target)</summary>
    public TimeSpan Timeout
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _timeoutMs));
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            Peer.SetConnectionTimeout(this, (long)value.TotalMilliseconds);
            Interlocked.Exchange(ref _timeoutMs, (long)value.TotalMilliseconds);
        }
    }

    /// <summary>GetAveragePing; null where RakNet returns -1 (no sample yet).</summary>
    /// <remarks>Needs the ping history of RakNet's pingAndClockDifferential, which the peer core does not keep.</remarks>
    public TimeSpan? AveragePing => throw new NotImplementedException();

    /// <summary>GetLastPing; null for -1.</summary>
    public TimeSpan? LastPing => PingOrNull(_lastPing);

    /// <summary>GetLowestPing; null for -1.</summary>
    public TimeSpan? LowestPing => PingOrNull(_lowestPing);

    /// <summary>GetClockDifferential, signed milliseconds. Incoming ID_TIMESTAMP is already shifted by it.</summary>
    public long ClockDifferential => Interlocked.Read(ref _clockDifferential);

    /// <summary>GetExternalID(target): this peer's address as the remote sees it.</summary>
    public SystemAddress ExternalAddress { get; }

    /// <summary>GetInternalID(target, i)</summary>
    public IReadOnlyList<SystemAddress> RemoteInternalAddresses => _remoteInternalAddresses;

    /// <summary>Whether the link is encrypted.</summary>
    public bool IsSecure { get; }

    /// <summary>GetClientPublicKeyFromSystemAddress: the client's identity key in two-way authentication.</summary>
    public RakPublicKey? RemoteIdentityKey { get; }

    /// <summary>Application state; never read by the library.</summary>
    public object? UserState { get; set; }

    /// <summary>Completes when <see cref="State"/> reaches Disconnected, when the cycle produces it rather than when it is read.</summary>
    public Task Closed => _closed.Task;

    internal ConnectionCounters Counters { get; }

    /// <summary>GetStatistics(target), as a value snapshot.</summary>
    public RakNetStatistics GetStatistics() => Counters.Snapshot(Peer.NowUs);

    /// <inheritdoc cref="RakPeer.Send(OutgoingMessage, AddressOrGuid, Reliability, Priority, byte, SendReceipt)"/>
    public SendResult Send(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High,
                           byte orderingChannel = 0, SendReceipt forceReceipt = default) =>
        Peer.Send(message, this, reliability, priority, orderingChannel, forceReceipt);

    /// <inheritdoc cref="RakPeer.Send{TState}(AddressOrGuid, Reliability, Priority, byte, int, TState, MessageWriter{TState})"/>
    public SendResult Send<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                   int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct =>
        Peer.Send(this, reliability, priority, orderingChannel, sizeHint, state, write);

    /// <inheritdoc cref="RakPeer.SendAndTrackAsync"/>
    public ValueTask<DeliveryOutcome> SendAndTrackAsync(OutgoingMessage message, Reliability reliability,
                                                        Priority priority = Priority.High, byte orderingChannel = 0,
                                                        CancellationToken cancellationToken = default) =>
        Peer.SendAndTrackAsync(message, this, reliability, priority, orderingChannel, cancellationToken);

    /// <summary>Ping(SystemAddress): a connected ping.</summary>
    /// <remarks>The peer core sends connected pings only on its own schedule.</remarks>
    public void Ping() => throw new NotImplementedException();

    /// <inheritdoc cref="RakPeer.Close"/>
    public void Close(bool notify = true, byte orderingChannel = 0, Priority notificationPriority = Priority.Low) =>
        Peer.Close(this, notify, orderingChannel, notificationPriority);

    /// <summary><see cref="Close"/>, completing with <see cref="Closed"/>.</summary>
    public async ValueTask CloseAsync(bool notify = true, byte orderingChannel = 0,
                                      Priority notificationPriority = Priority.Low,
                                      CancellationToken cancellationToken = default)
    {
        Close(notify, orderingChannel, notificationPriority);
        await Closed.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A notifying close was asked for: Disconnecting until the core reports the end.</summary>
    internal void MarkDisconnecting() =>
        Interlocked.CompareExchange(ref _state, (int)ConnectionState.Disconnecting, (int)ConnectionState.Connected);

    /// <summary>Copies what the core's slot holds. Called by the I/O cycle.</summary>
    internal void Refresh(RemoteSystem system, ConnectionState state, long nowUs)
    {
        if (State != ConnectionState.Disconnected)
            _state = (int)state;
        _systemIndex = system.Index;
        _mtu = system.Session.Mtu;
        Interlocked.Exchange(ref _timeoutMs, system.Session.TimeoutMs);
        _lastPing = system.LastPing;
        _lowestPing = system.LowestPing;
        Interlocked.Exchange(ref _clockDifferential, system.ClockDifferential);
        if (!ReferenceEquals(_internalAddressesSource, system.TheirInternalAddresses))
        {
            _internalAddressesSource = system.TheirInternalAddresses;
            _remoteInternalAddresses = system.TheirInternalAddresses.Select(a => (SystemAddress)a).ToArray();
        }
        Counters.Tick(nowUs, system.Session.MessagesInResendBuffer);
    }

    /// <summary>The connection ended; it stays readable with its last values.</summary>
    internal void MarkClosed()
    {
        _state = (int)ConnectionState.Disconnected;
        _closed.TrySetResult();
    }

    private static TimeSpan? PingOrNull(int ping) => ping == ushort.MaxValue ? null : TimeSpan.FromMilliseconds(ping);
}
