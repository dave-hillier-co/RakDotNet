using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/hmac-md5.json). cat's HMAC-MD5 starts from its own
// initial state, reads words little-endian and pads without the 0x80 marker when the last block has room for the
// length, so the BCL's HMACMD5 cannot be used.
public class HmacMd5Tests
{
    public static TheoryData<int> Cases() => new(Enumerable.Range(0, CatVectors.Load("hmac-md5").GetProperty("cases").GetArrayLength()));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Keying_from_skein_and_the_mac_match_cat(int index)
    {
        var c = CatVectors.Load("hmac-md5").GetProperty("cases")[index];
        var parent = new Skein();
        parent.BeginKey(256);
        parent.Crunch(c.Hex("keySkeinInput"));
        parent.End();

        var key = new HmacMd5();
        key.SetKey(parent);
        Assert.Equal(c.Hex("cachedInitialState"), key.CachedInitialStateBytes());
        Assert.Equal(c.Hex("cachedFinalState"), key.CachedFinalStateBytes());

        var mac = new HmacMd5();
        mac.RekeyFrom(in key);
        mac.BeginMac();
        mac.Crunch(c.Hex("first"));
        mac.Crunch(c.Hex("message"));
        mac.End();
        var output = new byte[16];
        mac.Generate(output);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("mac")), Convert.ToHexStringLower(output));
    }

    [Fact]
    public void The_mac_does_not_depend_on_how_the_message_is_split_into_crunches()
    {
        var parent = new Skein();
        parent.BeginKey(256);
        parent.Crunch("key"u8);
        parent.End();
        var key = new HmacMd5();
        key.SetKey(parent);
        var message = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        var whole = Mac(key, [message]);
        Assert.Equal(whole, Mac(key, [message[..1], message[1..64], message[64..65], message[65..200], message[200..]]));
        Assert.Equal(whole, Mac(key, [message[..64], message[64..128], message[128..]]));
    }

    [Fact]
    public void Generate_gives_at_most_the_16_byte_digest()
    {
        var parent = new Skein();
        parent.BeginKey(256);
        parent.End();
        var key = new HmacMd5();
        key.SetKey(parent);
        key.RekeyFrom(in key);
        key.BeginMac();
        key.End();
        var output = new byte[20];
        key.Generate(output);
        Assert.Equal(new byte[4], output[16..]);
    }

    private static byte[] Mac(HmacMd5 key, byte[][] chunks)
    {
        var mac = new HmacMd5();
        mac.RekeyFrom(in key);
        mac.BeginMac();
        foreach (var chunk in chunks)
            mac.Crunch(chunk);
        mac.End();
        var output = new byte[8];
        mac.Generate(output);
        return output;
    }
}
