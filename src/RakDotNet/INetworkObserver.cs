using RakDotNet.Protocol.Wire;

namespace RakDotNet;

/// <summary>
/// The reliability-layer hooks of PluginInterface2 (UsesReliabilityLayer() true). Runs in the I/O cycle, so it must not
/// block; it may call the thread-safe commands. Spans are valid for the call; everything else is a value.
/// </summary>
public interface INetworkObserver
{
    /// <summary>OnDirectSocketSend</summary>
    void OnDatagramSent(ReadOnlySpan<byte> datagram, SystemAddress remote) { }

    /// <summary>OnDirectSocketReceive</summary>
    void OnDatagramReceived(ReadOnlySpan<byte> datagram, SystemAddress remote) { }

    /// <summary>OnInternalPacket</summary>
    void OnFrame(in FrameInfo frame) { }

    /// <summary>OnAck</summary>
    void OnAck(uint messageNumber, SystemAddress remote, ulong timeMs) { }

    /// <summary>
    /// OnReliabilityLayerNotification. Malformed or hostile input is reported here and dropped; it never throws.
    /// </summary>
    void OnReliabilityNotification(ReadOnlySpan<char> message, int bitLength, SystemAddress remote, bool isError) { }

    /// <summary>The SetUserUpdateThread callback: once at the end of every I/O cycle.</summary>
    void OnUpdateCycle() { }
}

/// <summary>The InternalPacket fields PacketLogger prints, for <see cref="INetworkObserver.OnFrame"/>.</summary>
public readonly ref struct FrameInfo
{
    public bool IsSend => throw new NotImplementedException();

    public uint FrameNumber => throw new NotImplementedException();

    public ulong TimeMs => throw new NotImplementedException();

    public SystemAddress Remote => throw new NotImplementedException();

    public RakConnection? Connection => throw new NotImplementedException();

    public Reliability Reliability => throw new NotImplementedException();

    public Priority Priority => throw new NotImplementedException();

    public byte OrderingChannel => throw new NotImplementedException();

    public UInt24 MessageNumber => throw new NotImplementedException();

    public UInt24 OrderingIndex => throw new NotImplementedException();

    public UInt24 SequencingIndex => throw new NotImplementedException();

    public bool IsSplit => throw new NotImplementedException();

    public ushort SplitId => throw new NotImplementedException();

    public uint SplitIndex => throw new NotImplementedException();

    public uint SplitCount => throw new NotImplementedException();

    public ulong CreationTimeUs => throw new NotImplementedException();

    public ReadOnlySpan<byte> Data => throw new NotImplementedException();

    public int BitLength => throw new NotImplementedException();
}
