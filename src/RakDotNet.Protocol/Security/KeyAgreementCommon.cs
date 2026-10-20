using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::KeyAgreementCommon (crypt/tunnel/KeyAgreement.hpp, KeyAgreement.cpp): the curve parameters and key
/// generation shared by the "Tabby" key agreement. Only the 256-bit curve is built, the one EasyHandshake uses.
/// </summary>
internal abstract class KeyAgreementCommon
{
    /// <summary>Registers the key agreement code uses for its own values (KeyAgreement.hpp:181).</summary>
    public const int EccRegOverhead = 21;

    public const int EdwardC256 = 435;
    public const int EdwardD256 = 31720;
    public const int MaxBits = 512;
    public const int MaxBytes = MaxBits / 8;

    // KeyAgreement.cpp:49-58
    private static ReadOnlySpan<byte> Q256 =>
    [
        245, 131, 113, 179, 240, 8, 8, 95, 168, 93, 210, 180, 187, 107, 50, 108,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 64,
    ];

    private static ReadOnlySpan<byte> GeneratorPoint256 =>
    [
        199, 172, 100, 86, 21, 9, 105, 52, 27, 54, 112, 27, 130, 66, 212, 206, 2, 201,
        233, 157, 146, 53, 115, 139, 157, 11, 140, 127, 85, 208, 200, 234, 89, 166,
        146, 6, 210, 52, 127, 185, 80, 155, 102, 54, 140, 112, 165, 133, 28, 79, 155,
        87, 57, 23, 8, 77, 36, 126, 56, 208, 44, 204, 44, 122,
    ];

    protected int KeyBits { get; private set; }

    protected int KeyBytes { get; private set; }

    protected int KeyLegs { get; private set; }

    /// <summary>KeyAgreementCommon::InstantiateMath: the curve for <paramref name="bits"/>, or null.</summary>
    public static BigTwistedEdwards? InstantiateMath(int bits) =>
        bits == 256 ? new BigTwistedEdwards(EccRegOverhead, 256, EdwardC256, EdwardD256, Q256, GeneratorPoint256) : null;

    /// <summary>KeyAgreementCommon::Initialize: accepts cat's security levels.</summary>
    protected bool Initialize(int bits)
    {
        switch (bits)
        {
            case 256:
            case 384:
            case 512:
                KeyBits = bits;
                KeyBytes = bits / 8;
                KeyLegs = KeyBytes / sizeof(ulong);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// KeyAgreementCommon::GenerateKey: draws KeyBytes, clears the top bit and retries until 1 &lt; key &lt; q.
    /// </summary>
    protected void GenerateKey(BigTwistedEdwards math, ISecureRandom random, Span<ulong> key)
    {
        Span<byte> bytes = stackalloc byte[KeyBytes];
        do
        {
            random.Fill(bytes);
            math.Load(bytes, key);
            key[KeyLegs - 1] &= ~(1UL << 63);
        } while (!math.Less(key, math.CurveQ) || !math.GreaterX(key, 1));
        CryptographicOperations.ZeroMemory(bytes);
    }
}

/// <summary>cat::KeyMaker (crypt/tunnel/KeyMaker.hpp, KeyMaker.cpp): a long-term key pair b, B = b*G.</summary>
internal sealed class KeyMaker : KeyAgreementCommon
{
    public bool GenerateKeyPair(BigTwistedEdwards math, ISecureRandom random, Span<byte> publicKey, Span<byte> privateKey)
    {
        ArgumentNullException.ThrowIfNull(math);
        ArgumentNullException.ThrowIfNull(random);
        if (!Initialize(math.RegBytes * 8))
            return false;
        if (privateKey.Length != KeyBytes || publicKey.Length != KeyBytes * 2)
            return false;

        var b = math.Get(0);
        var bigB = math.Get(1);
        GenerateKey(math, random, b);
        math.PtMultiply(math.Generator, b, 0, bigB);
        math.SaveAffineXY(bigB, publicKey[..KeyBytes], publicKey[KeyBytes..]);
        math.Save(b, privateKey);
        return true;
    }
}
