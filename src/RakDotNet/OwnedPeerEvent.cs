namespace RakDotNet;

/// <summary>An event the caller owns, from <see cref="PeerEvent.Detach"/> or <see cref="PeerEventReader.ReadAllAsync"/>.</summary>
/// <remarks>
/// The handle is a pooled segment and a generation. Release is an <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// on the generation, so disposing a copy, disposing twice, or disposing after <see cref="RakPeer.Inject(OwnedPeerEvent, bool)"/>
/// consumed it is a no-op. One that is never disposed is collected and counted in <see cref="PoolStatistics.Misses"/>.
/// </remarks>
public struct OwnedPeerEvent : IDisposable
{
    private readonly EventSegment? _segment;
    private readonly int _generation;

    internal OwnedPeerEvent(EventSegment segment, int generation)
    {
        _segment = segment;
        _generation = generation;
    }

    public readonly bool IsValid => _segment is not null && Volatile.Read(ref _segment.Generation) == _generation;

    /// <summary>A view, valid while owned; throws <see cref="ObjectDisposedException"/> after.</summary>
    public readonly PeerEvent Event => new(Segment, _generation);

    /// <summary>The same bytes as <see cref="PeerEvent.Data"/>, usable across <c>await</c>.</summary>
    public readonly ReadOnlyMemory<byte> Data => Segment.Memory;

    internal readonly EventSegment? Handle => _segment;

    internal readonly int HandleGeneration => _generation;

    public readonly void Dispose() => _segment?.TryRelease(_generation);

    private readonly EventSegment Segment =>
        IsValid ? _segment! : throw new ObjectDisposedException(nameof(OwnedPeerEvent), "The owned event was disposed or consumed.");
}
