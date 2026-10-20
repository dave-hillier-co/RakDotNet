using System.Text;

namespace RakDotNet.Protocol.Wire;

/// <summary>
/// RakNet's StringTable (StringTable.h): strings every system registers in the same way are sent as a one-byte index,
/// and any other string falls back to <see cref="StringCompressor"/>.
/// </summary>
/// <remarks>
/// The index is the string's position in the table sorted by <c>strcmp</c>, an ordinal byte comparison, so every system
/// must register the same strings; the order they are added in does not matter. Strings are bytes and end at their first
/// NUL, as RakNet's C strings do. Safe to use from several threads.
/// </remarks>
public sealed class StringTable
{
    // StringTableType is unsigned char, and AddString asserts the size stays below (StringTableType)-1 (StringTable.cpp:86).
    private const int MaxStrings = byte.MaxValue - 1;

    private readonly Lock _lock = new();
    private readonly List<byte[]> _sorted = [];

    /// <summary>StringTable::Instance, the process-wide table (StringTable.cpp:64-67).</summary>
    public static StringTable Instance { get; } = new();

    /// <summary>
    /// StringTable::AddString (StringTable.cpp:69-88): registers the bytes before the first NUL of
    /// <paramref name="value"/>. RakNet's copyString choice does not apply: the table always keeps its own copy.
    /// </summary>
    /// <exception cref="ArgumentException">The string is already registered, which RakNet asserts.</exception>
    /// <exception cref="InvalidOperationException">The table already holds 254 strings, which RakNet asserts.</exception>
    public void AddString(scoped ReadOnlySpan<byte> value)
    {
        var text = StringCompressor.ToCString(value);
        lock (_lock)
        {
            var index = Find(text);
            if (index >= 0)
                throw new ArgumentException("The string is already in the table.", nameof(value));
            if (_sorted.Count >= MaxStrings)
                throw new InvalidOperationException($"A StringTable holds at most {MaxStrings} strings.");
            _sorted.Insert(~index, text.ToArray());
        }
    }

    /// <summary>StringTable::AddString of <paramref name="value"/> encoded as UTF-8.</summary>
    /// <inheritdoc cref="AddString(ReadOnlySpan{byte})"/>
    public void AddString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        AddString(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// StringTable::EncodeString (StringTable.cpp:90-107): a 1 bit then the index as a byte for a registered string,
    /// otherwise a 0 bit then <see cref="StringCompressor.EncodeString"/> of <see cref="StringCompressor.Instance"/>
    /// with language 0 and <paramref name="maxCharCount"/>.
    /// </summary>
    public void EncodeString(ref BitStreamWriter writer, scoped ReadOnlySpan<byte> input, int maxCharCount)
    {
        int index;
        lock (_lock)
            index = Find(StringCompressor.ToCString(input));
        writer.WriteBit(index >= 0);
        if (index >= 0)
            writer.WriteByte((byte)index);
        else
            StringCompressor.Instance.EncodeString(ref writer, input, maxCharCount);
    }

    /// <summary>StringTable::EncodeString of <paramref name="input"/> encoded as UTF-8.</summary>
    public void EncodeString(ref BitStreamWriter writer, string input, int maxCharCount)
    {
        ArgumentNullException.ThrowIfNull(input);
        EncodeString(ref writer, Encoding.UTF8.GetBytes(input), maxCharCount);
    }

    /// <summary>
    /// StringTable::DecodeString (StringTable.cpp:109-141): a registered string keeps at most
    /// <paramref name="maxCharCount"/> - 1 bytes, as strncpy and the terminator leave it, and a compressed one is decoded
    /// as <see cref="StringCompressor.TryDecodeString"/> decodes it. False, without moving, for a limit that is not
    /// positive, an index past the table or truncated input; RakNet ignores a failed compressed decode.
    /// </summary>
    public bool TryDecodeString(ref BitStreamReader reader, int maxCharCount, out byte[] value)
    {
        value = [];
        if (maxCharCount <= 0)
            return false;
        var r = reader;
        if (!r.TryReadBit(out var hasIndex))
            return false;
        if (!hasIndex)
        {
            if (!StringCompressor.Instance.TryDecodeString(ref r, maxCharCount, out value))
                return false;
            reader = r;
            return true;
        }

        if (!r.TryReadByte(out var index))
            return false;
        lock (_lock)
        {
            if (index >= _sorted.Count)
                return false;
            var text = _sorted[index];
            value = text.AsSpan(0, Math.Min(text.Length, maxCharCount - 1)).ToArray();
        }
        reader = r;
        return true;
    }

    // The index of text in the sorted list, or the bitwise complement of where it would go (List<T>.BinarySearch style).
    private int Find(ReadOnlySpan<byte> text)
    {
        int low = 0, high = _sorted.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) >> 1);
            var order = _sorted[middle].AsSpan().SequenceCompareTo(text);
            if (order == 0)
                return middle;
            if (order < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return ~low;
    }
}
