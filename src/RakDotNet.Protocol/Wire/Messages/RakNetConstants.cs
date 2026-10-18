namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>Protocol constants of stock RakNet 4.081, with the C++ definition each comes from.</summary>
public static class RakNetConstants
{
    /// <summary>RAKNET_PROTOCOL_VERSION, RakNetVersion.h:19.</summary>
    public const byte ProtocolVersion = 6;

    /// <summary>UDP_HEADER_SIZE (IPv4 20 + UDP 8), CCRakNetSlidingWindow.h:51.</summary>
    public const int UdpHeaderSize = 28;

    /// <summary>MAXIMUM_MTU_SIZE, MTUSize.h:33. The server caps the MTU it offers to this value (RakPeer.cpp:5183).</summary>
    public const int MaximumMtuSize = 1492;

    /// <summary>MTU sizes tried in order during connection, RakPeer.cpp:104.</summary>
    public static ReadOnlySpan<ushort> MtuSizes => [MaximumMtuSize, 1200, 576];

    /// <summary>MAX_OFFLINE_DATA_LENGTH, RakPeer.cpp:127. Bounds offline ping responses and out-of-band data.</summary>
    public const int MaxOfflineDataLength = 400;

    /// <summary>MAXIMUM_NUMBER_OF_INTERNAL_IDS, RakNetDefines.h:76. Overridable at C++ build time.</summary>
    public const int MaximumNumberOfInternalIds = 10;

    /// <summary>UNASSIGNED_RAKNET_GUID, RakNetTypes.h:364.</summary>
    public const ulong UnassignedGuid = ulong.MaxValue;
}
