using System.Buffers;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Transport;

/// <summary>
/// ApplyNetworkSimulator on the send path: drops or delays the connected datagrams the I/O cycle hands a transport, as
/// ReliabilityLayer::SendBitStream does in RakNet's debug build (ReliabilityLayer.cpp:2257-2296). Offline datagrams pass
/// untouched, because RakNet applies it in the reliability layer only. Owned by the I/O cycle.
/// </summary>
internal sealed class NetworkSimulator
{
    private readonly PriorityQueue<Delayed, (long DueUs, long Sequence)> _delayed = new();
    private NetworkSimulatorOptions? _options;
    private Random _random = new(0);
    private long _sequence;

    public static bool IsActive(NetworkSimulatorOptions? options) =>
        options is not null && (options.PacketLoss > 0 || options.MinExtraPing > TimeSpan.Zero || options.ExtraPingVariance > TimeSpan.Zero);

    public static void Validate(NetworkSimulatorOptions? options, string parameter)
    {
        if (options is null)
            return;
        if (!(options.PacketLoss >= 0 && options.PacketLoss <= 1))
            throw new ArgumentOutOfRangeException(parameter, options.PacketLoss, "Packet loss is between 0 and 1.");
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MinExtraPing, TimeSpan.Zero, parameter);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ExtraPingVariance, TimeSpan.Zero, parameter);
    }

    public void Configure(NetworkSimulatorOptions? options)
    {
        _options = IsActive(options) ? options : null;
        if (_options is not null)
            _random = new Random(_options.Seed);
    }

    /// <summary>
    /// False when the simulator took the datagram: dropped, or copied to go out later through <see cref="TryDequeueDue"/>.
    /// </summary>
    public bool PassesNow(ReadOnlySpan<byte> datagram, int socket, SystemAddress remote, long nowUs)
    {
        var options = _options;
        // Connected datagrams set the valid flag (0x80); every offline message ID is below it.
        if (options is null || datagram.IsEmpty || (datagram[0] & 0x80) == 0)
            return true;
        if (options.PacketLoss > 0 && _random.NextDouble() < options.PacketLoss)
            return false;

        var delayMs = (long)options.MinExtraPing.TotalMilliseconds;
        var varianceMs = (long)options.ExtraPingVariance.TotalMilliseconds;
        if (varianceMs > 0)
            delayMs += _random.NextInt64(varianceMs);
        if (delayMs <= 0)
            return true;

        var copy = ArrayPool<byte>.Shared.Rent(datagram.Length);
        datagram.CopyTo(copy);
        _delayed.Enqueue(new Delayed(copy, datagram.Length, socket, remote), (nowUs + (delayMs * 1000), _sequence++));
        return false;
    }

    /// <summary>The next delayed datagram whose time has come; return its buffer with <see cref="Release"/>.</summary>
    public bool TryDequeueDue(long nowUs, out Delayed datagram)
    {
        if (_delayed.TryPeek(out datagram, out var key) && key.DueUs <= nowUs)
        {
            _delayed.Dequeue();
            return true;
        }
        datagram = default;
        return false;
    }

    public static void Release(in Delayed datagram) => ArrayPool<byte>.Shared.Return(datagram.Buffer);

    /// <summary>Drops every delayed datagram, at shutdown.</summary>
    public void Clear()
    {
        while (_delayed.TryDequeue(out var datagram, out _))
            Release(datagram);
    }

    internal readonly record struct Delayed(byte[] Buffer, int Length, int Socket, SystemAddress Remote)
    {
        public ReadOnlySpan<byte> Data => Buffer.AsSpan(0, Length);
    }
}
