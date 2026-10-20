using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// PRO: how one channel of a serialization, or a construction, is sent. Channels with equal parameters share a message.
/// </summary>
/// <param name="Priority">Passed to the send; by default <see cref="ReplicaManager3.DefaultPriority"/>.</param>
/// <param name="Reliability">Passed to the send; by default <see cref="ReplicaManager3.DefaultReliability"/>.</param>
/// <param name="OrderingChannel">Passed to the send; by default <see cref="ReplicaManager3.DefaultOrderingChannel"/>.</param>
/// <param name="SendReceipt">The receipt number to send with; 0 lets the peer assign one.</param>
public record struct ReplicaSendParameters(Priority Priority, Reliability Reliability, byte OrderingChannel, uint SendReceipt);

/// <summary>SerializeParameters: what <see cref="Replica3.Serialize"/> writes to and is told.</summary>
/// <remarks>
/// One instance is reused for every replica and system of a serialization tick, as RakNet reuses its one struct, so what a
/// replica sets in <see cref="Pro"/> or <see cref="MessageTimestamp"/> stays for the next replica unless it sets them
/// again.
/// </remarks>
public sealed class SerializeParameters : IDisposable
{
    /// <summary>RM3_NUM_OUTPUT_BITSTREAM_CHANNELS</summary>
    public const int ChannelCount = 16;

    private readonly BitStream[] _output = new BitStream[ChannelCount];
    private readonly ReplicaSendParameters[] _pro = new ReplicaSendParameters[ChannelCount];
    private readonly BitStream?[] _lastSent = new BitStream?[ChannelCount];

    internal SerializeParameters()
    {
        for (var i = 0; i < ChannelCount; i++)
            _output[i] = new BitStream(0);
    }

    /// <summary>
    /// outputBitstream: write the replica's state to any of the <see cref="ChannelCount"/> channels. Channels are compared
    /// and sent independently; nothing written to any of them means nothing is sent.
    /// </summary>
    public IReadOnlyList<BitStream> OutputBitstream => _output;

    /// <summary>pro: how each channel is sent, starting from the manager's defaults.</summary>
    public Span<ReplicaSendParameters> Pro => _pro;

    /// <summary>messageTimestamp: non-zero sends the serialization after ID_TIMESTAMP with this time (RakNet::GetTime).</summary>
    public ulong MessageTimestamp { get; set; }

    /// <summary>destinationConnection: the system being serialized to.</summary>
    public ReplicaConnection? DestinationConnection { get; internal set; }

    /// <summary>bitsWrittenSoFar: the bits sent to this system earlier this tick, for limiting a tick's updates.</summary>
    public long BitsWrittenSoFar { get; set; }

    /// <summary>
    /// whenLastSerialized: when this replica was last sent to this system. 0 for the serialization that follows a
    /// construction; for one in an update tick, the time the replica became known to the system when it has never been
    /// sent, as RakNet's LastSerializationResult starts with the time it was made.
    /// </summary>
    public ulong WhenLastSerialized { get; internal set; }

    /// <summary>curTime: the time now, in RakNet::GetTime milliseconds.</summary>
    public ulong CurrentTime { get; internal set; }

    /// <summary>lastSentBitstream: a reader over what was last sent on <paramref name="channel"/>; read it, do not change it.</summary>
    public BitStreamReader GetLastSentBitstream(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, ChannelCount);
        return _lastSent[channel] is { } stream ? stream.CreateReader() : default;
    }

    public void Dispose()
    {
        foreach (var stream in _output)
            stream.Dispose();
    }

    internal void SetLastSent(int channel, BitStream? stream) => _lastSent[channel] = stream;

    internal void SetAllPro(ReplicaSendParameters pro) => Array.Fill(_pro, pro);
}

/// <summary>DeserializeParameters: what <see cref="Replica3.Deserialize"/> reads.</summary>
public sealed class DeserializeParameters : IDisposable
{
    private readonly BitStream[] _streams = new BitStream[SerializeParameters.ChannelCount];
    private readonly bool[] _writtenTo = new bool[SerializeParameters.ChannelCount];

    internal DeserializeParameters()
    {
        for (var i = 0; i < _streams.Length; i++)
            _streams[i] = new BitStream(0);
    }

    /// <summary>bitstreamWrittenTo: whether the sender wrote to each channel in this message.</summary>
    public IReadOnlyList<bool> BitstreamWrittenTo => _writtenTo;

    /// <summary>timeStamp: the sender's MessageTimestamp, shifted to this system's clock; 0 when it sent none.</summary>
    public ulong Timestamp { get; internal set; }

    /// <summary>sourceConnection: the system that sent the serialization.</summary>
    public ReplicaConnection? SourceConnection { get; internal set; }

    /// <summary>serializationBitstream: a reader over what the sender wrote to <paramref name="channel"/>, empty when nothing.</summary>
    public BitStreamReader GetSerializationBitstream(int channel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, SerializeParameters.ChannelCount);
        return _streams[channel].CreateReader();
    }

    public void Dispose()
    {
        foreach (var stream in _streams)
            stream.Dispose();
    }

    internal BitStream Stream(int channel) => _streams[channel];

    internal void SetWrittenTo(int channel, bool value) => _writtenTo[channel] = value;

    internal void Reset()
    {
        foreach (var stream in _streams)
            stream.Reset();
        Array.Clear(_writtenTo);
        Timestamp = 0;
        SourceConnection = null;
    }
}

// LastSerializationResultBS: what was last sent on each channel, and which channels were sent.
internal sealed class SerializationChannels : IDisposable
{
    public BitStream[] Streams { get; } = CreateStreams();

    public bool[] IndicesToSend { get; } = new bool[SerializeParameters.ChannelCount];

    public void ResetStreams()
    {
        foreach (var stream in Streams)
            stream.Reset();
    }

    public void Dispose()
    {
        foreach (var stream in Streams)
            stream.Dispose();
    }

    private static BitStream[] CreateStreams()
    {
        var streams = new BitStream[SerializeParameters.ChannelCount];
        for (var i = 0; i < streams.Length; i++)
            streams[i] = new BitStream(0);
        return streams;
    }
}

// LastSerializationResult: a replica as one connection knows it, shared by that connection's lists.
internal sealed class LastSerializationResult(Replica3 replica, ulong whenLastSerialized) : IDisposable
{
    public Replica3 Replica { get; } = replica;

    // RakNet's constructor takes RakNet::GetTime() (ReplicaManager3.cpp:57-62).
    public ulong WhenLastSerialized { get; set; } = whenLastSerialized;

    public SerializationChannels? Channels { get; private set; }

    public SerializationChannels AllocChannels() => Channels ??= new SerializationChannels();

    public void Dispose() => Channels?.Dispose();
}
