using RakDotNet.Security;

namespace RakDotNet.Tests.Security;

public class RakKeyPairTests
{
    public static TheoryData<int> GeneratedKeyPairs() => new(Enumerable.Range(0, CatKeyVectors.GeneratedKeyPairs().Count()));

    [Theory]
    [MemberData(nameof(GeneratedKeyPairs))]
    public void A_generated_key_pair_matches_cat_given_the_same_random_draws(int index)
    {
        var c = CatKeyVectors.GeneratedKeyPairs().ElementAt(index);
        var random = new RecordedRandom(c.Hex("random"));

        using var keys = RakKeyPair.Generate(random);

        Assert.Equal(c.Hex("publicKey"), keys.PublicKey.Bytes.ToArray());
        Assert.Equal(c.Hex("privateKey"), ExportPrivateKey(keys));
        Assert.Equal(0, random.Remaining);
    }

    [Fact]
    public void A_key_pair_from_the_system_csprng_can_be_imported_again()
    {
        using var keys = RakKeyPair.Generate();

        Assert.True(RakKeyPair.TryImport(keys.PublicKey.Bytes, ExportPrivateKey(keys), out var imported));
        using (imported)
        {
            Assert.Equal(keys.PublicKey, imported.PublicKey);
            Assert.Equal(ExportPrivateKey(keys), ExportPrivateKey(imported));
        }
    }

    public static TheoryData<string> CheckedKeys() => new(CatKeyVectors.CheckedKeys().Select(k => k.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(CheckedKeys))]
    public void A_key_pair_is_imported_when_a_cat_server_initializes_with_it(string name)
    {
        var c = CatKeyVectors.CheckedKey(name);

        var imported = RakKeyPair.TryImport(c.Hex("publicKey"), c.Hex("privateKey"), out var keys);

        Assert.Equal(c.GetProperty("serverInitialized").GetBoolean(), imported);
        Assert.Equal(c.GetProperty("identitySet").GetBoolean(), imported);
        if (imported)
        {
            using (keys)
                Assert.Equal(c.Hex("privateKey"), ExportPrivateKey(keys!));
        }
        else
        {
            Assert.Null(keys);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void A_private_key_of_the_wrong_length_is_refused(int length)
    {
        var c = CatKeyVectors.CheckedKey("valid");
        var privateKey = c.Hex("privateKey");
        Array.Resize(ref privateKey, length);
        Assert.False(RakKeyPair.TryImport(c.Hex("publicKey"), privateKey, out _));
    }

    [Fact]
    public void Exporting_the_private_key_needs_room_for_it()
    {
        using var keys = RakKeyPair.Generate();
        Assert.Throws<ArgumentException>(() => keys.ExportPrivateKey(new byte[RakKeyPair.PrivateKeyLength - 1]));
    }

    [Fact]
    public void A_disposed_key_pair_no_longer_gives_out_its_private_key()
    {
        var keys = RakKeyPair.Generate();
        keys.Dispose();
        keys.Dispose();
        Assert.Throws<ObjectDisposedException>(() => keys.ExportPrivateKey(new byte[RakKeyPair.PrivateKeyLength]));
        Assert.Equal(RakPublicKey.Length, keys.PublicKey.Bytes.Length);
    }

    private static byte[] ExportPrivateKey(RakKeyPair keys)
    {
        var bytes = new byte[RakKeyPair.PrivateKeyLength];
        keys.ExportPrivateKey(bytes);
        return bytes;
    }
}
