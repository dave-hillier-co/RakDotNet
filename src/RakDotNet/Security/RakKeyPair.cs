using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Security;

/// <summary>A server or client identity key pair. The private key lives in a pinned array zeroed on <see cref="Dispose"/>.</summary>
/// <remarks>Key material is validated on creation, so <see cref="RakPeer.Start"/> cannot fail on it.</remarks>
public sealed class RakKeyPair : IDisposable
{
    /// <summary>cat::EasyHandshake::PRIVATE_KEY_BYTES</summary>
    public const int PrivateKeyLength = 32;

    private RakKeyPair() { }

    /// <summary>EasyHandshake::GenerateServerKey, from the system CSPRNG.</summary>
    public static RakKeyPair Generate() => throw new NotImplementedException();

    /// <summary>Validates the keys as the cat responder does.</summary>
    public static bool TryImport(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey,
                                 [NotNullWhen(true)] out RakKeyPair? keyPair) => throw new NotImplementedException();

    public RakPublicKey PublicKey => throw new NotImplementedException();

    public void ExportPrivateKey(Span<byte> destination) => throw new NotImplementedException();

    /// <summary>Zeroes the private key with CryptographicOperations.ZeroMemory.</summary>
    public void Dispose() => throw new NotImplementedException();
}
