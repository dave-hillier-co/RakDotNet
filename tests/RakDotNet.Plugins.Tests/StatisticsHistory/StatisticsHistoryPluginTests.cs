using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.StatisticsHistory;

public sealed class StatisticsHistoryPluginTests : IDisposable
{
    private static readonly string[] RecordedKeys =
    [
        "RN_ACTUAL_BYTES_SENT",
        "RN_USER_MESSAGE_BYTES_RESENT",
        "RN_ACTUAL_BYTES_RECEIVED",
        "RN_USER_MESSAGE_BYTES_PUSHED",
        "RN_USER_MESSAGE_BYTES_RECEIVED_PROCESSED",
        "RN_lastPing",
        "RN_bytesInResendBuffer",
        "RN_packetlossLastSecond",
    ];

    private readonly PluginMesh _mesh = new();

    public void Dispose() => _mesh.Dispose();

    [Fact]
    public void A_connection_is_tracked_under_its_guid_and_its_statistics_recorded_each_read()
    {
        var plugin = new StatisticsHistoryPlugin();
        var server = _mesh.Add(maxIncoming: 1, plugin);
        var client = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(client, server);

        connection.Send(new byte[100], Reliability.ReliableOrdered);
        _mesh.Settle();

        var statistics = plugin.Statistics;
        Assert.Equal(new TrackedObjectData(client.RakNetGuid.Value, 0, null), Assert.Single(Enumerable.Range(0, statistics.ObjectCount).Select(statistics.GetObjectAtIndex)));
        var keys = new List<string>();
        statistics.GetUniqueKeyList(keys);
        Assert.Equal(RecordedKeys, keys);
        Assert.Equal(StatisticsHistoryError.Ok, statistics.GetHistoryForKey(client.RakNetGuid.Value, "RN_ACTUAL_BYTES_RECEIVED", out var received, server.GetTime()));
        Assert.True(received!.RecentHighest > 100);
        Assert.Equal(server.GetTime(), received.Values[^1].Time);
    }

    [Fact]
    public void The_last_ping_is_recorded_as_RakNet_reports_it_before_the_first_pong()
    {
        var plugin = new StatisticsHistoryPlugin();
        var server = _mesh.Add(maxIncoming: 1, plugin);
        var client = _mesh.Add(maxIncoming: 0);
        _mesh.Connect(client, server);
        var guid = client.RakNetGuid.Value;

        var first = plugin.Statistics.GetHistoryForKey(guid, "RN_lastPing", out var pings, server.GetTime());

        Assert.Equal(StatisticsHistoryError.Ok, first);
        var connection = server.Connections.Single();
        var expected = connection.LastPing is { } ping ? ping.TotalMilliseconds : ushort.MaxValue;
        Assert.Equal(expected, pings!.Values[^1].Value);
    }

    [Fact]
    public void A_lost_connection_stops_being_tracked()
    {
        var plugin = new StatisticsHistoryPlugin();
        var server = _mesh.Add(maxIncoming: 1, plugin);
        var client = _mesh.Add(maxIncoming: 0);
        var connection = _mesh.Connect(client, server);

        connection.Close();
        _mesh.RunUntil(() => _mesh.Seen(server).Any(e => e.Id == (byte)MessageId.DisconnectionNotification), "the disconnection");

        Assert.Equal(0, plugin.Statistics.ObjectCount);
    }

    [Fact]
    public void Connections_can_be_tracked_with_a_type_or_not_at_all()
    {
        var typed = new StatisticsHistoryPlugin();
        typed.SetTrackConnections(addNewConnections: true, newConnectionsObjectType: 7, removeLostConnections: false);
        var untracked = new StatisticsHistoryPlugin();
        untracked.SetTrackConnections(addNewConnections: false, 0, removeLostConnections: true);
        var server = _mesh.Add(maxIncoming: 2, typed);
        var client = _mesh.Add(maxIncoming: 0, untracked);
        var connection = _mesh.Connect(client, server);
        connection.Close();
        _mesh.RunUntil(() => _mesh.Seen(server).Any(e => e.Id == (byte)MessageId.DisconnectionNotification), "the disconnection");

        Assert.Equal(7, typed.Statistics.GetObjectAtIndex(0).ObjectType);
        Assert.Equal(0, untracked.Statistics.ObjectCount);
    }
}
