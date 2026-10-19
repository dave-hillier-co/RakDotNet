using System.Threading.Channels;

namespace RakDotNet.Connections;

/// <summary>
/// Splits a peer's one ordered stream into a queue per connection, for servers structured per connection. Built on the
/// public API only; it becomes the single reader of <see cref="RakPeer.Events"/>, so plugins run on
/// <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// Events cross threads here, so each is detached and its consumer disposes it. Order holds within a connection, not
/// between connections.
/// </remarks>
public sealed class ConnectionDemultiplexer : IAsyncDisposable
{
    public ConnectionDemultiplexer(RakPeer peer, DemultiplexerOptions? options = null) => throw new NotImplementedException();

    /// <summary>Each connection as its Connected event is read, incoming and outgoing.</summary>
    public ChannelReader<RakConnection> Connections => throw new NotImplementedException();

    /// <summary>Events without a connection: pongs, advertisements, out-of-band messages and ConnectFailed.</summary>
    public ChannelReader<OwnedPeerEvent> Offline => throw new NotImplementedException();

    /// <summary>
    /// The connection's messages and receipts, then exactly its terminal event if it has one, then completion.
    /// </summary>
    public ChannelReader<OwnedPeerEvent> EventsOf(RakConnection connection) => throw new NotImplementedException();

    /// <summary>The pump: reads the peer's stream until shutdown or cancellation.</summary>
    public Task RunAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

    public ValueTask DisposeAsync() => throw new NotImplementedException();
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
