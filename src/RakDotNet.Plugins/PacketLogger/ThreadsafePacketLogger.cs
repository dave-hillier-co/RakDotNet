using System.Collections.Concurrent;

namespace RakDotNet.Plugins;

/// <summary>
/// ThreadsafePacketLogger: a <see cref="PacketLogger"/> that queues every line and writes the queue on the dispatch
/// step, in <see cref="OnUpdate"/>, so <see cref="PacketLogger.WriteLog"/> only ever runs on the thread reading events.
/// </summary>
/// <remarks>Ported from ThreadsafePacketLogger.cpp; the SingleProducerConsumer queue is a <see cref="ConcurrentQueue{T}"/>.</remarks>
public class ThreadsafePacketLogger : PacketLogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>A logger writing to <see cref="Console.Out"/>.</summary>
    public ThreadsafePacketLogger()
    {
    }

    /// <summary>A logger writing to <paramref name="writer"/> on the dispatch step.</summary>
    public ThreadsafePacketLogger(TextWriter writer)
        : base(writer)
    {
    }

    /// <summary>ThreadsafePacketLogger::Update: writes every queued line.</summary>
    public override void OnUpdate()
    {
        while (_lines.TryDequeue(out var line))
            WriteLog(line);
    }

    /// <summary>Queues the line for the next <see cref="OnUpdate"/>.</summary>
    protected override void AddToLog(string line) => _lines.Enqueue(line);
}
