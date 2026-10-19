using RakDotNet.Protocol.Wire;
using RakDotNet.Security;

namespace RakDotNet;

/// <summary>One entry of RakNet's remote system list.</summary>
/// <remarks>
/// <para>
/// Created when the entry is assigned: for an outgoing attempt when the offline handshake completes and the attempt moves
/// into the connection table, for an incoming one when the request is accepted. Before that an outgoing attempt is
/// visible only as <see cref="ConnectionState.Pending"/> and its ConnectAsync task.
/// </para>
/// <para>
/// Never reused: a later connection from the same address or GUID gets a new object, so identity is safe as a dictionary
/// key. After the connection ends it stays readable with <see cref="State"/> Disconnected, every property keeps its last
/// value, and <see cref="Send"/> returns <see cref="SendStatus.NotConnected"/>. In a cross-connection RakNet handles the
/// second request on the existing entry (RakPeer.cpp:5981-5988), so there is one object per entry.
/// </para>
/// </remarks>
public sealed class RakConnection
{
    private RakConnection() { }

    public RakPeer Peer => throw new NotImplementedException();

    public RakNetGuid RakNetGuid => throw new NotImplementedException();

    /// <summary>Changes only through <see cref="RakPeerLowLevel.ChangeSystemAddress"/>.</summary>
    public SystemAddress Address => throw new NotImplementedException();

    /// <summary>GetIndexFromSystemAddress</summary>
    public int SystemIndex => throw new NotImplementedException();

    public bool IsIncoming => throw new NotImplementedException();

    /// <summary>GetSocket(target)</summary>
    public RakSocket Socket => throw new NotImplementedException();

    public ConnectionState State => throw new NotImplementedException();

    public bool IsConnected => throw new NotImplementedException();

    /// <summary>GetMTUSize(target); accounts for authenticated-encryption overhead on a secure link.</summary>
    public int Mtu => throw new NotImplementedException();

    /// <summary>SetTimeoutTime / GetTimeoutTime(target)</summary>
    public TimeSpan Timeout
    {
        get => throw new NotImplementedException();
        set => throw new NotImplementedException();
    }

    /// <summary>GetAveragePing; null where RakNet returns -1 (no sample yet).</summary>
    public TimeSpan? AveragePing => throw new NotImplementedException();

    /// <summary>GetLastPing; null for -1.</summary>
    public TimeSpan? LastPing => throw new NotImplementedException();

    /// <summary>GetLowestPing; null for -1.</summary>
    public TimeSpan? LowestPing => throw new NotImplementedException();

    /// <summary>GetClockDifferential, signed milliseconds. Incoming ID_TIMESTAMP is already shifted by it.</summary>
    public long ClockDifferential => throw new NotImplementedException();

    /// <summary>GetExternalID(target): this peer's address as the remote sees it.</summary>
    public SystemAddress ExternalAddress => throw new NotImplementedException();

    /// <summary>GetInternalID(target, i)</summary>
    public IReadOnlyList<SystemAddress> RemoteInternalAddresses => throw new NotImplementedException();

    /// <summary>Whether the link is encrypted.</summary>
    public bool IsSecure => throw new NotImplementedException();

    /// <summary>GetClientPublicKeyFromSystemAddress: the client's identity key in two-way authentication.</summary>
    public RakPublicKey? RemoteIdentityKey => throw new NotImplementedException();

    /// <summary>Application state; never read by the library.</summary>
    public object? UserState { get; set; }

    /// <summary>Completes when <see cref="State"/> reaches Disconnected, when the cycle produces it rather than when it is read.</summary>
    public Task Closed => throw new NotImplementedException();

    /// <summary>GetStatistics(target), as a value snapshot.</summary>
    public RakNetStatistics GetStatistics() => throw new NotImplementedException();

    /// <inheritdoc cref="RakPeer.Send(OutgoingMessage, AddressOrGuid, Reliability, Priority, byte, SendReceipt)"/>
    public SendResult Send(OutgoingMessage message, Reliability reliability, Priority priority = Priority.High,
                           byte orderingChannel = 0, SendReceipt forceReceipt = default) => throw new NotImplementedException();

    /// <inheritdoc cref="RakPeer.Send{TState}(AddressOrGuid, Reliability, Priority, byte, int, TState, MessageWriter{TState})"/>
    public SendResult Send<TState>(Reliability reliability, Priority priority, byte orderingChannel,
                                   int sizeHint, TState state, MessageWriter<TState> write)
        where TState : allows ref struct => throw new NotImplementedException();

    /// <inheritdoc cref="RakPeer.SendAndTrackAsync"/>
    public ValueTask<DeliveryOutcome> SendAndTrackAsync(OutgoingMessage message, Reliability reliability,
                                                        Priority priority = Priority.High, byte orderingChannel = 0,
                                                        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>Ping(SystemAddress): a connected ping.</summary>
    public void Ping() => throw new NotImplementedException();

    /// <inheritdoc cref="RakPeer.Close"/>
    public void Close(bool notify = true, byte orderingChannel = 0, Priority notificationPriority = Priority.Low) =>
        throw new NotImplementedException();

    /// <summary><see cref="Close"/>, completing with <see cref="Closed"/>.</summary>
    public ValueTask CloseAsync(bool notify = true, byte orderingChannel = 0,
                                Priority notificationPriority = Priority.Low,
                                CancellationToken cancellationToken = default) => throw new NotImplementedException();
}
