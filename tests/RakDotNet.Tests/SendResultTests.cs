namespace RakDotNet.Tests;

public class SendResultTests
{
    [Fact]
    public void None_is_receipt_zero_and_the_default()
    {
        // RakPeer.cpp:1326-1330 - a forceReceipt of 0 means "assign one"
        Assert.Equal(0u, SendReceipt.None.Value);
        Assert.Equal(default, SendReceipt.None);
    }

    [Theory]
    [InlineData(SendStatus.Queued, true)]
    [InlineData(SendStatus.NotActive, false)]
    [InlineData(SendStatus.NotConnected, false)]
    public void IsQueued_is_true_only_for_Queued(SendStatus status, bool expected)
    {
        Assert.Equal(expected, new SendResult(status, new SendReceipt(5)).IsQueued);
    }
}
