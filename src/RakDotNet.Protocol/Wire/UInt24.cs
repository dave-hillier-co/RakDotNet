namespace RakDotNet.Protocol.Wire;

/// <summary>
/// Unsigned 24-bit integer with wrapping arithmetic, as used by RakNet for datagram numbers,
/// reliable message numbers, sequencing indices and ordering indices (RakNetTypes.h uint24_t).
/// </summary>
public readonly record struct UInt24
{
    public const uint Mask = 0x00FF_FFFF;

    /// <summary>Half the 24-bit span, used for serial-number comparison (CCRakNetSlidingWindow.cpp).</summary>
    public const uint HalfSpan = Mask / 2;

    public static readonly UInt24 MinValue = new(0);
    public static readonly UInt24 MaxValue = new(Mask);

    public UInt24(uint value)
    {
        Value = value & Mask;
    }

    public uint Value { get; }

    public static UInt24 operator +(UInt24 left, uint right) => new(left.Value + right);

    public static UInt24 operator -(UInt24 left, uint right) => new(left.Value - right);

    public static UInt24 operator +(UInt24 left, UInt24 right) => new(left.Value + right.Value);

    public static UInt24 operator -(UInt24 left, UInt24 right) => new(left.Value - right.Value);

    public static UInt24 operator ++(UInt24 value) => new(value.Value + 1);

    public static UInt24 operator --(UInt24 value) => new(value.Value - 1);

    public static implicit operator uint(UInt24 value) => value.Value;

    public static explicit operator UInt24(uint value) => new(value);

    /// <summary>
    /// True when <paramref name="a"/> is ahead of <paramref name="b"/> in wrapping order.
    /// Matches CCRakNetSlidingWindow::GreaterThan.
    /// </summary>
    public static bool GreaterThan(UInt24 a, UInt24 b) => a != b && (b - a).Value > HalfSpan;

    /// <summary>
    /// True when <paramref name="a"/> is behind <paramref name="b"/> in wrapping order.
    /// Matches CCRakNetSlidingWindow::LessThan.
    /// </summary>
    public static bool LessThan(UInt24 a, UInt24 b) => a != b && (b - a).Value < HalfSpan;

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
