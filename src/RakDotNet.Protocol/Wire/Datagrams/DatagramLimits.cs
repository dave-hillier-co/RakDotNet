namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>Size limits RakNet applies to connected datagrams.</summary>
public static class DatagramLimits
{
    /// <summary>MAXIMUM_MTU_SIZE (MTUSize.h:33). No RakNet datagram is larger.</summary>
    public const int MaximumMtuSize = 1492;

    /// <summary>MINIMUM_MTU_SIZE (MTUSize.h:36).</summary>
    public const int MinimumMtuSize = 400;

    /// <summary>
    /// The datagram header size RakNet budgets for when filling a datagram: DatagramHeaderFormat::GetDataHeaderByteLength
    /// (ReliabilityLayer.cpp:133-141), 2 + 3 + sizeof(float) without the timestamp of the default build. It is larger
    /// than any header actually written (at most 5 bytes); the C++ uses it only as a budget.
    /// </summary>
    public const int HeaderBudgetBytes = 2 + 3 + sizeof(float);

    /// <summary>
    /// ReliabilityLayer::GetMaxDatagramSizeExcludingMessageHeaderBits (ReliabilityLayer.cpp:3909-3924) without security:
    /// the bit budget passed to range list serialisation and used when packing messages.
    /// </summary>
    public static int MaxPayloadBits(int mtu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mtu, HeaderBudgetBytes);
        return (mtu - HeaderBudgetBytes) * 8;
    }
}
