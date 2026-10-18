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
    /// <summary>Milestone 4 items of docs/plan.md that scenarios wait for before they run against RakDotNet.</summary>
    public static class Milestone4
    {
        public const string Loss = "Loss: resends, NAKs, RTO and sliding-window congestion control";

        public const string SplitMessages = "Split messages and reassembly";
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
    /// Why a scenario cannot run yet in this combination: one needing <paramref name="milestone4Item"/> skips whenever
    /// a RakDotNet peer takes part. Null when the combination can run.
    /// </summary>
    public static string? PendingReason(PeerKind client, PeerKind server, string milestone4Item) =>
        client == PeerKind.RakDotNet || server == PeerKind.RakDotNet
            ? $"RakDotNet waits for milestone 4: {milestone4Item}"
            : null;

    /// <summary>Skips the current test while <see cref="PendingReason"/> gives a reason.</summary>
    public static void SkipUntilImplemented(PeerKind client, PeerKind server, string milestone4Item)
    {
        if (PendingReason(client, server, milestone4Item) is { } reason)
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
