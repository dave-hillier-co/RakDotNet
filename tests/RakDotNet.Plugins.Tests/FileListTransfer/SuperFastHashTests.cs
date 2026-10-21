using System.Text;

namespace RakDotNet.Plugins.Tests.FileListTransfer;

/// <summary>Known answers printed by native SuperFastHash.cpp (tools/file-vectors).</summary>
public sealed class SuperFastHashTests
{
    private static readonly byte[] Text = Encoding.ASCII.GetBytes("Hello, World!");
    private static readonly byte[] High = [0x80, 0xff, 0xfe, 0x81, 0xc3, 0x9f, 0xa0];

    [Theory]
    [InlineData(0, 0x00000000u)]
    [InlineData(1, 0xc7dd6143u)]
    [InlineData(2, 0xa851e25fu)]
    [InlineData(3, 0xd9eef027u)]
    [InlineData(4, 0x9d5b8ae3u)]
    [InlineData(5, 0x3891935au)]
    [InlineData(6, 0x06b6a3dfu)]
    [InlineData(7, 0x89b2f9d9u)]
    [InlineData(8, 0x23ffff1cu)]
    [InlineData(9, 0xc7c72b76u)]
    [InlineData(10, 0x6b955c3eu)]
    [InlineData(11, 0x36d04dd2u)]
    [InlineData(12, 0xc2f067dau)]
    [InlineData(13, 0x86956be9u)]
    public void Hashes_text_as_native_does(int length, uint expected) =>
        Assert.Equal(expected, SuperFastHash.Hash(Text.AsSpan(0, length)));

    [Theory]
    [InlineData(1, 0xf30533c4u)]
    [InlineData(2, 0x34e99c25u)]
    [InlineData(3, 0x390dc6cau)]
    [InlineData(4, 0x6f075aacu)]
    [InlineData(5, 0x42437fbfu)]
    [InlineData(6, 0x585739a7u)]
    [InlineData(7, 0x1663a453u)]
    public void Sign_extends_trailing_bytes_as_a_signed_char_platform_does(int length, uint expected) =>
        // SuperFastHash.cpp:96, 104: data is const char*, signed on the platforms RakNet targets.
        Assert.Equal(expected, SuperFastHash.Hash(High.AsSpan(0, length)));

    [Theory]
    [InlineData(65535, 0x236f588bu)]
    [InlineData(65536, 0xb88f6873u)]
    [InlineData(65539, 0xde7aafb2u)]
    [InlineData(131072, 0xea31bf59u)]
    [InlineData(200000, 0x827f718du)]
    public void Hashes_in_blocks_of_64_KiB(int length, uint expected) =>
        Assert.Equal(expected, SuperFastHash.Hash(Pattern(200000).AsSpan(0, length)));

    [Fact]
    public void An_incremental_hash_continues_from_the_last_one()
    {
        var pattern = Pattern(107);

        var first = SuperFastHash.HashIncremental(pattern.AsSpan(0, 100), 107);

        Assert.Equal(0xa9be66cdu, SuperFastHash.HashIncremental(pattern.AsSpan(100, 7), first));
    }

    [Fact]
    public void A_file_hashes_as_its_contents_do()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rakdotnet-sfh-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, Pattern(131072));
        try
        {
            Assert.Equal(0xea31bf59u, SuperFastHash.HashFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_missing_file_hashes_to_zero() =>
        Assert.Equal(0u, SuperFastHash.HashFile(Path.Combine(Path.GetTempPath(), $"rakdotnet-missing-{Guid.NewGuid():N}")));

    internal static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = unchecked((byte)(i * 31 + 7));
        return bytes;
    }
}
