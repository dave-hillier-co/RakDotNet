namespace RakDotNet.Interop.Tests.Harness;

/// <summary>Checks on a split message as a native RakNet receiver delivers it.</summary>
public static class SplitParts
{
    /// <summary>
    /// True when <paramref name="joined"/> is <paramref name="message"/>'s split parts joined in some order. The parts
    /// are <paramref name="partLength"/> bytes but the last, which holds the remainder (SplitPacket,
    /// ReliabilityLayer.cpp:2928-2950). Native RakNet 4.081 joins parts in the order they arrived, so this is what a
    /// native receiver delivers when the network reorders them (docs/plan.md, "Wire format notes").
    /// </summary>
    public static bool IsJoinedInSomeOrder(ReadOnlyMemory<byte> joined, ReadOnlyMemory<byte> message, int partLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partLength);
        if (joined.Length != message.Length)
        {
            return false;
        }

        var count = Math.Max(1, (message.Length + partLength - 1) / partLength);
        var lastStart = (count - 1) * partLength;
        var last = message[lastStart..];

        // The full parts as a multiset: for each distinct content, segments of the join taken minus parts sent.
        var ids = new Dictionary<ReadOnlyMemory<byte>, int>(ContentComparer.Instance);
        var surplus = new List<int>();
        var unbalanced = 0;
        void Count(ReadOnlyMemory<byte> segment, int delta)
        {
            if (!ids.TryGetValue(segment, out var id))
            {
                id = ids.Count;
                ids.Add(segment, id);
                surplus.Add(0);
            }
            var before = surplus[id];
            surplus[id] += delta;
            unbalanced += (surplus[id] != 0 ? 1 : 0) - (before != 0 ? 1 : 0);
        }

        for (var i = 0; i < count - 1; i++)
        {
            Count(message.Slice(i * partLength, partLength), -1);
        }

        // With the last part at position p of the join, full parts start at i * partLength before it and at
        // i * partLength + last.Length after it. Start with p at the end, then move it forward one part at a time.
        for (var i = 0; i < count - 1; i++)
        {
            Count(joined.Slice(i * partLength, partLength), +1);
        }
        for (var p = count - 1; ; p--)
        {
            if (unbalanced == 0 && joined.Slice(p * partLength, last.Length).Span.SequenceEqual(last.Span))
            {
                return true;
            }
            if (p == 0)
            {
                return false;
            }
            Count(joined.Slice((p - 1) * partLength, partLength), -1);
            Count(joined.Slice((p - 1) * partLength + last.Length, partLength), +1);
        }
    }

    private sealed class ContentComparer : IEqualityComparer<ReadOnlyMemory<byte>>
    {
        public static readonly ContentComparer Instance = new();

        public bool Equals(ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) => x.Span.SequenceEqual(y.Span);

        public int GetHashCode(ReadOnlyMemory<byte> obj)
        {
            var hash = new HashCode();
            hash.AddBytes(obj.Span);
            return hash.ToHashCode();
        }
    }
}
