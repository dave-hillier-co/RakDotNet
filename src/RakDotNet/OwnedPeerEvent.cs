namespace RakDotNet;

/// <summary>An event the caller owns, from <see cref="PeerEvent.Detach"/> or <see cref="PeerEventReader.ReadAllAsync"/>.</summary>
/// <remarks>
/// The handle is a pooled segment and a generation. Release is an <see cref="Interlocked.CompareExchange(ref int, int, int)"/>
/// on the generation, so disposing a copy, disposing twice, or disposing after <see cref="RakPeer.Inject(OwnedPeerEvent, bool)"/>
/// consumed it is a no-op. One that is never disposed is collected and counted in <see cref="PoolStatistics.Misses"/>.
/// </remarks>
public struct OwnedPeerEvent : IDisposable
{
    public readonly bool IsValid => throw new NotImplementedException();

    /// <summary>A view, valid while owned; throws <see cref="ObjectDisposedException"/> after.</summary>
    public readonly PeerEvent Event => throw new NotImplementedException();

    /// <summary>The same bytes as <see cref="PeerEvent.Data"/>, usable across <c>await</c>.</summary>
    public readonly ReadOnlyMemory<byte> Data => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
