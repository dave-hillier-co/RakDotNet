using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// Which reliable message numbers have arrived, so duplicates and late resends are dropped: receivedPacketsBaseIndex
/// and hasReceivedPacketQueue in ReliabilityLayer.cpp:941-1044.
/// </summary>
/// <remarks>
/// <see cref="BaseIndex"/> is the lowest number not yet received. The queue holds one flag per number from the base,
/// true for a hole still waiting to be filled.
/// </remarks>
internal sealed class ReceivedMessageNumbers
{
    // ReliabilityLayer.cpp:1003: a hole this large would exhaust memory.
    private const uint MaxHoleCount = 1_000_000;

    private readonly RingQueue<bool> _holes = new();

    public ReceivedMessageNumbers(UInt24 baseIndex = default)
    {
        BaseIndex = baseIndex;
    }

    public UInt24 BaseIndex { get; private set; }

    /// <summary>True when <paramref name="messageNumber"/> is new and should be processed.</summary>
    public bool TryAccept(UInt24 messageNumber) => Accept(messageNumber) == MessageNumberCheck.Accepted;

    /// <summary>Records <paramref name="messageNumber"/> when it is new, and says why when it is not.</summary>
    public MessageNumberCheck Accept(UInt24 messageNumber)
    {
        // The subtraction wraps on purpose (ReliabilityLayer.cpp:946).
        var holeCount = (messageNumber - BaseIndex).Value;
        if (holeCount == 0)
        {
            if (_holes.Count > 0)
                _holes.Dequeue();
            BaseIndex++;
        }
        else if (holeCount > UInt24.Mask / 2)
        {
            return MessageNumberCheck.BehindBase; // Behind the base: a duplicate.
        }
        else if (holeCount < (uint)_holes.Count)
        {
            if (!_holes[(int)holeCount])
                return MessageNumberCheck.Duplicate; // Already filled.
            _holes[(int)holeCount] = false;
        }
        else
        {
            if (holeCount > MaxHoleCount)
                return MessageNumberCheck.TooFarAhead;
            while (holeCount > (uint)_holes.Count)
                _holes.Enqueue(true);
            _holes.Enqueue(false);
        }

        while (_holes.Count > 0 && !_holes.Peek())
        {
            _holes.Dequeue();
            BaseIndex++;
        }
        return MessageNumberCheck.Accepted;
    }
}

/// <summary>What <see cref="ReceivedMessageNumbers.Accept"/> found, one value per branch of ReliabilityLayer.cpp:952-1037.</summary>
internal enum MessageNumberCheck : byte
{
    Accepted,

    /// <summary>holeCount &gt; typeRange / 2: behind the base index (line 959).</summary>
    BehindBase,

    /// <summary>A hole already filled (line 985).</summary>
    Duplicate,

    /// <summary>holeCount &gt; 1000000 (line 1003).</summary>
    TooFarAhead,
}
