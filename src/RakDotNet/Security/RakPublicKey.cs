using System.Diagnostics.CodeAnalysis;
using RakDotNet.Protocol.Security;

namespace RakDotNet.Security;

/// <summary>A RakNet public key (cat::EasyHandshake).</summary>
public sealed class RakPublicKey : IEquatable<RakPublicKey>
{
    /// <summary>cat::EasyHandshake::PUBLIC_KEY_BYTES</summary>
    public const int Length = EasyHandshake.PublicKeyBytes;

    private readonly byte[] _bytes;

    private RakPublicKey(byte[] bytes) => _bytes = bytes;

    /// <summary>
    /// False when <paramref name="bytes"/> is not a valid key: not 64 bytes, or not a point cat accepts as a
    /// server or client key (on the curve, x not zero).
    /// </summary>
    public static bool TryCreate(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out RakPublicKey? key)
    {
        key = EasyHandshake.IsValidPublicKey(bytes) ? new RakPublicKey(bytes.ToArray()) : null;
        return key is not null;
    }

    public ReadOnlySpan<byte> Bytes => _bytes;

    public bool Equals(RakPublicKey? other) => other is not null && _bytes.AsSpan().SequenceEqual(other._bytes);

    public override bool Equals(object? obj) => Equals(obj as RakPublicKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }
}
