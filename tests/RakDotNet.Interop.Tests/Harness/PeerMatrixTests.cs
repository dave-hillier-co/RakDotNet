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
    public void RakDotNet_peers_are_reported_as_not_implemented()
    {
        Assert.Equal("RakDotNet driver not implemented", PeerMatrix.UnavailableReason(PeerKind.RakDotNet));
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
