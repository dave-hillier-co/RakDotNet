using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// The reliable message numbers and send time of each recently sent data datagram, so an ACK or NAK for a datagram
/// can be mapped back to messages: datagramHistory and datagramHistoryPopCount (ReliabilityLayer.cpp:3767-3836).
/// </summary>
/// <remarks>
/// <para>
/// Datagrams are numbered consecutively, so entry <c>i</c> is datagram <see cref="FirstNumber"/> + i. A datagram that
/// carried no reliable or receipt-tracked message has an entry with no messages and is never found: it yields no RTT
/// sample, as in the C++.
/// </para>
/// <para>
/// Each entry copies its message numbers into a list that is reused once the entry is removed or evicted, so the
/// history allocates nothing in steady state.
/// </para>
/// </remarks>
internal sealed class DatagramHistory
{
    /// <summary>DATAGRAM_MESSAGE_ID_ARRAY_LENGTH, RakNetDefines.h:98.</summary>
    public const int Capacity = 512;

    // Never handed out with entries in it: TryGet returns it only with false.
    private static readonly List<UInt24> NoNumbers = [];

    private readonly RingQueue<Entry> _entries = new(Capacity + 1);
    private readonly Stack<List<UInt24>> _freeLists = new();

    /// <summary>datagramHistoryPopCount: the number of the oldest datagram still held.</summary>
    public UInt24 FirstNumber { get; private set; }

    public int Count => _entries.Count;

    /// <summary>
    /// Records the next datagram (AddFirstToDatagramHistory), evicting the oldest past the capacity. The numbers are
    /// copied, so the caller may reuse <paramref name="messageNumbers"/>.
    /// </summary>
    public void Add(long sentUs, IReadOnlyList<UInt24> messageNumbers)
    {
        ArgumentNullException.ThrowIfNull(messageNumbers);
        if (_entries.Count > Capacity)
        {
            Free(_entries.Dequeue().MessageNumbers);
            FirstNumber++;
        }
        List<UInt24>? copy = null;
        if (messageNumbers.Count > 0)
        {
            copy = _freeLists.TryPop(out var list) ? list : [];
            for (var i = 0; i < messageNumbers.Count; i++)
                copy.Add(messageNumbers[i]);
        }
        _entries.Enqueue(new Entry(sentUs, copy));
    }

    /// <summary>
    /// GetMessageNumberNodeByDatagramIndex (ReliabilityLayer.cpp:3767-3781). The list is the history's own and is valid
    /// until the datagram is removed or evicted.
    /// </summary>
    public bool TryGet(UInt24 datagramNumber, out long sentUs, out List<UInt24> messageNumbers)
    {
        sentUs = 0;
        messageNumbers = NoNumbers;
        if (!TryGetOffset(datagramNumber, out var offset))
            return false;
        var entry = _entries[offset];
        if (entry.MessageNumbers is null)
            return false;
        sentUs = entry.SentUs;
        messageNumbers = entry.MessageNumbers;
        return true;
    }

    /// <summary>True when the history holds an entry for <paramref name="datagramNumber"/>, with or without messages.</summary>
    public bool Holds(UInt24 datagramNumber) => TryGetOffset(datagramNumber, out _);

    /// <summary>RemoveFromDatagramHistory (ReliabilityLayer.cpp:3783-3795): forget the datagram's messages.</summary>
    public void Remove(UInt24 datagramNumber)
    {
        if (!TryGetOffset(datagramNumber, out var offset))
            return;
        Free(_entries[offset].MessageNumbers);
        _entries[offset] = _entries[offset] with { MessageNumbers = null };
    }

    private void Free(List<UInt24>? list)
    {
        if (list is null)
            return;
        list.Clear();
        _freeLists.Push(list);
    }

    private bool TryGetOffset(UInt24 datagramNumber, out int offset)
    {
        offset = 0;
        if (_entries.Count == 0 || UInt24.LessThan(datagramNumber, FirstNumber))
            return false;
        var distance = (datagramNumber - FirstNumber).Value;
        if (distance >= (uint)_entries.Count)
            return false;
        offset = (int)distance;
        return true;
    }

    private readonly record struct Entry(long SentUs, List<UInt24>? MessageNumbers);
}
