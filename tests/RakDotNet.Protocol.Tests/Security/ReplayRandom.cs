using RakDotNet.Protocol.Security;

namespace RakDotNet.Protocol.Tests.Security;

/// <summary>
/// Hands out the CSPRNG draws tools/cat-vectors recorded from cat, in order, so a handshake is reproduced
/// exactly. Stands in for the system CSPRNG only.
/// </summary>
internal sealed class ReplayRandom(byte[] draws) : ISecureRandom
{
    private int _position;

    public int Remaining => draws.Length - _position;

    public void Fill(Span<byte> destination)
    {
        if (destination.Length > Remaining)
            throw new InvalidOperationException($"The vector recorded {draws.Length} random bytes; more were drawn.");
        draws.AsSpan(_position, destination.Length).CopyTo(destination);
        _position += destination.Length;
    }
}
