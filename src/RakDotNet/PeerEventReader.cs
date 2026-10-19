using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;

namespace RakDotNet;

/// <summary>The peer's one ordered event stream, <see cref="RakPeer.Events"/>: RakNet's Receive().</summary>
/// <remarks>
/// <para>
/// Purpose-built rather than a <c>ChannelReader</c>, because a read runs the plugin chain and releases the previous
/// borrowed event, which no <c>ChannelReader</c> member may do. There is no TryPeek: peeking would either run plugins
/// or show an event a plugin is about to consume.
/// </para>
/// <para>
/// Entries arrive in RakNet's packetReturnQueue order. Connected precedes a connection's first message; its last
/// message precedes its Disconnected, and nothing for it follows. A receipt event follows the send that produced it,
/// and ReceiptAbandoned precedes the connection's Disconnected. Reads are the dispatch step: one caller at a time,
/// enforced by an interlocked guard that throws <see cref="InvalidOperationException"/>.
/// </para>
/// <para>
/// Internal traffic never reaches the stream: connected pings and pongs, unconnected pings, handshake messages and
/// ID_DETECT_LOST_CONNECTIONS. <see cref="INetworkObserver"/> sees it.
/// </para>
/// </remarks>
public sealed class PeerEventReader
{
    private readonly RakPeer _peer;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly WaitSource _wait = new();
    private bool _waiting;
    private CancellationTokenRegistration _registration;
    private ITimer? _pluginTimer;

    internal PeerEventReader(RakPeer peer) => _peer = peer;

    /// <summary>
    /// One dispatch step, as RakPeer::Receive (RakPeer.cpp:1528-1600): releases the previous event, runs every
    /// plugin's OnUpdate, then returns the next event no plugin consumed, or false.
    /// </summary>
    public bool TryRead(out PeerEvent e) => _peer.TryReadNext(out e);

    /// <summary>
    /// True when an event may be available; false after shutdown. Completes synchronously when one is queued, otherwise
    /// through one reusable value-task source, so one wait may be outstanding at a time. While plugins are attached it
    /// also returns true at least every <see cref="RakPeerOptions.PluginUpdateInterval"/>, so OnUpdate keeps running
    /// for an idle async consumer, and the next <see cref="TryRead"/> may then return false.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another wait is outstanding.</exception>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<bool>(cancellationToken);
        lock (_gate)
        {
            if (_peer.IsStreamClosed)
                return new ValueTask<bool>(false);
            if (_peer.HasDispatchWork)
                return new ValueTask<bool>(true);
            if (_waiting)
                throw new InvalidOperationException("Only one WaitToReadAsync may be outstanding.");
            _waiting = true;
            _wait.Core.Reset();
            var version = _wait.Core.Version;
            if (cancellationToken.CanBeCanceled)
                _registration = cancellationToken.UnsafeRegister(static (state, token) => ((PeerEventReader)state!).Cancel(token), this);
            if (_peer.Plugins.Count > 0)
                _pluginTimer = _peer.TimeProvider.CreateTimer(static state => ((PeerEventReader)state!).Signal(true), this, _peer.PluginUpdateInterval, Timeout.InfiniteTimeSpan);
            return new ValueTask<bool>(_wait, version);
        }
    }

    /// <summary>
    /// Yields owned events, each detached and to be disposed by the consumer, for code that awaits while holding one.
    /// Allocates one enumerator per enumeration.
    /// </summary>
    public async IAsyncEnumerable<OwnedPeerEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (TryReadOwned(out var owned))
                yield return owned;
        }
    }

    /// <summary>GetReceiveBufferSize.</summary>
    public int Count => _peer.QueuedEventCount;

    /// <summary>
    /// Completes at shutdown, which also ends every untracked receipt still outstanding; faults with
    /// <see cref="RakTransportException"/> on a fatal transport failure.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>Wakes an outstanding wait: true when something may be read, false when the stream ended.</summary>
    internal void Signal(bool available)
    {
        lock (_gate)
        {
            if (!_waiting)
                return;
            _waiting = false;
            EndWait();
            _wait.Core.SetResult(available);
        }
    }

    internal void Complete(Exception? fault)
    {
        Signal(false);
        if (fault is null)
            _completion.TrySetResult();
        else
            _completion.TrySetException(fault);
    }

    private bool TryReadOwned(out OwnedPeerEvent owned)
    {
        if (TryRead(out var e))
        {
            owned = e.Detach();
            return true;
        }
        owned = default;
        return false;
    }

    private void Cancel(CancellationToken token)
    {
        lock (_gate)
        {
            if (!_waiting)
                return;
            _waiting = false;
            EndWait();
            _wait.Core.SetException(new OperationCanceledException(token));
        }
    }

    private void EndWait()
    {
        _registration.Unregister();
        _registration = default;
        _pluginTimer?.Dispose();
        _pluginTimer = null;
    }

    private sealed class WaitSource : IValueTaskSource<bool>
    {
        public ManualResetValueTaskSourceCore<bool> Core = new() { RunContinuationsAsynchronously = true };

        public bool GetResult(short token) => Core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => Core.GetStatus(token);

        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            Core.OnCompleted(continuation, state, token, flags);
    }
}
