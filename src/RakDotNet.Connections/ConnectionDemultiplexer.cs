using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace RakDotNet.Connections;

/// <summary>
/// Splits a peer's one ordered stream into a queue per connection, for servers structured per connection. Built on the
/// public API only; it becomes the single reader of <see cref="RakPeer.Events"/>, so plugins run on
/// <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Events cross threads here, so each is detached and its consumer disposes it. Order holds within a connection, not
/// between connections. The Connected event itself is not queued: its connection is published on
/// <see cref="Connections"/> and its queue starts empty.
/// </para>
/// <para>
/// The pump runs once. When it ends, by shutdown of the peer, cancellation or <see cref="DisposeAsync"/>, every reader
/// completes, with the peer's fault if <see cref="PeerEventReader.Completion"/> faulted. A queue is kept as long as its
/// <see cref="RakConnection"/> is referenced. A connection that ended before the pump read its events has no terminal
/// event to complete its queue, so its queue completes when the pump ends.
/// </para>
/// <para>
/// <see cref="Offline"/> holds at most <see cref="DemultiplexerOptions.PerConnectionCapacity"/> events and drops the
/// oldest, releasing it, so an application that never reads it cannot stall the pump.
/// </para>
/// </remarks>
public sealed class ConnectionDemultiplexer : IAsyncDisposable
{
    private readonly RakPeer _peer;
    private readonly DemultiplexerOptions _options;
    private readonly Channel<RakConnection> _connections = Channel.CreateUnbounded<RakConnection>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<OwnedPeerEvent> _offline;
    private readonly ConditionalWeakTable<RakConnection, Queue> _queues = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private bool _ended;
    private int _disposed;

    public ConnectionDemultiplexer(RakPeer peer, DemultiplexerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        options ??= new DemultiplexerOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.PerConnectionCapacity, nameof(options));
        _peer = peer;
        _options = options;
        _offline = Channel.CreateBounded<OwnedPeerEvent>(
            new BoundedChannelOptions(options.PerConnectionCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true },
            static dropped => dropped.Dispose());
    }

    /// <summary>Each connection as its Connected event is read, incoming and outgoing.</summary>
    public ChannelReader<RakConnection> Connections => _connections.Reader;

    /// <summary>Events without a connection: pongs, advertisements, out-of-band messages, loopback and ConnectFailed.</summary>
    public ChannelReader<OwnedPeerEvent> Offline => _offline.Reader;

    /// <summary>
    /// The connection's messages and receipts, then exactly its terminal event if it has one, then completion. Under
    /// <see cref="DemultiplexerOverflow.CloseConnection"/> a queue that overflowed completes without its terminal event.
    /// </summary>
    public ChannelReader<OwnedPeerEvent> EventsOf(RakConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return QueueOf(connection).Channel.Reader;
    }

    /// <summary>The pump: reads the peer's stream until shutdown or cancellation.</summary>
    /// <exception cref="InvalidOperationException">The pump has already been started.</exception>
    public Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_pump is not null)
                throw new InvalidOperationException("The demultiplexer's pump is already running; it runs once.");
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            _pump = PumpAsync(linked);
            return _pump;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? pump;
        lock (_gate)
            pump = _pump;
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The pump was stopped.
            }
        }
        End(fault: null);
        Release(_offline.Reader);
        foreach (var (_, queue) in _queues)
            Release(queue.Channel.Reader);
        _stop.Dispose();
    }

    private async Task PumpAsync(CancellationTokenSource linked)
    {
        Exception? fault = null;
        try
        {
            var token = linked.Token;
            while (await _peer.Events.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (TryTake(out var routed))
                    await DeliverAsync(routed, token).ConfigureAwait(false);
            }
            if (_peer.Events.Completion.IsFaulted)
                fault = _peer.Events.Completion.Exception?.InnerException;
        }
        catch (ObjectDisposedException) when (!_peer.IsActive)
        {
            // The peer shut down between the wait and the read.
        }
        finally
        {
            End(fault);
            linked.Dispose();
        }
    }

    // One read, classified and detached, so no borrowed event is held across an await.
    private bool TryTake(out Routed routed)
    {
        routed = default;
        if (!_peer.Events.TryRead(out var e))
            return false;
        var connection = e.Connection;
        if (e.Kind == PeerEventKind.Connected && connection is not null)
        {
            routed = new Routed(connection, default, IsConnected: true, IsTerminal: false);
            return true;
        }
        var owned = e.Detach();
        routed = connection is null || e.Kind == PeerEventKind.ConnectFailed
            ? new Routed(null, owned, IsConnected: false, IsTerminal: false)
            : new Routed(connection, owned, IsConnected: false, IsTerminal: e.Kind == PeerEventKind.Disconnected);
        return true;
    }

    private async ValueTask DeliverAsync(Routed routed, CancellationToken cancellationToken)
    {
        if (routed.Connection is null)
        {
            if (!_offline.Writer.TryWrite(routed.Event))
                routed.Event.Dispose();
            return;
        }

        var queue = QueueOf(routed.Connection);
        if (routed.IsConnected)
        {
            _connections.Writer.TryWrite(routed.Connection);
            return;
        }

        var writer = queue.Channel.Writer;
        if (_options.Overflow == DemultiplexerOverflow.Wait)
        {
            try
            {
                await writer.WriteAsync(routed.Event, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                routed.Event.Dispose();
                return;
            }
            catch (OperationCanceledException)
            {
                routed.Event.Dispose();
                throw;
            }
        }
        else if (!writer.TryWrite(routed.Event))
        {
            routed.Event.Dispose();
            if (writer.TryComplete() && _peer.IsActive)
                routed.Connection.Close(notify: false);
            return;
        }

        if (routed.IsTerminal)
            writer.TryComplete();
    }

    private Queue QueueOf(RakConnection connection)
    {
        lock (_gate)
        {
            if (_queues.TryGetValue(connection, out var existing))
                return existing;
            var queue = new Queue(Channel.CreateBounded<OwnedPeerEvent>(new BoundedChannelOptions(_options.PerConnectionCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
            }));
            if (_ended)
                queue.Channel.Writer.TryComplete();
            _queues.Add(connection, queue);
            return queue;
        }
    }

    private void End(Exception? fault)
    {
        lock (_gate)
        {
            _ended = true;
            _connections.Writer.TryComplete(fault);
            _offline.Writer.TryComplete(fault);
            foreach (var (_, queue) in _queues)
                queue.Channel.Writer.TryComplete(fault);
        }
    }

    private static void Release(ChannelReader<OwnedPeerEvent> reader)
    {
        while (reader.TryRead(out var owned))
            owned.Dispose();
    }

    private readonly record struct Routed(RakConnection? Connection, OwnedPeerEvent Event, bool IsConnected, bool IsTerminal);

    private sealed class Queue(Channel<OwnedPeerEvent> channel)
    {
        public Channel<OwnedPeerEvent> Channel { get; } = channel;
    }
}

/// <summary>Per-connection queue limits for <see cref="ConnectionDemultiplexer"/>.</summary>
public sealed class DemultiplexerOptions
{
    public int PerConnectionCapacity { get; init; } = 1024;

    /// <summary>
    /// <see cref="DemultiplexerOverflow.Wait"/> by default, because it never drops or closes anything the application
    /// did not ask for.
    /// </summary>
    public DemultiplexerOverflow Overflow { get; init; } = DemultiplexerOverflow.Wait;
}

/// <summary>What the pump does when a connection's queue is full.</summary>
public enum DemultiplexerOverflow : byte
{
    /// <summary>The pump waits, so one slow handler pauses every connection (head-of-line blocking).</summary>
    Wait,

    /// <summary>The full connection is closed silently and its queue completed; the others keep flowing.</summary>
    CloseConnection,
}
