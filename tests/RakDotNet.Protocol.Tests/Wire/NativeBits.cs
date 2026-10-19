using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire;

/// <summary>
/// Checks against the known answers tools/bitstream-vectors prints from native RakNet: every case there starts with one
/// 1 bit, so each encoding is checked off a byte boundary.
/// </summary>
internal static class NativeBits
{
    public delegate void WriteAction(ref BitStreamWriter writer);

    /// <summary>Writes a 1 bit then <paramref name="write"/>, and checks the bit count and bytes.</summary>
    public static void AssertWritten(WriteAction write, int bits, string hex)
    {
        Span<byte> buffer = stackalloc byte[128];
        var writer = new BitStreamWriter(buffer);
        writer.WriteBit(true);
        write(ref writer);
        Assert.Equal(bits, writer.BitsWritten);
        Assert.Equal(hex, Convert.ToHexStringLower(writer.Written));
    }

    /// <summary>A reader over native output, past the leading 1 bit.</summary>
    public static BitStreamReader Reader(string hex, int bits)
    {
        var reader = new BitStreamReader(Convert.FromHexString(hex), bits);
        Assert.True(reader.TryReadBit(out var lead) && lead);
        return reader;
    }
}
