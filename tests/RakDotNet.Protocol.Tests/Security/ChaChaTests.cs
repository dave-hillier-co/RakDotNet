using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/chacha.json). cat's ChaCha has 12 rounds, XORs the key
// into its own constants and counts blocks from 1, so it is not the BCL's ChaCha20.
public class ChaChaTests
{
    public static TheoryData<int> Cases() => new(Enumerable.Range(0, CatVectors.Load("chacha").GetProperty("cases").GetArrayLength()));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Crypt_matches_cat(int index)
    {
        var c = CatVectors.Load("chacha").GetProperty("cases")[index];
        var key = new ChaChaKey();
        key.Set(c.Hex("key"));
        var input = c.Hex("input");
        var output = new byte[input.Length];
        var cipher = new ChaChaOutput(in key, c.UInt64("iv"));
        cipher.Crypt(input, output);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("output")), Convert.ToHexStringLower(output));
    }

    [Fact]
    public void Crypt_works_in_place_and_is_its_own_inverse()
    {
        var key = new ChaChaKey();
        key.Set(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var message = Enumerable.Range(0, 150).Select(i => (byte)(i * 7)).ToArray();
        var buffer = (byte[])message.Clone();

        new ChaChaOutput(in key, 42).Crypt(buffer, buffer);
        Assert.NotEqual(message, buffer);
        new ChaChaOutput(in key, 42).Crypt(buffer, buffer);
        Assert.Equal(message, buffer);
    }
}
