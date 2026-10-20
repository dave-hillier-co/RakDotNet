using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Hash known answers from tools/cat-vectors (tests/vectors/cat/cookie-jar.json); the epoch logic is from
// CookieJar.cpp, where an epoch is a 250 ms bin of a millisecond clock and a cookie stays valid for 16 bins.
public class CookieJarTests
{
    public static TheoryData<int> Cases() => new(Enumerable.Range(0, CatVectors.Load("cookie-jar").GetProperty("cases").GetArrayLength()));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hash_matches_cat(int index)
    {
        var c = CatVectors.Load("cookie-jar").GetProperty("cases")[index];
        var jar = new CookieJar();
        jar.Initialize(new ReplayRandom(c.Hex("key")));
        Assert.Equal(c.GetProperty("hash").GetUInt32(), jar.Hash(c.Hex("address"), c.GetProperty("epoch").GetUInt32()));
    }

    [Fact]
    public void A_cookie_carries_its_epoch_bin_in_the_low_four_bits()
    {
        var jar = Jar();
        var address = new byte[16];
        const uint nowMs = 1_000_000;
        var cookie = jar.Generate(address, nowMs);
        Assert.Equal(nowMs / 250 & 15, cookie & 15);
        Assert.Equal(jar.Hash(address, nowMs / 250) << 4, cookie & ~15u);
    }

    [Fact]
    public void A_cookie_verifies_for_its_address_until_it_is_sixteen_bins_old()
    {
        var jar = Jar();
        var address = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        const uint issuedMs = 5_000_123;
        var cookie = jar.Generate(address, issuedMs);

        Assert.True(jar.Verify(address, cookie, issuedMs));
        Assert.True(jar.Verify(address, cookie, issuedMs + 3_000));
        var lastBin = (issuedMs / 250 + 15) * 250;
        Assert.True(jar.Verify(address, cookie, lastBin + 249));
        Assert.False(jar.Verify(address, cookie, lastBin + 250));

        var other = (byte[])address.Clone();
        other[3] ^= 1;
        Assert.False(jar.Verify(other, cookie, issuedMs));
        Assert.False(jar.Verify(address, cookie ^ 0x100, issuedMs));
    }

    [Fact]
    public void Address_information_is_limited_to_48_bytes()
    {
        var jar = Jar();
        Assert.Throws<ArgumentOutOfRangeException>(() => jar.Hash(new byte[49], 0));
    }

    private static CookieJar Jar()
    {
        var jar = new CookieJar();
        jar.Initialize(new ReplayRandom(Enumerable.Range(0, 64).Select(i => (byte)(i * 3 + 1)).ToArray()));
        return jar;
    }
}
