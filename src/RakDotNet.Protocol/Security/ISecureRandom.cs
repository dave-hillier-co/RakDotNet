using System.Security.Cryptography;

namespace RakDotNet.Protocol.Security;

/// <summary>
/// The CSPRNG the handshake draws keys, nonces and cookie keys from: cat's FortunaOutput, whose output is not on
/// the wire. <see cref="SystemSecureRandom"/> is the only production source (api-design.md: randomness comes from
/// RandomNumberGenerator); tests substitute recorded draws to reproduce cat's known answers.
/// </summary>
internal interface ISecureRandom
{
    void Fill(Span<byte> destination);
}

/// <summary>RandomNumberGenerator, the system CSPRNG.</summary>
internal sealed class SystemSecureRandom : ISecureRandom
{
    public static SystemSecureRandom Instance { get; } = new();

    private SystemSecureRandom() { }

    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}
