using RakDotNet.Protocol.Wire;

namespace RakDotNet.Protocol.Session;

/// <summary>Wraparound-aware comparison of ordering and sequencing indexes.</summary>
internal static class OrderingIndex
{
    private const uint Half = UInt24.Mask / 2;

    /// <summary>
    /// ReliabilityLayer::IsOlderOrderedPacket (ReliabilityLayer.cpp:2901-2922): true when <paramref name="candidate"/>
    /// is in the half of the 24-bit space behind <paramref name="waitingFor"/>.
    /// </summary>
    public static bool IsOlder(UInt24 candidate, UInt24 waitingFor)
    {
        if (waitingFor.Value > Half)
            return candidate.Value >= (waitingFor - Half + 1u).Value && candidate.Value < waitingFor.Value;
        return candidate.Value >= (waitingFor - (Half + 1)).Value || candidate.Value < waitingFor.Value;
    }
}
