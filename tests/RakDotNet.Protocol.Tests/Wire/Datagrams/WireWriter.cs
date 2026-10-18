using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Tests.Wire.Datagrams;

internal delegate void WriteAction(ref BitStreamWriter writer);

internal static class WireWriter
{
    public static byte[] Write(WriteAction write) => Write(write, out _);

    public static byte[] Write(WriteAction write, out int bits)
    {
        var writer = new BitStreamWriter(new byte[4096]);
        write(ref writer);
        bits = writer.BitsWritten;
        return writer.Written.ToArray();
    }

    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
}
