namespace RakDotNet.Plugins;

/// <summary>
/// StatisticsHistoryPlugin: records each connection's statistics in <see cref="Statistics"/> on every dispatch step,
/// under the connection's GUID as the object ID.
/// </summary>
/// <remarks>
/// Ported from StatisticsHistoryPlugin (StatisticsHistory.cpp:847-947). Each <see cref="IRakPlugin.OnUpdate"/> adds, at the host's
/// time, RN_ACTUAL_BYTES_SENT, RN_USER_MESSAGE_BYTES_RESENT, RN_ACTUAL_BYTES_RECEIVED, RN_USER_MESSAGE_BYTES_PUSHED and
/// RN_USER_MESSAGE_BYTES_RECEIVED_PROCESSED (each over the last second), RN_lastPing (65535 before the first pong, as
/// GetLastPing reports), RN_bytesInResendBuffer and RN_packetlossLastSecond for every connected system that is a tracked
/// object. <see cref="Statistics"/> is updated on the dispatch step and should be read there.
/// </remarks>
public sealed class StatisticsHistoryPlugin : IRakPlugin
{
    private IPluginHost? _host;
    private ConnectionStatistics[] _buffer = [];
    private bool _addNewConnections = true;
    private bool _removeLostConnections = true;
    private int _newConnectionsObjectType;

    public StatisticsHistoryPlugin()
    {
        Statistics = new StatisticsHistory(() => _host?.GetTime() ?? 0);
    }

    /// <summary>The recorded values, on the host's clock.</summary>
    public StatisticsHistory Statistics { get; }

    /// <summary>
    /// SetTrackConnections: whether new connections become tracked objects of <paramref name="newConnectionsObjectType"/>,
    /// and whether lost ones stop being tracked. Both by default, with type 0.
    /// </summary>
    public void SetTrackConnections(bool addNewConnections, int newConnectionsObjectType, bool removeLostConnections)
    {
        _addNewConnections = addNewConnections;
        _newConnectionsObjectType = newConnectionsObjectType;
        _removeLostConnections = removeLostConnections;
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>StatisticsHistoryPlugin::Update (StatisticsHistory.cpp:862-921).</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host?.Peer is not { } peer)
            return;
        var wanted = peer.ConnectionCount;
        if (_buffer.Length < wanted)
            _buffer = new ConnectionStatistics[wanted];
        var count = peer.CopyStatistics(_buffer);
        var now = _host.GetTime();
        for (var i = 0; i < count; i++)
        {
            var (connection, statistics) = _buffer[i];
            var index = Statistics.GetObjectIndex(connection.RakNetGuid.Value);
            if (index < 0)
                continue;
            var lastSecond = statistics.ValueOverLastSecond;
            Statistics.AddValueByIndex(index, "RN_ACTUAL_BYTES_SENT", lastSecond[RnsPerSecondMetric.ActualBytesSent], now, false);
            Statistics.AddValueByIndex(index, "RN_USER_MESSAGE_BYTES_RESENT", lastSecond[RnsPerSecondMetric.UserMessageBytesResent], now, false);
            Statistics.AddValueByIndex(index, "RN_ACTUAL_BYTES_RECEIVED", lastSecond[RnsPerSecondMetric.ActualBytesReceived], now, false);
            Statistics.AddValueByIndex(index, "RN_USER_MESSAGE_BYTES_PUSHED", lastSecond[RnsPerSecondMetric.UserMessageBytesPushed], now, false);
            Statistics.AddValueByIndex(index, "RN_USER_MESSAGE_BYTES_RECEIVED_PROCESSED", lastSecond[RnsPerSecondMetric.UserMessageBytesReceivedProcessed], now, false);
            Statistics.AddValueByIndex(index, "RN_lastPing", connection.LastPing is { } ping ? (int)ping.TotalMilliseconds : ushort.MaxValue, now, false);
            Statistics.AddValueByIndex(index, "RN_bytesInResendBuffer", statistics.BytesInResendBuffer, now, false);
            Statistics.AddValueByIndex(index, "RN_packetlossLastSecond", statistics.PacketLossLastSecond, now, false);
        }
        Array.Clear(_buffer, 0, count);
    }

    /// <summary>OnNewConnection (StatisticsHistory.cpp:939-947).</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        if (_addNewConnections)
            Statistics.AddObject(new TrackedObjectData(connection.RakNetGuid.Value, _newConnectionsObjectType, null));
    }

    /// <summary>OnClosedConnection (StatisticsHistory.cpp:930-938).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        if (_removeLostConnections)
            Statistics.RemoveObject(connection.RakNetGuid.Value);
    }
}
