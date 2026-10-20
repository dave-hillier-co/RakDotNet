using BenchmarkDotNet.Attributes;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Benchmarks;

/// <summary>
/// Encoding and decoding one data datagram: the <see cref="DatagramHeader"/> and a run of RELIABLE_ORDERED
/// <see cref="EncapsulatedMessage"/>s, as the reliability layer frames them. One operation is one datagram.
/// </summary>
[MemoryDiagnoser]
public class DatagramBenchmarks
{
    private const int Mtu = 1492;
    private const int MessageBytes = 32;

    private readonly byte[] _buffer = new byte[Mtu];
    private EncapsulatedMessage[] _messages = [];
    private byte[] _datagram = [];

    /// <summary>1 is a datagram per message; 16 small messages share one datagram.</summary>
    [Params(1, 16)]
    public int MessagesPerDatagram { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[MessageBytes];
        payload[0] = (byte)MessageId.UserPacketEnum;
        _messages = new EncapsulatedMessage[MessagesPerDatagram];
        for (var i = 0; i < MessagesPerDatagram; i++)
        {
            _messages[i] = new EncapsulatedMessage(
                Reliability.ReliableOrdered,
                payload,
                reliableMessageNumber: new UInt24((uint)i),
                orderingIndex: new UInt24((uint)i)
            );
        }
        _datagram = _buffer.AsSpan(0, Encode()).ToArray();
    }

    [Benchmark]
    public int Encode()
    {
        var writer = new BitStreamWriter(_buffer);
        DatagramHeader.ForData(new UInt24(7), isContinuousSend: true).Write(ref writer);
        foreach (var message in _messages)
            message.Write(ref writer);
        return writer.BytesWritten;
    }

    [Benchmark]
    public int Decode()
    {
        if (!EncapsulatedMessageReader.TryOpenDatagram(_datagram, out _, out var messages))
            throw new InvalidOperationException("The datagram did not decode.");
        var bits = 0;
        while (messages.TryReadNext(out var message))
            bits += message.PayloadBitLength;
        return bits;
    }
}
