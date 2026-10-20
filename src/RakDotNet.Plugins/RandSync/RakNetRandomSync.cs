using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNetRandomSync: a <see cref="RakNetRandom"/> that two systems keep in step. One system sends its seed and call count
/// with <see cref="SerializeConstruction(BitStream)"/>; afterwards each sends its call count with
/// <see cref="Serialize(BitStream)"/>, and the receiver skips ahead, or replays the numbers it drew too many, so both
/// draw the same sequence.
/// </summary>
/// <remarks>
/// <para>
/// Ported from RandSync.cpp. The construction is the seed then the call count, the update the call count alone, each a
/// big-endian 32-bit integer. The last 64 numbers drawn are kept for replay; a system asked to replay further back draws
/// fresh numbers instead, without counting them, as RakNet does (RandSync.cpp:65-79).
/// </para>
/// <para>
/// RakNet's quirks are kept: a remote call count ahead of this one skips the difference even while numbers are being
/// replayed (RandSync.cpp:132-140), and <see cref="NextSingle"/> divides by 2^32 - 1, so it can return 1. An update too
/// short to hold a call count is ignored, where RakNet would use an uninitialised value. Not thread-safe.
/// </para>
/// <para>
/// Catching up draws every skipped number on the caller's thread, and the call count comes from the other system, so a
/// skip beyond <see cref="MaxSkip"/> is refused, changing nothing, where RakNet would draw up to 2^32 - 1 numbers.
/// </para>
/// </remarks>
public sealed class RakNetRandomSync
{
    private const int UsedValueCapacity = 64;

    private readonly RakNetRandom _generator = new();

    // DataStructures::Queue<unsigned int> usedValues: oldest first.
    private readonly List<uint> _usedValues = new(UsedValueCapacity + 1);
    private uint _seed = uint.MaxValue;
    private uint _callCount;
    private uint _usedValueBufferCount;

    /// <summary>GetSeed: the last seed; <see cref="uint.MaxValue"/> before the first.</summary>
    public uint CurrentSeed => _seed;

    /// <summary>GetCallCount and SetCallCount: how many numbers this system has drawn since seeding.</summary>
    public uint CallCount
    {
        get => _callCount;
        set => _callCount = value;
    }

    /// <summary>
    /// The most numbers <see cref="Deserialize(ref BitStreamReader)"/> or
    /// <see cref="DeserializeConstruction(ref BitStreamReader)"/> skips to catch up; 2^20 by default, a few milliseconds.
    /// A sender further ahead is refused. Raise it for sequences that draw more than that between updates, or that a late
    /// joiner constructs after.
    /// </summary>
    public uint MaxSkip { get; set; } = 1u << 20;

    /// <summary>SeedMT: restarts from <paramref name="seed"/>, then draws and discards <paramref name="skipValues"/> numbers.</summary>
    public void Seed(uint seed, uint skipValues = 0)
    {
        _seed = seed;
        _generator.Seed(seed);
        _callCount = 0;
        _usedValueBufferCount = 0;
        Skip(skipValues);
    }

    /// <summary>RandomMT: the next number of the shared sequence.</summary>
    public uint NextUInt32()
    {
        // RandSync.cpp:59-83.
        if (_usedValueBufferCount > 0)
        {
            --_usedValueBufferCount;
            if (_usedValueBufferCount < _usedValues.Count)
                return _usedValues[_usedValues.Count - (int)_usedValueBufferCount - 1];
            return _generator.NextUInt32();
        }
        _usedValues.Add(_generator.NextUInt32());
        ++_callCount;
        while (_usedValues.Count > UsedValueCapacity)
            _usedValues.RemoveAt(0);
        return _usedValues[^1];
    }

    /// <summary>FrandomMT: the next number divided by 2^32 - 1 (RandSync.cpp:55-58), in [0, 1].</summary>
    public float NextSingle() => (float)(NextUInt32() / (double)uint.MaxValue);

    /// <summary>SerializeConstruction: the seed, then the call count.</summary>
    public void SerializeConstruction(ref BitStreamWriter writer)
    {
        writer.WriteUInt32(_seed);
        writer.WriteUInt32(_callCount);
    }

    /// <inheritdoc cref="SerializeConstruction(ref BitStreamWriter)"/>
    public void SerializeConstruction(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteUInt32(_seed);
        stream.WriteUInt32(_callCount);
    }

    /// <summary>
    /// DeserializeConstruction: seeds with the sender's seed and skips its call count. False, changing nothing, when the
    /// call count cannot be read (RandSync.cpp:108-117) or is more than <see cref="MaxSkip"/>.
    /// </summary>
    public bool DeserializeConstruction(ref BitStreamReader reader)
    {
        reader.TryReadUInt32(out var seed);
        if (!reader.TryReadUInt32(out var skipValues) || skipValues > MaxSkip)
            return false;
        Seed(seed, skipValues);
        return true;
    }

    /// <summary>Serialize: the call count.</summary>
    public void Serialize(ref BitStreamWriter writer) => writer.WriteUInt32(_callCount);

    /// <inheritdoc cref="Serialize(ref BitStreamWriter)"/>
    public void Serialize(BitStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.WriteUInt32(_callCount);
    }

    /// <summary>
    /// Deserialize: catches up with the sender's call count (RandSync.cpp:122-141). False, changing nothing, when the call
    /// count cannot be read or is more than <see cref="MaxSkip"/> ahead of this one.
    /// </summary>
    public bool Deserialize(ref BitStreamReader reader)
    {
        if (!reader.TryReadUInt32(out var remoteCallCount))
            return false;
        if (remoteCallCount < _callCount)
        {
            // This system drew more than the sender, so its next numbers replay the ones the sender has yet to draw.
            _usedValueBufferCount = _callCount - remoteCallCount;
        }
        else if (remoteCallCount > _callCount)
        {
            var difference = remoteCallCount - _callCount;
            if (difference > MaxSkip)
                return false;
            if (difference <= _usedValueBufferCount)
                _usedValueBufferCount -= difference;
            Skip(difference);
        }
        return true;
    }

    // Skip (RandSync.cpp:142-147): the skipped numbers are counted but not kept for replay.
    private void Skip(uint count)
    {
        for (uint i = 0; i < count; i++)
            _generator.NextUInt32();
        _callCount += count;
    }
}
