namespace RakDotNet.Protocol.Security;

/// <summary>
/// cat::EasyHandshake (crypt/tunnel/EasyHandshake.hpp, .cpp): the handshake RakNet's LIBCAT_SECURITY build uses,
/// over the 256-bit curve. Each object owns its curve registers, as cat's tls_math, and draws randomness from
/// an <see cref="ISecureRandom"/> in place of cat's FortunaOutput.
/// </summary>
/// <remarks>
/// On the wire: the client sends a 64-byte challenge, the server answers with 128 bytes, and the client proves
/// the key with 32 bytes, or 160 bytes of identity when it authenticates too. Not thread-safe.
/// </remarks>
internal class EasyHandshake : IDisposable
{
    public const int Bits = 256;
    public const int Bytes = Bits / 8;
    public const int PublicKeyBytes = Bytes * 2;
    public const int PrivateKeyBytes = Bytes;
    public const int ChallengeBytes = Bytes * 2;
    public const int AnswerBytes = Bytes * 4;
    public const int ProofBytes = Bytes;
    public const int IdentityBytes = Bytes * 5;

    /// <summary>The key name RakNet's handshakes key their tunnels with (EasyHandshake.cpp:157, 229, 257).</summary>
    protected const string TunnelKeyName = "NtQuerySystemInformation";

    public EasyHandshake(ISecureRandom random)
    {
        ArgumentNullException.ThrowIfNull(random);
        Random = random;
        Math = KeyAgreementCommon.InstantiateMath(Bits)!;
    }

    protected BigTwistedEdwards Math { get; }

    protected ISecureRandom Random { get; }

    /// <summary>EasyHandshake::GenerateServerKey: a long-term key pair.</summary>
    public bool GenerateServerKey(Span<byte> publicKey, Span<byte> privateKey) =>
        new KeyMaker().GenerateKeyPair(Math, Random, publicKey, privateKey);

    /// <summary>Zeroes the curve registers, as ~EasyHandshake deletes tls_math.</summary>
    public virtual void Dispose()
    {
        Math.Clear();
        GC.SuppressFinalize(this);
    }
}

/// <summary>cat::ServerEasyHandshake: answers client challenges with the server's key pair.</summary>
internal sealed class ServerEasyHandshake(ISecureRandom random) : EasyHandshake(random)
{
    private readonly KeyAgreementResponder _responder = new();

    /// <summary>ServerEasyHandshake::FillCookieJar: keys the cookie jar from this handshake's CSPRNG.</summary>
    public void FillCookieJar(CookieJar jar)
    {
        ArgumentNullException.ThrowIfNull(jar);
        jar.Initialize(Random);
    }

    public bool Initialize(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey) =>
        _responder.Initialize(Math, Random, publicKey, privateKey);

    /// <summary>ServerEasyHandshake::ProcessChallenge: false for an invalid challenge.</summary>
    public bool ProcessChallenge(ReadOnlySpan<byte> challenge, Span<byte> answer, AuthenticatedEncryption authEnc)
    {
        ArgumentNullException.ThrowIfNull(authEnc);
        var keyHash = new Skein();
        try
        {
            return _responder.ProcessChallenge(Math, Random, challenge, answer, keyHash)
                && _responder.KeyEncryption(keyHash, authEnc, TunnelKeyName);
        }
        finally
        {
            keyHash.Clear();
        }
    }

    /// <summary>ServerEasyHandshake::VerifyInitiatorIdentity: the client's public key when its proof holds.</summary>
    public bool VerifyInitiatorIdentity(ReadOnlySpan<byte> answer, ReadOnlySpan<byte> identity, Span<byte> publicKey) =>
        _responder.VerifyInitiatorIdentity(Math, answer, identity, publicKey);

    public override void Dispose()
    {
        _responder.Dispose();
        base.Dispose();
    }
}

/// <summary>cat::ClientEasyHandshake: challenges a server whose public key it knows.</summary>
internal sealed class ClientEasyHandshake(ISecureRandom random) : EasyHandshake(random)
{
    private readonly KeyAgreementInitiator _initiator = new();

    public bool Initialize(ReadOnlySpan<byte> serverPublicKey) => _initiator.Initialize(Math, serverPublicKey);

    public bool SetIdentity(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey) =>
        _initiator.SetIdentity(Math, publicKey, privateKey);

    public bool GenerateChallenge(Span<byte> challenge) => _initiator.GenerateChallenge(Math, Random, challenge);

    /// <summary>ClientEasyHandshake::ProcessAnswer: keys <paramref name="authEnc"/> when the answer is valid.</summary>
    public bool ProcessAnswer(ReadOnlySpan<byte> answer, AuthenticatedEncryption authEnc)
    {
        ArgumentNullException.ThrowIfNull(authEnc);
        var keyHash = new Skein();
        try
        {
            if (!_initiator.ProcessAnswer(Math, answer, keyHash) || !_initiator.KeyEncryption(keyHash, authEnc, TunnelKeyName))
                return false;
            _initiator.SecureErasePrivateKey();
            return true;
        }
        finally
        {
            keyHash.Clear();
        }
    }

    /// <summary>ClientEasyHandshake::ProcessAnswerWithIdentity: as ProcessAnswer, also writing the identity proof.</summary>
    public bool ProcessAnswerWithIdentity(ReadOnlySpan<byte> answer, Span<byte> identity, AuthenticatedEncryption authEnc)
    {
        ArgumentNullException.ThrowIfNull(authEnc);
        var keyHash = new Skein();
        try
        {
            if (!_initiator.ProcessAnswerWithIdentity(Math, Random, answer, keyHash, identity)
                || !_initiator.KeyEncryption(keyHash, authEnc, TunnelKeyName))
                return false;
            _initiator.SecureErasePrivateKey();
            return true;
        }
        finally
        {
            keyHash.Clear();
        }
    }

    public override void Dispose()
    {
        _initiator.Dispose();
        base.Dispose();
    }
}
