namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>
/// An inclusive range of datagram numbers: DataStructures::RangeNode&lt;uint24_t&gt; (DS_RangeList.h:27-35).
/// </summary>
/// <remarks>
/// <see cref="Min"/> is never numerically greater than <see cref="Max"/>. RakNet compares uint24_t values numerically
/// (RakNetTypes.h:483-484) and rejects max &lt; min (DS_RangeList.h:134), so a range never straddles the 24-bit wrap.
/// </remarks>
public readonly record struct AckRange
{
    public AckRange(UInt24 min, UInt24 max)
    {
        if (min.Value > max.Value)
            throw new ArgumentException($"Range minimum {min} is greater than maximum {max}.", nameof(min));
        Min = min;
        Max = max;
    }

    public UInt24 Min { get; }

    public UInt24 Max { get; }

    public bool IsSingle => Min == Max;

    /// <summary>The number of datagram numbers covered, 1 to 2^24.</summary>
    public uint Count => Max.Value - Min.Value + 1;

    public bool Contains(UInt24 number) => number.Value >= Min.Value && number.Value <= Max.Value;

    public override string ToString() => IsSingle ? $"[{Min}]" : $"[{Min}..{Max}]";
}
