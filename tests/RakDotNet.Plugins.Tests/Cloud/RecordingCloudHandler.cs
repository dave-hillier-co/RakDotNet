namespace RakDotNet.Plugins.Tests.Cloud;

/// <summary>An <see cref="ICloudClientHandler"/> that keeps what it is given.</summary>
internal sealed class RecordingCloudHandler : ICloudClientHandler
{
    public List<CloudQueryResult> Gets { get; } = [];

    public List<(CloudQueryRow Row, bool WasUpdated)> Notifications { get; } = [];

    public void OnGet(CloudQueryResult result) => Gets.Add(result);

    public void OnSubscriptionNotification(CloudQueryRow row, bool wasUpdated) => Notifications.Add((row, wasUpdated));
}
