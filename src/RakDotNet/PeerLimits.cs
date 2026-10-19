namespace RakDotNet;

/// <summary>
/// The hardening limits of docs/plan.md. The defaults accept everything stock RakNet sends.
/// </summary>
/// <remarks>
/// When the stream holds <see cref="EventQueueCapacity"/> entries the I/O cycle stops reading datagrams until the
/// consumer drains it below half, so the kernel queues or drops datagrams and the reliability layer recovers them.
/// Locally generated control entries use reserved headroom above the cap and are never dropped or reordered.
/// </remarks>
public sealed record PeerLimits
{
    /// <summary>Limits that accept everything stock RakNet sends.</summary>
    public static PeerLimits Default => throw new NotImplementedException();

    public int MaxSplitCount { get; init; }

    public int MaxSplitMessageBytes { get; init; }

    public int MaxConcurrentSplitsPerConnection { get; init; }

    public TimeSpan SplitReassemblyTimeout { get; init; }

    public int MaxOrderingBufferMessagesPerChannel { get; init; }

    public int EventQueueCapacity { get; init; } = 65_536;
}
