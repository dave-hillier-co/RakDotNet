using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/skein.json): each case is a script of Skein calls on
// numbered hash objects. cat's Skein advances the tweak by the whole remaining length for every block it hashes
// straight from a long crunch, so the way input is split into crunches changes the output; scripts keep cat's splits.
public class SkeinTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in CatVectors.Load("skein").GetProperty("cases").EnumerateArray())
            data.Add(c.GetProperty("name").GetString()!);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_generated_output_matches_cat(string name)
    {
        var script = CatVectors.Load("skein").GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var hashes = new Skein[4];
        for (var i = 0; i < hashes.Length; i++)
            hashes[i] = new Skein();

        var generated = 0;
        foreach (var op in script.GetProperty("ops").EnumerateArray())
        {
            var hash = hashes[op.GetProperty("hash").GetInt32()];
            switch (op.GetProperty("op").GetString())
            {
                case "beginKey":
                    Assert.True(hash.BeginKey(op.GetProperty("bits").GetInt32()));
                    break;
                case "setKey":
                    hash.SetKey(hashes[op.GetProperty("parent").GetInt32()]);
                    break;
                case "beginMac":
                    hash.BeginMac();
                    break;
                case "beginKdf":
                    hash.BeginKdf();
                    break;
                case "crunch":
                    hash.Crunch(op.Hex("data"));
                    break;
                case "end":
                    hash.End();
                    break;
                case "generate":
                    var output = new byte[op.GetProperty("bytes").GetInt32()];
                    hash.Generate(output);
                    Assert.Equal(Convert.ToHexStringLower(op.Hex("output")), Convert.ToHexStringLower(output));
                    generated++;
                    break;
                default:
                    throw new InvalidOperationException(op.GetProperty("op").GetString());
            }
        }
        Assert.True(generated > 0);
    }

    [Fact]
    public void Crunch_string_includes_the_terminating_nul()
    {
        var withString = KeyedHash();
        withString.BeginKdf();
        withString.CrunchString("dsound.dll");
        withString.End();

        var withBytes = KeyedHash();
        withBytes.BeginKdf();
        withBytes.Crunch("dsound.dll\0"u8);
        withBytes.End();

        var a = new byte[32];
        var b = new byte[32];
        withString.Generate(a);
        withBytes.Generate(b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Only_the_256_bit_state_cat_uses_for_RakNet_is_supported()
    {
        Assert.False(new Skein().BeginKey(512));
    }

    private static Skein KeyedHash()
    {
        var hash = new Skein();
        hash.BeginKey(256);
        hash.Crunch("key"u8);
        hash.End();
        return hash;
    }
}
