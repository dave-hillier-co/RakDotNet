using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Wire;

/// <summary>A peer's RakNetGUID (RakNetTypes.h). Serialised as a big-endian u64.</summary>
/// <remarks>
/// <c>default</c> is GUID 0, which RakNet treats as assigned but never generates: a peer redraws its GUID until it is
/// neither 0 nor <see cref="Unassigned"/> (RakPeer.cpp:382).
/// </remarks>
public readonly record struct RakNetGuid(ulong Value) : ISpanFormattable, ISpanParsable<RakNetGuid>
{
    private const string UnassignedText = "UNASSIGNED_RAKNET_GUID";

    /// <summary>UNASSIGNED_RAKNET_GUID (RakNetTypes.h:364), <see cref="RakNetConstants.UnassignedGuid"/>.</summary>
    public static RakNetGuid Unassigned => new(RakNetConstants.UnassignedGuid);

    /// <summary>False only for <see cref="Unassigned"/>.</summary>
    public bool IsAssigned => Value != RakNetConstants.UnassignedGuid;

    /// <summary>
    /// The value in decimal, as RakNetGUID::ToString and the interop protocol write it, or <c>UNASSIGNED_RAKNET_GUID</c>
    /// (RakNetTypes.cpp:782-790).
    /// </summary>
    public override string ToString() => IsAssigned ? Value.ToString(CultureInfo.InvariantCulture) : UnassignedText;

    /// <inheritdoc cref="ToString()"/>
    /// <remarks><paramref name="format"/> and <paramref name="formatProvider"/> are ignored: the text is always invariant decimal.</remarks>
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc/>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (IsAssigned)
            return Value.TryFormat(destination, out charsWritten, default, CultureInfo.InvariantCulture);
        if (UnassignedText.TryCopyTo(destination))
        {
            charsWritten = UnassignedText.Length;
            return true;
        }
        charsWritten = 0;
        return false;
    }

    /// <summary>Parses the text <see cref="ToString()"/> writes.</summary>
    public static RakNetGuid Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static RakNetGuid Parse(ReadOnlySpan<char> s, IFormatProvider? provider) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException($"'{s}' is not a RakNetGUID.");

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out RakNetGuid result) =>
        TryParse(s.AsSpan(), provider, out result);

    /// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out RakNetGuid result)
    {
        if (s.SequenceEqual(UnassignedText))
        {
            result = Unassigned;
            return true;
        }
        var parsed = ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value);
        result = new RakNetGuid(value);
        return parsed;
    }
}
