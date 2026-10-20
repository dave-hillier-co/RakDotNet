using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::KeyAgreementResponder (crypt/tunnel/KeyAgreementResponder.hpp, .cpp): the server side of the key
/// agreement. Holds the long-term key b, B and an ephemeral key y, Y that is replaced after every 100 challenges.
/// Not thread-safe, as RakNet uses it from its update thread only.
/// </summary>
internal sealed class KeyAgreementResponder : KeyAgreementCommon, IDisposable
{
    private const string KeyProofLabel = "shfolder.dll";

    private ulong[] _b = [];
    private byte[] _bNeutral = [];
    private readonly ulong[][] _y = [[], []];
    private readonly byte[][] _yNeutral = [[], []];
    private ulong[]? _gMultPrecomp;
    private uint _challengeCount;
    private uint _activeY;

    /// <summary>KeyAgreementResponder::Initialize: loads and checks the key pair, then makes the first ephemeral key.</summary>
    public bool Initialize(BigTwistedEdwards math, ISecureRandom random, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey)
    {
        ArgumentNullException.ThrowIfNull(math);
        ArgumentNullException.ThrowIfNull(random);
        if (!Initialize(math.RegBytes * 8))
            return false;
        Clear();
        _b = new ulong[KeyLegs];
        _y[0] = new ulong[KeyLegs];
        _y[1] = new ulong[KeyLegs];
        _yNeutral[0] = new byte[KeyBytes * 2];
        _yNeutral[1] = new byte[KeyBytes * 2];
        if (privateKey.Length != KeyBytes || publicKey.Length != KeyBytes * 2)
            return false;

        _gMultPrecomp = math.PtMultiplyPrecompAlloc(8);
        math.PtMultiplyPrecomp(math.Generator, 8, _gMultPrecomp);

        math.Load(privateKey, _b);
        var bigB = math.Get(0);
        if (!math.LoadVerifyAffineXY(publicKey[..KeyBytes], publicKey[KeyBytes..], bigB))
            return false;
        math.PtUnpack(bigB);
        if (math.IsAffineIdentity(bigB))
            return false;
        _bNeutral = publicKey.ToArray();

        _challengeCount = 0;
        _activeY = 0;
        Rekey(math, random);
        return true;
    }

    /// <summary>
    /// KeyAgreementResponder::ProcessChallenge: checks the initiator's A and writes the answer Y || r || MAC(k).
    /// <paramref name="keyHash"/> is left holding k for <see cref="KeyEncryption"/>.
    /// </summary>
    public bool ProcessChallenge(BigTwistedEdwards math, ISecureRandom random, ReadOnlySpan<byte> challenge, Span<byte> answer, Skein keyHash)
    {
        ArgumentNullException.ThrowIfNull(math);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentNullException.ThrowIfNull(keyHash);
        if (_gMultPrecomp is null || challenge.Length != KeyBytes * 2 || answer.Length != KeyBytes * 4)
            return false;

        var a = math.Get(0);
        var s = math.Get(8);
        var t = math.Get(12);
        var hA = math.Get(16);

        if (!math.LoadVerifyAffineXY(challenge[..KeyBytes], challenge[KeyBytes..], a))
            return false;
        if (math.IsAffineIdentity(a))
            return false;
        math.PtDoubleZ1(a, hA);
        math.PtEDouble(hA, hA);

        if (_challengeCount++ == 100)
            Rekey(math, random);

        var thisY = _activeY;
        _yNeutral[thisY].CopyTo(answer);
        Span<byte> hash = stackalloc byte[KeyBytes];
        do
        {
            random.Fill(answer.Slice(KeyBytes * 2, KeyBytes));
            keyHash.BeginKey(KeyBits);
            keyHash.Crunch(challenge);
            keyHash.Crunch(_bNeutral);
            keyHash.Crunch(answer[..(KeyBytes * 3)]);
            keyHash.End();
            keyHash.Generate(hash);
            math.Load(hash, s);
        } while (math.LessX(s, 1000));

        // T = S*y + b (mod q)
        math.MulMod(s, _y[thisY], math.CurveQ, t);
        if (math.Add(t, _b, t) != 0)
            math.Subtract(t, math.CurveQ, t);
        while (!math.Less(t, math.CurveQ))
            math.Subtract(t, math.CurveQ, t);

        // T = AffineX(T * hA), k = H(d, T)
        math.PtMultiply(hA, t, 0, s);
        math.SaveAffineX(s, hash);
        keyHash.BeginKdf();
        keyHash.Crunch(hash);
        keyHash.End();
        CryptographicOperations.ZeroMemory(hash);

        var mac = new Skein();
        mac.SetKey(keyHash);
        mac.BeginMac();
        mac.CrunchString(KeyProofLabel);
        mac.End();
        mac.Generate(answer.Slice(KeyBytes * 3, KeyBytes));
        mac.Clear();
        return true;
    }

    /// <summary>KeyAgreementResponder::KeyEncryption: keys the responder's side of the tunnel.</summary>
    public bool KeyEncryption(Skein keyHash, AuthenticatedEncryption authEnc, string keyName)
    {
        ArgumentNullException.ThrowIfNull(authEnc);
        return authEnc.SetKey(KeyBytes, keyHash, false, keyName);
    }

    /// <summary>
    /// KeyAgreementResponder::VerifyInitiatorIdentity: checks the initiator's Schnorr signature over both nonces
    /// and, when it holds, gives the initiator's public key.
    /// </summary>
    public bool VerifyInitiatorIdentity(BigTwistedEdwards math, ReadOnlySpan<byte> answer, ReadOnlySpan<byte> proof, Span<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(math);
        if (_gMultPrecomp is null || proof.Length != KeyBytes * 5 || answer.Length != KeyBytes * 4 || publicKey.Length != KeyBytes * 2)
            return false;

        var e = math.Get(0);
        var s = math.Get(1);
        var kp = math.Get(2);
        var iPublic = math.Get(7);

        math.Load(proof.Slice(KeyBytes * 3, KeyBytes), e);
        math.Load(proof.Slice(KeyBytes * 4, KeyBytes), s);
        while (!math.Less(e, math.CurveQ))
            math.Subtract(e, math.CurveQ, e);
        if (math.IsZero(e) || math.IsZero(s) || !math.Less(e, math.CurveQ) || !math.Less(s, math.CurveQ))
            return false;

        if (!math.LoadVerifyAffineXY(proof[..KeyBytes], proof.Slice(KeyBytes, KeyBytes), iPublic))
            return false;
        if (math.IsAffineIdentity(iPublic))
            return false;

        var iMultPrecomp = math.PtMultiplyPrecompAlloc(8);
        math.PtUnpack(iPublic);
        math.PtMultiplyPrecomp(iPublic, 8, iMultPrecomp);
        math.PtSiMultiply(_gMultPrecomp, iMultPrecomp, 8, s, 0, e, 0, kp);
        Span<byte> kpBytes = stackalloc byte[KeyBytes];
        math.SaveAffineX(kp, kpBytes);

        // e' = H(IRN || RRN || K')
        var h = new Skein();
        h.BeginKey(KeyBits);
        h.Crunch(proof.Slice(KeyBytes * 2, KeyBytes));
        h.Crunch(answer.Slice(KeyBytes * 2, KeyBytes));
        h.Crunch(kpBytes);
        h.End();
        Span<byte> ep = stackalloc byte[KeyBytes];
        h.Generate(ep);
        h.Clear();

        var success = CryptographicOperations.FixedTimeEquals(proof.Slice(KeyBytes * 3, KeyBytes), ep);
        if (success)
            proof[..(KeyBytes * 2)].CopyTo(publicKey);
        return success;
    }

    /// <summary>Zeroes the private keys, as ~KeyAgreementResponder does.</summary>
    public void Dispose() => Clear();

    // KeyAgreementResponder::Rekey: a new ephemeral y, Y = y*G.
    private void Rekey(BigTwistedEdwards math, ISecureRandom random)
    {
        var nextY = _activeY ^ 1;
        GenerateKey(math, random, _y[nextY]);
        var point = math.Get(4);
        math.PtMultiply(_gMultPrecomp!, 8, _y[nextY], 0, point);
        math.SaveAffineXY(point, _yNeutral[nextY].AsSpan(0, KeyBytes), _yNeutral[nextY].AsSpan(KeyBytes));
        _activeY = nextY;
        _challengeCount = 0;
    }

    private void Clear()
    {
        Array.Clear(_b);
        Array.Clear(_y[0]);
        Array.Clear(_y[1]);
    }
}
