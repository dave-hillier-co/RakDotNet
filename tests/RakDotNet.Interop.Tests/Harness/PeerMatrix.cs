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
    public const string RakDotNetUnavailable = "RakDotNet driver not implemented";

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
        PeerKind.RakDotNet => RakDotNetUnavailable,
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

    public static async Task<IPeerDriver> LaunchAsync(PeerKind kind, CancellationToken cancellationToken = default)
    {
        SkipUnlessAvailable(kind);
        return kind switch
        {
            PeerKind.Native => await NativePeerDriver.LaunchAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }
}
