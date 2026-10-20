using BenchmarkDotNet.Attributes;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// End to end over UDP on 127.0.0.1, modelled on RakNet's Samples/LoopbackPerformanceTest: a client sends a burst of
/// RELIABLE_ORDERED messages and the operation ends when the server has read the last one. One operation is one
/// message; the Messages/s and MB/s columns are computed from the mean.
/// </summary>
/// <remarks>
/// 32 and 400 bytes (LoopbackPerformanceTest's default) fit one datagram; 16000 bytes is split into parts by the
/// sender and reassembled by the receiver.
/// </remarks>
[MemoryDiagnoser]
public class LoopbackBenchmarks : IDisposable
{
    public const int MessagesPerOperationBatch = 256;

    private LoopbackPair? _pair;
    private byte[] _message = [];

    [Params(PeerThreading.Background, PeerThreading.Manual)]
    public PeerThreading Threading { get; set; }

    [Params(32, 400, 16_000)]
    public int MessageBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _message = new byte[MessageBytes];
        _message[0] = (byte)MessageId.UserPacketEnum;
        _pair = new LoopbackPair(Threading);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _pair?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(OperationsPerInvoke = MessagesPerOperationBatch)]
    public void ReliableOrdered() => _pair!.Transfer(_message, MessagesPerOperationBatch, Reliability.ReliableOrdered);
}
