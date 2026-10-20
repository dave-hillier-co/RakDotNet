using RakDotNet.Security;

namespace RakDotNet.Tests.Security;

public class RakPublicKeyTests
{
    public static TheoryData<string> CheckedKeys() => new(CatKeyVectors.CheckedKeys().Select(k => k.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(CheckedKeys))]
    public void A_public_key_is_accepted_when_a_cat_client_accepts_it_as_the_server_key(string name)
    {
        var c = CatKeyVectors.CheckedKey(name);
        var bytes = c.Hex("publicKey");

        var created = RakPublicKey.TryCreate(bytes, out var key);

        Assert.Equal(c.GetProperty("clientInitialized").GetBoolean(), created);
        if (created)
            Assert.Equal(bytes, key!.Bytes.ToArray());
        else
            Assert.Null(key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void A_public_key_of_the_wrong_length_is_refused(int length)
    {
        var bytes = CatKeyVectors.CheckedKey("valid").Hex("publicKey");
        Array.Resize(ref bytes, length);
        Assert.False(RakPublicKey.TryCreate(bytes, out _));
    }

    [Fact]
    public void Public_keys_are_equal_when_their_bytes_are()
    {
        var bytes = CatKeyVectors.CheckedKey("valid").Hex("publicKey");
        Assert.True(RakPublicKey.TryCreate(bytes, out var a));
        Assert.True(RakPublicKey.TryCreate(bytes, out var b));
        Assert.True(RakPublicKey.TryCreate(CatKeyVectors.CheckedKey("x negated").Hex("publicKey"), out var other));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, other);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void A_public_key_keeps_its_own_copy_of_the_bytes()
    {
        var bytes = CatKeyVectors.CheckedKey("valid").Hex("publicKey");
        Assert.True(RakPublicKey.TryCreate(bytes, out var key));
        var expected = (byte[])bytes.Clone();
        bytes[0] ^= 1;
        Assert.Equal(expected, key.Bytes.ToArray());
    }
}
