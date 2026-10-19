namespace RakDotNet.Interop.Tests.Harness;

public sealed class PeerMatrixTests
{
    [Fact]
    public void The_matrix_covers_every_client_and_server_combination()
    {
        var combinations = PeerMatrix.ClientServer.Select(row => row.Data).ToList();

        Assert.Equal(
            [
                (PeerKind.Native, PeerKind.Native),
                (PeerKind.Native, PeerKind.RakDotNet),
                (PeerKind.RakDotNet, PeerKind.Native),
                (PeerKind.RakDotNet, PeerKind.RakDotNet),
            ],
            combinations);
    }

    [Fact]
    public void RakDotNet_peers_are_always_available()
    {
        Assert.Null(PeerMatrix.UnavailableReason(PeerKind.RakDotNet));
    }

    private static readonly PendingFeature SecureHandshake = new(7, "Secure handshake");

    [Fact]
    public void A_scenario_waiting_for_a_milestone_is_pending_in_every_combination_with_a_RakDotNet_peer_and_names_the_item()
    {
        var reasons = PeerMatrix.ClientServer
            .Select(row => row.Data)
            .ToDictionary(c => c, c => PeerMatrix.PendingReason(c.Item1, c.Item2, SecureHandshake));

        Assert.Null(reasons[(PeerKind.Native, PeerKind.Native)]);
        Assert.All(
            reasons.Where(r => r.Key != (PeerKind.Native, PeerKind.Native)),
            r => Assert.Equal("RakDotNet waits for milestone 7: Secure handshake", r.Value));
    }

    [Fact]
    public void A_feature_needed_by_one_role_is_pending_only_when_that_role_is_a_RakDotNet_peer()
    {
        Assert.Null(PeerMatrix.PendingReason(PeerKind.Native, SecureHandshake));
        Assert.Equal("RakDotNet waits for milestone 7: Secure handshake", PeerMatrix.PendingReason(PeerKind.RakDotNet, SecureHandshake));
    }

    [Fact]
    public void A_missing_native_binary_is_reported_with_where_it_was_looked_for()
    {
        var reason = PeerMatrix.UnavailableReason(PeerKind.Native);

        if (NativePeerDriver.FindBinary() is null)
        {
            Assert.NotNull(reason);
            Assert.Contains(NativePeerDriver.ResolveBinaryPath(), reason, StringComparison.Ordinal);
            Assert.Contains(NativePeerDriver.BinaryEnvironmentVariable, reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(reason);
        }
    }
}
