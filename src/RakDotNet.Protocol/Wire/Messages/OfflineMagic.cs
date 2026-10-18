namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// The 16-byte OFFLINE_MESSAGE_DATA_ID (RakPeer.cpp:135) that marks unconnected messages so they cannot be
/// confused with reliability-layer datagrams. Always written and read byte-aligned.
/// </summary>
public static class OfflineMagic
{
    public const int Length = 16;

    public static ReadOnlySpan<byte> Bytes =>
        [0x00, 0xFF, 0xFF, 0x00, 0xFE, 0xFE, 0xFE, 0xFE, 0xFD, 0xFD, 0xFD, 0xFD, 0x12, 0x34, 0x56, 0x78];

    /// <summary>Aligns to a byte boundary and writes the magic, as every RakPeer.cpp send site does with WriteAlignedBytes.</summary>
    public static void Write(ref BitStreamWriter writer) => writer.WriteAlignedBytes(Bytes);

    /// <summary>Aligns, then consumes the magic if it matches. On failure the reader does not move.</summary>
    public static bool TryRead(ref BitStreamReader reader)
    {
        var r = reader;
        if (!r.TryReadAlignedBytes(Length, out var candidate) || !candidate.SequenceEqual(Bytes))
            return false;
        reader = r;
        return true;
    }

    /// <summary>True if <paramref name="candidate"/> starts with the magic.</summary>
    public static bool Matches(ReadOnlySpan<byte> candidate) =>
        candidate.Length >= Length && candidate[..Length].SequenceEqual(Bytes);
}
