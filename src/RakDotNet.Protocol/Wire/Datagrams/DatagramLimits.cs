namespace RakDotNet.Protocol.Wire.Datagrams;

/// <summary>Size limits RakNet applies to connected datagrams.</summary>
public static class DatagramLimits
{
    /// <summary>MAXIMUM_MTU_SIZE (MTUSize.h:33). No RakNet datagram is larger.</summary>
    public const int MaximumMtuSize = 1492;

    /// <summary>MINIMUM_MTU_SIZE (MTUSize.h:36).</summary>
    public const int MinimumMtuSize = 400;

    /// <summary>
    /// UDP_HEADER_SIZE (CCRakNetSlidingWindow.h:51): the IPv4 and UDP header bytes RakNet reserves out of the MTU.
    /// </summary>
    public const int UdpHeaderBytes = 28;

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
    /// <param name="mtu">
    /// The negotiated MTU, including the IP and UDP headers (the MTUSize given to ReliabilityLayer::SetMTUSize). The C++
    /// hands the congestion manager <c>MTUSize - UDP_HEADER_SIZE</c> (ReliabilityLayer.cpp:361, CCRakNetSlidingWindow.cpp:49)
    /// and its GetMTU (CCRakNetSlidingWindow.cpp:322-325) is what the header budget is taken from, so the result is
    /// <c>(mtu - 28 - 9) * 8</c>.
    /// </param>
    public static int MaxPayloadBits(int mtu)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(mtu, UdpHeaderBytes + HeaderBudgetBytes);
        return (mtu - UdpHeaderBytes - HeaderBudgetBytes) * 8;
    }
}
