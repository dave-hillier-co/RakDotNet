namespace RakDotNet.Interop.Tests.Harness;

public enum PeerKind
{
    Native,
    RakDotNet,
}

/// <summary>
/// Every scenario is a theory over (client kind, server kind). Combinations whose drivers are unavailable skip
/// with the reason, so the matrix shape is always visible in test output.
/// </summary>
public static class PeerMatrix
{
    /// <summary>
    /// Milestone 4 items of docs/plan.md that scenarios wait for before they run against RakDotNet: what the peer core
    /// (layers 1 and 2) does not do yet.
    /// </summary>
    public static class Milestone4
    {
        public static readonly PendingFeature Loss = new(4, "Loss: resends, NAKs, RTO and sliding-window congestion control");

        public static readonly PendingFeature SplitMessages = new(4, "Split messages and reassembly");

        public static readonly PendingFeature SplitMessageProgress = new(4, "Split messages and reassembly: ID_DOWNLOAD_PROGRESS");

        public static readonly PendingFeature OutgoingBandwidthLimit = new(4, "Sliding-window congestion control: the per-connection outgoing bandwidth limit");

        public static readonly PendingFeature ConnectedPings = new(4, "Connected pings and ping statistics");

        public static readonly PendingFeature Timestamps = new(4, "ID_TIMESTAMP and clock differential");

        public static readonly PendingFeature TimeoutDetection = new(4, "Timeout detection with SetTimeoutTime");

        public static readonly PendingFeature BanList = new(4, "Ban list");

        public static readonly PendingFeature AdvertiseSystem = new(4, "AdvertiseSystem");

        public static readonly PendingFeature CancelConnectionAttempt = new(4, "Connection limits: CancelConnectionAttempt");
    }

    /// <summary>
    /// Milestone 5 items: what the core already does, or does not need, but RakDotNetPeerDriver cannot reach until it
    /// moves onto the public API.
    /// </summary>
    public static class Milestone5
    {
        public static readonly PendingFeature Statistics = new(5, "RakPeer statistics: GetStatistics");

        public static readonly PendingFeature ConnectionQueries = new(5, "RakPeer connection queries: GetSystemList, GetMTUSize, GetExternalID and pings");

        public static readonly PendingFeature SendList = new(5, "RakPeer SendList");

        public static readonly PendingFeature Time = new(5, "RakNet::GetTime for ID_TIMESTAMP senders");
    }

    public static TheoryData<PeerKind, PeerKind> ClientServer
    {
        get
        {
            var data = new TheoryData<PeerKind, PeerKind>();
            foreach (var client in Enum.GetValues<PeerKind>())
            {
                foreach (var server in Enum.GetValues<PeerKind>())
                {
                    data.Add(client, server);
                }
            }
            return data;
        }
    }

    /// <summary>Why a driver of this kind cannot run here, or null when it can.</summary>
    public static string? UnavailableReason(PeerKind kind) => kind switch
    {
        PeerKind.RakDotNet => null,
        PeerKind.Native when NativePeerDriver.FindBinary() is null =>
            $"Native interop peer not found at {NativePeerDriver.ResolveBinaryPath()}. Build tools/interop-peer or set {NativePeerDriver.BinaryEnvironmentVariable}.",
        PeerKind.Native => null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Skips the current test unless every kind is available.</summary>
    public static void SkipUnlessAvailable(params PeerKind[] kinds)
    {
        foreach (var kind in kinds.Distinct())
        {
            if (UnavailableReason(kind) is { } reason)
            {
                Assert.Skip(reason);
            }
        }
    }

    /// <summary>
    /// Why a scenario cannot run yet in this combination: one needing <paramref name="feature"/> from both peers skips
    /// whenever a RakDotNet peer takes part. Null when the combination can run.
    /// </summary>
    public static string? PendingReason(PeerKind client, PeerKind server, PendingFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return PendingReason(client, feature) ?? PendingReason(server, feature);
    }

    /// <summary>Why the peer in one role cannot provide <paramref name="feature"/> yet, or null when it can.</summary>
    public static string? PendingReason(PeerKind role, PendingFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return role == PeerKind.RakDotNet ? feature.Reason : null;
    }

    /// <summary>Skips the current test while either peer is waiting for <paramref name="feature"/>.</summary>
    public static void SkipUntilImplemented(PeerKind client, PeerKind server, PendingFeature feature)
    {
        if (PendingReason(client, server, feature) is { } reason)
        {
            Assert.Skip(reason);
        }
    }

    /// <summary>Skips the current test while the peer in one role (the only one that needs it) waits for <paramref name="feature"/>.</summary>
    public static void SkipUntilImplemented(PeerKind role, PendingFeature feature)
    {
        if (PendingReason(role, feature) is { } reason)
        {
            Assert.Skip(reason);
        }
    }

    public static async Task<IPeerDriver> LaunchAsync(PeerKind kind, CancellationToken cancellationToken = default)
    {
        SkipUnlessAvailable(kind);
        return kind switch
        {
            PeerKind.Native => await NativePeerDriver.LaunchAsync(cancellationToken).ConfigureAwait(false),
            PeerKind.RakDotNet => RakDotNetPeerDriver.Launch(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}

/// <summary>A feature a RakDotNet peer does not provide yet, and the milestone of docs/plan.md that adds it.</summary>
public sealed record PendingFeature(int Milestone, string Name)
{
    public string Reason => $"RakDotNet waits for milestone {Milestone}: {Name}";
}
