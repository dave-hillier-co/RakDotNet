namespace RakDotNet.Tests;

public class ConnectResultTests
{
    [Fact]
    public void A_synchronously_rejected_attempt_is_not_connected()
    {
        var result = new ConnectResult(ConnectAttemptResult.AlreadyConnectedToEndpoint, failure: null, connection: null, remoteProtocolVersion: 0);

        Assert.False(result.IsConnected);
        Assert.Null(result.Failure);
        Assert.Equal(ConnectAttemptResult.AlreadyConnectedToEndpoint, result.AttemptResult);
    }

    [Fact]
    public void EnsureConnected_throws_carrying_the_result_when_the_attempt_failed()
    {
        var result = new ConnectResult(ConnectAttemptResult.Started, ConnectFailure.IncompatibleProtocolVersion, connection: null, remoteProtocolVersion: 5);

        var exception = Assert.Throws<RakConnectException>(() => result.EnsureConnected());

        Assert.Equal(ConnectFailure.IncompatibleProtocolVersion, exception.Result.Failure);
        Assert.Equal(5, exception.Result.RemoteProtocolVersion);
    }
}
