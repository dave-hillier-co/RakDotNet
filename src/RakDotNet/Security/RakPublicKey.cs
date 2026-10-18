using System.Diagnostics.CodeAnalysis;

namespace RakDotNet.Security;

/// <summary>A RakNet public key (cat::EasyHandshake).</summary>
public sealed class RakPublicKey : IEquatable<RakPublicKey>
{
    /// <summary>cat::EasyHandshake::PUBLIC_KEY_BYTES</summary>
    public const int Length = 64;

    private RakPublicKey() { }

    /// <summary>False when <paramref name="bytes"/> is not a valid key.</summary>
    public static bool TryCreate(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out RakPublicKey? key) =>
        throw new NotImplementedException();

    public ReadOnlySpan<byte> Bytes => throw new NotImplementedException();

    public bool Equals(RakPublicKey? other) => throw new NotImplementedException();

    public override bool Equals(object? obj) => Equals(obj as RakPublicKey);

    public override int GetHashCode() => throw new NotImplementedException();
}
