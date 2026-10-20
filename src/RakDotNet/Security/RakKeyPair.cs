using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Security;

/// <summary>A server or client identity key pair. The private key lives in a pinned array zeroed on <see cref="Dispose"/>.</summary>
/// <remarks>Key material is validated on creation, so <see cref="RakPeer.Start"/> cannot fail on it.</remarks>
public sealed class RakKeyPair : IDisposable
{
    /// <summary>cat::EasyHandshake::PRIVATE_KEY_BYTES</summary>
    public const int PrivateKeyLength = EasyHandshake.PrivateKeyBytes;

    private readonly byte[] _privateKey;
    private bool _disposed;

    private RakKeyPair(RakPublicKey publicKey, byte[] privateKey)
    {
        PublicKey = publicKey;
        _privateKey = privateKey;
    }

    /// <summary>EasyHandshake::GenerateServerKey, from the system CSPRNG.</summary>
    public static RakKeyPair Generate() => Generate(SystemSecureRandom.Instance);

    /// <summary>EasyHandshake::GenerateServerKey, from <paramref name="random"/>.</summary>
    internal static RakKeyPair Generate(ISecureRandom random)
    {
        Span<byte> publicKey = stackalloc byte[RakPublicKey.Length];
        var privateKey = GC.AllocateArray<byte>(PrivateKeyLength, pinned: true);
        using var maker = new EasyHandshake(random);
        if (!maker.GenerateServerKey(publicKey, privateKey) || !RakPublicKey.TryCreate(publicKey, out var key))
        {
            CryptographicOperations.ZeroMemory(privateKey);
            throw new CryptographicException("EasyHandshake::GenerateServerKey failed.");
        }
        return new RakKeyPair(key, privateKey);
    }

    /// <summary>
    /// Validates the keys as the cat responder does: the public key as <see cref="RakPublicKey.TryCreate"/>, the
    /// private key by its length only, as cat does not check that the two belong together.
    /// </summary>
    public static bool TryImport(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey,
                                 [NotNullWhen(true)] out RakKeyPair? keyPair)
    {
        keyPair = null;
        if (privateKey.Length != PrivateKeyLength || !RakPublicKey.TryCreate(publicKey, out var key))
            return false;
        var copy = GC.AllocateArray<byte>(PrivateKeyLength, pinned: true);
        privateKey.CopyTo(copy);
        keyPair = new RakKeyPair(key, copy);
        return true;
    }

    public RakPublicKey PublicKey { get; }

    /// <summary>Copies the private key into <paramref name="destination"/>, which needs <see cref="PrivateKeyLength"/> bytes.</summary>
    public void ExportPrivateKey(Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Length < PrivateKeyLength)
            throw new ArgumentException($"The destination needs {PrivateKeyLength} bytes.", nameof(destination));
        _privateKey.CopyTo(destination);
    }

    /// <summary>Zeroes the private key with CryptographicOperations.ZeroMemory.</summary>
    public void Dispose()
    {
        _disposed = true;
        CryptographicOperations.ZeroMemory(_privateKey);
    }
}
