using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>
/// The reliable message numbers and send time of each recently sent data datagram, so an ACK or NAK for a datagram
/// can be mapped back to messages: datagramHistory and datagramHistoryPopCount (ReliabilityLayer.cpp:3767-3836).
/// </summary>
/// <remarks>
/// Datagrams are numbered consecutively, so entry <c>i</c> is datagram <see cref="FirstNumber"/> + i. A datagram that
/// carried no reliable or receipt-tracked message has an entry with no messages and is never found: it yields no RTT
/// sample, as in the C++.
/// </remarks>
internal sealed class DatagramHistory
{
    /// <summary>DATAGRAM_MESSAGE_ID_ARRAY_LENGTH, RakNetDefines.h:98.</summary>
    public const int Capacity = 512;

    private readonly RingQueue<Entry> _entries = new(Capacity + 1);

    /// <summary>datagramHistoryPopCount: the number of the oldest datagram still held.</summary>
    public UInt24 FirstNumber { get; private set; }

    public int Count => _entries.Count;

    /// <summary>Records the next datagram (AddFirstToDatagramHistory), evicting the oldest past the capacity.</summary>
    public void Add(long sentUs, IReadOnlyList<UInt24> messageNumbers)
    {
        ArgumentNullException.ThrowIfNull(messageNumbers);
        if (_entries.Count > Capacity)
        {
            _entries.Dequeue();
            FirstNumber++;
        }
        _entries.Enqueue(new Entry(sentUs, messageNumbers.Count == 0 ? null : messageNumbers));
    }

    /// <summary>GetMessageNumberNodeByDatagramIndex (ReliabilityLayer.cpp:3767-3781).</summary>
    public bool TryGet(UInt24 datagramNumber, out long sentUs, out IReadOnlyList<UInt24> messageNumbers)
    {
        sentUs = 0;
        messageNumbers = [];
        if (!TryGetOffset(datagramNumber, out var offset))
            return false;
        var entry = _entries[offset];
        if (entry.MessageNumbers is null)
            return false;
        sentUs = entry.SentUs;
        messageNumbers = entry.MessageNumbers;
        return true;
    }

    /// <summary>RemoveFromDatagramHistory (ReliabilityLayer.cpp:3783-3795): forget the datagram's messages.</summary>
    public void Remove(UInt24 datagramNumber)
    {
        if (TryGetOffset(datagramNumber, out var offset))
            _entries[offset] = _entries[offset] with { MessageNumbers = null };
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

    private readonly record struct Entry(long SentUs, IReadOnlyList<UInt24>? MessageNumbers);
}
