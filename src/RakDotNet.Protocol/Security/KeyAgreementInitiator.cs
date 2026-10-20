using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::KeyAgreementInitiator (crypt/tunnel/KeyAgreementInitiator.hpp, .cpp): the client side of the key
/// agreement, which knows the server's public key B in advance and may prove its own identity key.
/// </summary>
internal sealed class KeyAgreementInitiator : KeyAgreementCommon, IDisposable
{
    private const string KeyProofLabel = "shfolder.dll";

    private ulong[] _a = [];
    private byte[] _aNeutral = [];
    private byte[] _bNeutral = [];
    private ulong[]? _gMultPrecomp;
    private ulong[]? _bMultPrecomp;
    private ulong[]? _yMultPrecomp;
    private ulong[]? _identityPrivate;
    private byte[]? _identityPublic;

    /// <summary>KeyAgreementInitiator::Initialize: loads and checks the responder's public key.</summary>
    public bool Initialize(BigTwistedEdwards math, ReadOnlySpan<byte> responderPublicKey)
    {
        ArgumentNullException.ThrowIfNull(math);
        if (!Initialize(math.RegBytes * 8))
            return false;
        Array.Clear(_a);
        _a = new ulong[KeyLegs];
        _aNeutral = new byte[KeyBytes * 2];
        if (responderPublicKey.Length != KeyBytes * 2)
            return false;

        _gMultPrecomp = math.PtMultiplyPrecompAlloc(6);
        math.PtMultiplyPrecomp(math.Generator, 6, _gMultPrecomp);

        var bigB = math.Get(0);
        if (!math.LoadVerifyAffineXY(responderPublicKey[..KeyBytes], responderPublicKey[KeyBytes..], bigB))
            return false;
        if (math.IsAffineIdentity(bigB))
            return false;
        _bNeutral = responderPublicKey.ToArray();

        _bMultPrecomp = math.PtMultiplyPrecompAlloc(6);
        math.PtUnpack(bigB);
        math.PtMultiplyPrecomp(bigB, 6, _bMultPrecomp);
        return true;
    }

    /// <summary>KeyAgreementInitiator::SetIdentity: the initiator's own key pair, for two-way authentication.</summary>
    public bool SetIdentity(BigTwistedEdwards math, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey)
    {
        ArgumentNullException.ThrowIfNull(math);
        if (publicKey.Length != KeyBytes * 2 || privateKey.Length != KeyBytes)
            return false;
        var temp = math.Get(0);
        if (!math.LoadVerifyAffineXY(publicKey[..KeyBytes], publicKey[KeyBytes..], temp))
            return false;
        if (math.IsAffineIdentity(temp))
            return false;
        _identityPrivate ??= new ulong[KeyLegs];
        _identityPublic = publicKey.ToArray();
        math.Load(privateKey, _identityPrivate);
        return true;
    }

    /// <summary>KeyAgreementInitiator::GenerateChallenge: a fresh ephemeral a and the challenge A = a*G.</summary>
    public bool GenerateChallenge(BigTwistedEdwards math, ISecureRandom random, Span<byte> challenge)
    {
        ArgumentNullException.ThrowIfNull(math);
        ArgumentNullException.ThrowIfNull(random);
        if (_gMultPrecomp is null || challenge.Length != KeyBytes * 2)
            return false;
        GenerateKey(math, random, _a);
        var bigA = math.Get(0);
        math.PtMultiply(_gMultPrecomp, 6, _a, 0, bigA);
        math.PtNormalize(bigA, bigA);
        math.SaveAffineXY(bigA, challenge[..KeyBytes], challenge[KeyBytes..]);
        challenge.CopyTo(_aNeutral);
        return true;
    }

    /// <summary>
    /// KeyAgreementInitiator::ProcessAnswer: checks the responder's Y and its proof of k. <paramref name="keyHash"/>
    /// is left holding k for <see cref="KeyEncryption"/>.
    /// </summary>
    public bool ProcessAnswer(BigTwistedEdwards math, ReadOnlySpan<byte> answer, Skein keyHash)
    {
        ArgumentNullException.ThrowIfNull(math);
        ArgumentNullException.ThrowIfNull(keyHash);
        if (_bMultPrecomp is null || answer.Length < KeyBytes * 4) // cat checks for 3 keys and then reads the 4th
            return false;

        var y = math.Get(0);
        var s = math.Get(4);
        var t = math.Get(8);
        var hY = math.Get(12);
        var ah = math.Get(16);

        if (!math.LoadVerifyAffineXY(answer[..KeyBytes], answer.Slice(KeyBytes, KeyBytes), y))
            return false;
        if (math.IsAffineIdentity(y))
            return false;
        math.PtDoubleZ1(y, hY);
        math.PtEDouble(hY, hY);
        _yMultPrecomp ??= math.PtMultiplyPrecompAlloc(6);

        // S = H(A, B, Y, r)
        keyHash.BeginKey(KeyBits);
        keyHash.Crunch(_aNeutral);
        keyHash.Crunch(_bNeutral);
        keyHash.Crunch(answer[..(KeyBytes * 3)]);
        keyHash.End();
        Span<byte> hash = stackalloc byte[KeyBytes];
        keyHash.Generate(hash);
        math.Load(hash, s);
        if (math.LessX(s, 1000))
            return false;

        // ah = a*h, h = 4, reduced only on a carry out (KeyAgreementInitiator.cpp:267-270)
        if (math.Double(_a, ah) != 0)
            math.Subtract(ah, math.CurveQ, ah);
        if (math.Double(ah, ah) != 0)
            math.Subtract(ah, math.CurveQ, ah);

        // T = AffineX(ah * B + S*a * hY)
        math.MulMod(s, _a, math.CurveQ, s);
        math.PtMultiplyPrecomp(hY, 6, _yMultPrecomp);
        math.PtSiMultiply(_bMultPrecomp, _yMultPrecomp, 6, ah, 0, s, 0, t);
        math.SaveAffineX(t, hash);

        // k = H(d, T)
        keyHash.BeginKdf();
        keyHash.Crunch(hash);
        keyHash.End();
        CryptographicOperations.ZeroMemory(hash);

        var mac = new Skein();
        mac.SetKey(keyHash);
        mac.BeginMac();
        mac.CrunchString(KeyProofLabel);
        mac.End();
        Span<byte> expected = stackalloc byte[KeyBytes];
        mac.Generate(expected);
        mac.Clear();
        return CryptographicOperations.FixedTimeEquals(expected, answer.Slice(KeyBytes * 3, KeyBytes));
    }

    /// <summary>
    /// KeyAgreementInitiator::ProcessAnswerWithIdentity: <see cref="ProcessAnswer"/>, then the identity proof
    /// public key || nonce || Schnorr signature (e, s) over both nonces.
    /// </summary>
    public bool ProcessAnswerWithIdentity(BigTwistedEdwards math, ISecureRandom random, ReadOnlySpan<byte> answer, Skein keyHash, Span<byte> identityProof)
    {
        if (!ProcessAnswer(math, answer, keyHash))
            return false;
        ArgumentNullException.ThrowIfNull(random);
        if (identityProof.Length != KeyBytes * 5 || _identityPublic is null || _identityPrivate is null)
            return false;

        _identityPublic.CopyTo(identityProof);
        random.Fill(identityProof.Slice(KeyBytes * 2, KeyBytes));

        var k = math.Get(0);
        var bigK = math.Get(1);
        var e = math.Get(5);
        var s = math.Get(6);
        Span<byte> kBytes = stackalloc byte[KeyBytes];
        do
        {
            do
            {
                GenerateKey(math, random, k);
                math.PtMultiply(_gMultPrecomp!, 6, k, 0, bigK);
                math.SaveAffineX(bigK, kBytes);

                // e = H(IRN || RRN || K)
                var h = new Skein();
                h.BeginKey(KeyBits);
                h.Crunch(identityProof.Slice(KeyBytes * 2, KeyBytes));
                h.Crunch(answer.Slice(KeyBytes * 2, KeyBytes));
                h.Crunch(kBytes);
                h.End();
                h.Generate(identityProof.Slice(KeyBytes * 3, KeyBytes));
                h.Clear();
                math.Load(identityProof.Slice(KeyBytes * 3, KeyBytes), e);
                while (!math.Less(e, math.CurveQ))
                    math.Subtract(e, math.CurveQ, e);
            } while (math.IsZero(e));

            // s = k - I_private * e (mod q)
            math.MulMod(_identityPrivate, e, math.CurveQ, s);
            if (!math.IsZero(s))
                math.Subtract(math.CurveQ, s, s);
            if (math.Add(s, k, s) != 0)
                while (math.Subtract(s, math.CurveQ, s) == 0) { }
            while (!math.Less(s, math.CurveQ))
                math.Subtract(s, math.CurveQ, s);
        } while (math.IsZero(s));

        math.Save(s, identityProof.Slice(KeyBytes * 4, KeyBytes));
        math.CopyX(0, k);
        CryptographicOperations.ZeroMemory(kBytes);
        return true;
    }

    /// <summary>KeyAgreementInitiator::KeyEncryption: keys the initiator's side of the tunnel.</summary>
    public bool KeyEncryption(Skein keyHash, AuthenticatedEncryption authEnc, string keyName)
    {
        ArgumentNullException.ThrowIfNull(authEnc);
        return authEnc.SetKey(KeyBytes, keyHash, true, keyName);
    }

    /// <summary>KeyAgreementInitiator::SecureErasePrivateKey: zeroes the ephemeral a once the handshake is done.</summary>
    public void SecureErasePrivateKey() => Array.Clear(_a);

    /// <summary>Zeroes the private keys, as ~KeyAgreementInitiator does.</summary>
    public void Dispose()
    {
        Array.Clear(_a);
        if (_identityPrivate is not null)
            Array.Clear(_identityPrivate);
    }
}
