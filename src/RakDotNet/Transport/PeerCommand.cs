using System.Net;
using System.Threading.Tasks.Sources;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Transport;

/// <summary>What a command asks the I/O cycle to do.</summary>
internal enum CommandKind : byte
{
    Connect,
    CancelConnect,
    AbortConnect,
    Close,
    Send,
    Broadcast,
    PingUnconnected,
    SetOfflinePingResponse,
    SetMaxIncoming,
    NotifyShutdown,
    SetConnectionTimeout,
    SendRaw,
    SetInternalAddress,
    SetNetworkSimulator,
    SetIncomingPassword,
    SetDefaultTimeout,
    SetUnreliableTimeout,
    SetOccasionalPing,
    SetSplitMessageProgressInterval,
    SetLimitIpConnectionFrequency,
    SetAllowConnectionResponseIpMigration,
    SetOutgoingBandwidthLimit,
    PingConnected,
    ChangeSystemAddress,
}

/// <summary>
/// One command from any thread to the I/O cycle. A struct on a concurrent queue, so enqueuing a send allocates
/// nothing once the queue has grown; send payloads are rented from the shared array pool and returned once applied.
/// </summary>
internal struct PeerCommand
{
    public CommandKind Kind;
    public AddressOrGuid Target;
    public byte[]? Payload;
    public int BitLength;
    public Reliability Reliability;
    public Priority Priority;
    public byte Channel;
    public SendReceipt Receipt;
    public bool Flag;
    public long Value;
    public object? State;
    public short Version;
    public int Ttl;

    public readonly int ByteLength => (BitLength + 7) / 8;
}

/// <summary>A Connect call between its synchronous answer and its outcome.</summary>
internal sealed class PendingConnect(SystemAddress address, IPEndPoint endPoint, ConnectOptions options, TaskCompletionSource<ConnectResult>? completion)
{
    public SystemAddress Address { get; } = address;

    public IPEndPoint EndPoint { get; } = endPoint;

    public ConnectOptions Options { get; } = options;

    public byte[] Password { get; } = options.Password.ToArray();

    public TaskCompletionSource<ConnectResult>? Completion { get; } = completion;

    public CancellationTokenRegistration Registration { get; set; }

    /// <summary>The ConnectAsync token, reported when cancelling it ends the attempt.</summary>
    public CancellationToken Token { get; set; }

    /// <summary>Pending while the core holds it as an attempt, Connecting once it has a slot.</summary>
    public volatile ConnectionState State = ConnectionState.Pending;

    /// <summary>Set by the I/O cycle once the core accepted the attempt.</summary>
    public bool Applied { get; set; }

    public void Complete(ConnectResult result)
    {
        Registration.Unregister();
        Completion?.TrySetResult(result);
    }

    public void Cancel(CancellationToken token)
    {
        Registration.Unregister();
        Completion?.TrySetCanceled(token);
    }

    /// <summary>Shutdown ends the attempt with <see cref="OperationCanceledException"/>, a transport failure with its cause.</summary>
    public void Fail(Exception exception)
    {
        Registration.Unregister();
        if (exception is OperationCanceledException)
            Completion?.TrySetCanceled();
        else
            Completion?.TrySetException(exception);
    }
}

/// <summary>A PingAsync call waiting for its pong.</summary>
internal sealed class PendingPing(SystemAddress address)
{
    public SystemAddress Address { get; } = address;

    public TaskCompletionSource<PongResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ITimer? Timer { get; set; }

    public CancellationTokenRegistration Registration { get; set; }

    public void Dispose()
    {
        Timer?.Dispose();
        Registration.Unregister();
    }
}

/// <summary>A receipt the I/O cycle is waiting on, keyed by the receipt the core assigned to the send.</summary>
internal readonly record struct ReceiptRecord(SendReceipt Receipt, RakConnection Connection, TrackedSend? Tracker, short Version);

/// <summary>
/// The pooled value-task source behind SendAndTrackAsync. A version guards against completing a later use of the
/// same source after it went back to the pool.
/// </summary>
/// <remarks>
/// The claim to complete a use is one compare-exchange on <see cref="_claim"/>, which holds the version of the current
/// use and a claimed bit. A late completion of an earlier use therefore cannot pass the version check and then claim a
/// later use that <see cref="GetResult"/> has reset in between.
/// </remarks>
internal sealed class TrackedSend(Action<TrackedSend> returnToPool) : IValueTaskSource<DeliveryOutcome>
{
    private const int Claimed = 1 << 16;

    private ManualResetValueTaskSourceCore<DeliveryOutcome> _source = new() { RunContinuationsAsynchronously = true };
    private int _claim;

    public CancellationTokenRegistration Registration { get; set; }

    public short Version => _source.Version;

    public ValueTask<DeliveryOutcome> Task => new(this, _source.Version);

    public bool TryComplete(short version, DeliveryOutcome outcome)
    {
        if (!TryClaim(version))
            return false;
        Registration.Unregister();
        _source.SetResult(outcome);
        return true;
    }

    public bool TryFail(short version, Exception exception)
    {
        if (!TryClaim(version))
            return false;
        Registration.Unregister();
        _source.SetException(exception);
        return true;
    }

    public DeliveryOutcome GetResult(short token)
    {
        try
        {
            return _source.GetResult(token);
        }
        finally
        {
            _source.Reset();
            Registration = default;
            Volatile.Write(ref _claim, (ushort)_source.Version);
            returnToPool(this);
        }
    }

    private bool TryClaim(short version)
    {
        var unclaimed = (int)(ushort)version;
        return Interlocked.CompareExchange(ref _claim, unclaimed | Claimed, unclaimed) == unclaimed;
    }

    public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

    public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _source.OnCompleted(continuation, state, token, flags);
}

/// <summary>Something the dispatch step applies: plugin attach and detach, and injections made outside a hook.</summary>
internal sealed record DispatchOp(IRakPlugin? Attach, IRakPlugin? Detach, EventSegment? Inject, bool AtHead);
