using BenchmarkDotNet.Attributes;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// <see cref="BitStreamWriter"/> and <see cref="BitStreamReader"/> on a typical game message: a message ID, then
/// fields of mixed width, written both byte-aligned and one bit off alignment (the common case after a bool).
/// One operation is one whole message.
/// </summary>
[MemoryDiagnoser]
public class BitStreamBenchmarks
{
    private const int Fields = 16;

    private readonly byte[] _buffer = new byte[256];
    private int _written;

    /// <summary>Whether a leading bool shifts every later field off byte alignment.</summary>
    [Params(false, true)]
    public bool Unaligned { get; set; }

    [GlobalSetup]
    public void Setup() => _written = Write();

    [Benchmark]
    public int Write()
    {
        var writer = new BitStreamWriter(_buffer);
        writer.WriteByte((byte)MessageId.UserPacketEnum);
        if (Unaligned)
            writer.WriteBool(true);
        for (var i = 0; i < Fields; i++)
        {
            writer.WriteUInt32((uint)i * 2_654_435_761u);
            writer.WriteUInt16((ushort)i);
            writer.WriteSingle(i * 0.5f);
            writer.WriteUInt24(new UInt24((uint)i));
        }
        return writer.BytesWritten;
    }

    [Benchmark]
    public uint Read()
    {
        var reader = new BitStreamReader(_buffer.AsSpan(0, _written));
        reader.TryReadByte(out _);
        if (Unaligned)
            reader.TryReadBool(out _);
        var sum = 0u;
        for (var i = 0; i < Fields; i++)
        {
            reader.TryReadUInt32(out var a);
            reader.TryReadUInt16(out var b);
            reader.TryReadSingle(out var c);
            reader.TryReadUInt24(out var d);
            sum += a + b + (uint)c + d.Value;
        }
        return sum;
    }
}
