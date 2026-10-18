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
/// </remarks>
public sealed class PeerEventReader
{
    private PeerEventReader() { }

    /// <summary>
    /// One dispatch step, as RakPeer::Receive (RakPeer.cpp:1528-1600): releases the previous event, runs every
    /// plugin's OnUpdate, then returns the next event no plugin consumed, or false.
    /// </summary>
    public bool TryRead(out PeerEvent e) => throw new NotImplementedException();

    /// <summary>
    /// True when an event may be available; false after shutdown. Completes synchronously when one is queued. While
    /// plugins are attached it also returns true at least every <see cref="RakPeerOptions.PluginUpdateInterval"/>, so
    /// OnUpdate keeps running for an idle async consumer, and the next <see cref="TryRead"/> may then return false.
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

    /// <summary>
    /// Yields owned events, each detached and to be disposed by the consumer, for code that awaits while holding one.
    /// Allocates one enumerator per enumeration.
    /// </summary>
    public IAsyncEnumerable<OwnedPeerEvent> ReadAllAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>GetReceiveBufferSize.</summary>
    public int Count => throw new NotImplementedException();

    /// <summary>Completes at shutdown; faults with <see cref="RakTransportException"/> on a fatal transport failure.</summary>
    public Task Completion => throw new NotImplementedException();
}
