using System.Text.Json;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

// Known answers from tools/cat-vectors (tests/vectors/cat/curve.json): cat's twisted Edwards curve over
// p = 2^256 - 435 as KeyAgreementCommon::InstantiateMath(256) builds it, driven through the same calls the key
// agreement makes.
public class BigTwistedEdwardsTests
{
    private static readonly JsonElement Curve = CatVectors.Load("curve");

    [Fact]
    public void The_curve_constants_match_cat()
    {
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var expected = Curve.GetProperty("curve");
        Assert.Equal(expected.Hex("q"), Save(math, math.CurveQ));
        Assert.Equal(expected.Hex("modulus"), Save(math, math.Modulus));
        Assert.Equal(expected.Hex("generator"), SaveXY(math, math.Generator));
        Assert.Equal(expected.GetProperty("d").GetUInt64(), math.CurveD);
    }

    [Fact]
    public void Only_the_256_bit_curve_is_built()
    {
        Assert.Null(KeyAgreementCommon.InstantiateMath(384));
    }

    public static TheoryData<int> Multiplies() => Indices("multiply");

    [Theory]
    [MemberData(nameof(Multiplies))]
    public void Scalar_multiplication_matches_cat_for_every_window(int index)
    {
        var c = Curve.GetProperty("multiply")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var n = math.RegBytes;
        var point = c.Hex("point");
        var pt = math.Get(0);
        Assert.True(math.LoadVerifyAffineXY(point.AsSpan(0, n), point.AsSpan(n), pt));
        math.PtUnpack(pt);
        var k = math.Get(4);
        math.Load(c.Hex("scalar"), k);
        var msb = (byte)c.GetProperty("msb").GetInt32();
        var output = math.Get(5);

        math.PtMultiply(pt, k, msb, output);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("result")), Convert.ToHexStringLower(SaveXY(math, output)));

        foreach (var window in c.GetProperty("precomputed").EnumerateArray())
        {
            var w = window.GetProperty("w").GetInt32();
            var table = math.PtMultiplyPrecompAlloc(w);
            math.PtMultiplyPrecomp(pt, w, table);
            math.PtMultiply(table, w, k, msb, output);
            Assert.Equal(Convert.ToHexStringLower(window.Hex("result")), Convert.ToHexStringLower(SaveXY(math, output)));
        }
    }

    public static TheoryData<int> SimultaneousMultiplies() => Indices("simultaneousMultiply");

    [Theory]
    [MemberData(nameof(SimultaneousMultiplies))]
    public void Simultaneous_multiplication_matches_cat(int index)
    {
        var c = Curve.GetProperty("simultaneousMultiply")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var n = math.RegBytes;
        var w = c.GetProperty("w").GetInt32();
        var p = math.Get(0);
        var q = math.Get(4);
        var k = math.Get(8);
        var l = math.Get(9);
        var output = math.Get(10);
        var pBytes = c.Hex("p");
        var qBytes = c.Hex("q");
        Assert.True(math.LoadVerifyAffineXY(pBytes.AsSpan(0, n), pBytes.AsSpan(n), p));
        math.PtUnpack(p);
        Assert.True(math.LoadVerifyAffineXY(qBytes.AsSpan(0, n), qBytes.AsSpan(n), q));
        math.PtUnpack(q);
        math.Load(c.Hex("k"), k);
        math.Load(c.Hex("l"), l);
        var tableP = math.PtMultiplyPrecompAlloc(w);
        var tableQ = math.PtMultiplyPrecompAlloc(w);
        math.PtMultiplyPrecomp(p, w, tableP);
        math.PtMultiplyPrecomp(q, w, tableQ);

        math.PtSiMultiply(tableP, tableQ, w, k, (byte)c.GetProperty("msbK").GetInt32(), l, (byte)c.GetProperty("msbL").GetInt32(), output);

        var x = new byte[n];
        math.SaveAffineX(output, x);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("resultX")), Convert.ToHexStringLower(x));
        Assert.Equal(Convert.ToHexStringLower(c.Hex("result")), Convert.ToHexStringLower(SaveXY(math, output)));
    }

    public static TheoryData<int> Cofactors() => Indices("cofactor");

    [Theory]
    [MemberData(nameof(Cofactors))]
    public void Cofactor_doubling_and_normalisation_match_cat(int index)
    {
        var c = Curve.GetProperty("cofactor")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var n = math.RegBytes;
        var point = c.Hex("point");
        var pt = math.Get(0);
        var h = math.Get(4);
        var normalized = math.Get(8);
        math.LoadVerifyAffineXY(point.AsSpan(0, n), point.AsSpan(n), pt);

        math.PtDoubleZ1(pt, h);
        math.PtEDouble(h, h);
        Assert.Equal(c.Hex("cofactorMultiple"), SaveXY(math, h));

        math.PtNormalize(h, normalized);
        var raw = new byte[n * 4];
        for (var r = 0; r < 4; r++)
            math.Save(normalized[(r * math.Legs)..], raw.AsSpan(r * n, n));
        Assert.Equal(Convert.ToHexStringLower(c.Hex("normalizedXYTZ")), Convert.ToHexStringLower(raw));
    }

    public static TheoryData<int> Validations() => Indices("validate");

    [Theory]
    [MemberData(nameof(Validations))]
    public void Point_validation_matches_cat(int index)
    {
        var c = Curve.GetProperty("validate")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var n = math.RegBytes;
        var xy = c.Hex("xy");
        var pt = math.Get(0);
        Assert.Equal(c.GetProperty("valid").GetBoolean(), math.LoadVerifyAffineXY(xy.AsSpan(0, n), xy.AsSpan(n), pt));
        Assert.Equal(c.GetProperty("identity").GetBoolean(), math.IsAffineIdentity(pt));
    }

    public static TheoryData<int> MulMods() => Indices("mulModQ");

    [Theory]
    [MemberData(nameof(MulMods))]
    public void Multiplication_modulo_q_matches_cat(int index)
    {
        var c = Curve.GetProperty("mulModQ")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var a = math.Get(0);
        var b = math.Get(1);
        var r = math.Get(2);
        math.Load(c.Hex("a"), a);
        math.Load(c.Hex("b"), b);
        math.MulMod(a, b, math.CurveQ, r);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("result")), Convert.ToHexStringLower(Save(math, r)));
    }

    public static TheoryData<int> Inverses() => Indices("invert");

    [Theory]
    [MemberData(nameof(Inverses))]
    public void Field_inversion_matches_cat(int index)
    {
        var c = Curve.GetProperty("invert")[index];
        var math = KeyAgreementCommon.InstantiateMath(256)!;
        var a = math.Get(0);
        var r = math.Get(1);
        math.Load(c.Hex("input"), a);
        math.MrInvert(a, r);
        Assert.Equal(Convert.ToHexStringLower(c.Hex("result")), Convert.ToHexStringLower(Save(math, r)));
    }

    public static TheoryData<int> KeyPairs() => Indices("keyPairs");

    [Theory]
    [MemberData(nameof(KeyPairs))]
    public void Key_pair_generation_matches_cat(int index)
    {
        var c = Curve.GetProperty("keyPairs")[index];
        var random = new ReplayRandom(c.Hex("random"));
        using var handshake = new EasyHandshake(random);
        var publicKey = new byte[EasyHandshake.PublicKeyBytes];
        var privateKey = new byte[EasyHandshake.PrivateKeyBytes];

        Assert.Equal(c.GetProperty("ok").GetBoolean(), handshake.GenerateServerKey(publicKey, privateKey));
        Assert.Equal(Convert.ToHexStringLower(c.Hex("publicKey")), Convert.ToHexStringLower(publicKey));
        Assert.Equal(Convert.ToHexStringLower(c.Hex("privateKey")), Convert.ToHexStringLower(privateKey));
        Assert.Equal(0, random.Remaining);
    }

    private static TheoryData<int> Indices(string section) => new(Enumerable.Range(0, Curve.GetProperty(section).GetArrayLength()));

    private static byte[] Save(BigTwistedEdwards math, ReadOnlySpan<ulong> register)
    {
        var bytes = new byte[math.RegBytes];
        math.Save(register, bytes);
        return bytes;
    }

    private static byte[] SaveXY(BigTwistedEdwards math, ReadOnlySpan<ulong> point)
    {
        var bytes = new byte[math.RegBytes * 2];
        math.SaveAffineXY(point, bytes.AsSpan(0, math.RegBytes), bytes.AsSpan(math.RegBytes));
        return bytes;
    }
}
