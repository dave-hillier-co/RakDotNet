namespace RakDotNet.Plugins;

/// <summary>
/// NATTypeDetectionResult (NatTypeDetectionCommon.h:28-50): every NAT type, in RakNet's order, as the byte
/// ID_NAT_TYPE_DETECTION_RESULT carries.
/// </summary>
public enum NatTypeDetectionResult : byte
{
    /// <summary>NAT_TYPE_NONE: works with anyone.</summary>
    None,

    /// <summary>NAT_TYPE_FULL_CONE: accepts any datagram to a port that has been used.</summary>
    FullCone,

    /// <summary>NAT_TYPE_ADDRESS_RESTRICTED: accepts datagrams from an IP address it has sent to.</summary>
    AddressRestricted,

    /// <summary>NAT_TYPE_PORT_RESTRICTED: accepts datagrams from an IP address and port it has sent to.</summary>
    PortRestricted,

    /// <summary>NAT_TYPE_SYMMETRIC: a different external port for every destination.</summary>
    Symmetric,

    /// <summary>NAT_TYPE_UNKNOWN: not determined. NatTypeDetectionClient does not use it.</summary>
    Unknown,

    /// <summary>NAT_TYPE_DETECTION_IN_PROGRESS. NatTypeDetectionClient does not use it.</summary>
    DetectionInProgress,

    /// <summary>NAT_TYPE_SUPPORTS_UPNP: equivalent to <see cref="None"/>. NatTypeDetectionClient does not use it.</summary>
    SupportsUpnp,
}

/// <summary>NatTypeDetectionCommon's helpers (NatTypeDetectionCommon.cpp:19-104).</summary>
public static class NatTypeDetection
{
    /// <summary>NAT_TYPE_COUNT.</summary>
    public const int TypeCount = 8;

    // CanConnect's connectionGraph (NatTypeDetectionCommon.cpp:23-35), rows and columns in enum order.
    private static readonly bool[,] ConnectionGraph =
    {
        { true, true, true, true, true, false, false, true },
        { true, true, true, true, true, false, false, true },
        { true, true, true, true, true, false, false, true },
        { true, true, true, true, false, false, false, true },
        { true, true, true, false, false, false, false, true },
        { false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false },
        { true, true, true, true, true, false, false, true },
    };

    /// <summary>
    /// CanConnect: whether a system behind <paramref name="first"/> can punch through to one behind
    /// <paramref name="second"/>. A symmetric NAT needs address restricted or less on the other side; a port
    /// restricted one needs port restricted or less.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Either value is not a <see cref="NatTypeDetectionResult"/>.</exception>
    public static bool CanConnect(NatTypeDetectionResult first, NatTypeDetectionResult second)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)first, TypeCount, nameof(first));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((int)second, TypeCount, nameof(second));
        return ConnectionGraph[(int)first, (int)second];
    }

    /// <summary>NATTypeDetectionResultToString: the technical name, such as "Port restricted".</summary>
    public static string ToTechnicalString(this NatTypeDetectionResult type) => type switch
    {
        NatTypeDetectionResult.None => "None",
        NatTypeDetectionResult.FullCone => "Full cone",
        NatTypeDetectionResult.AddressRestricted => "Address restricted",
        NatTypeDetectionResult.PortRestricted => "Port restricted",
        NatTypeDetectionResult.Symmetric => "Symmetric",
        NatTypeDetectionResult.Unknown => "Unknown",
        NatTypeDetectionResult.DetectionInProgress => "In Progress",
        NatTypeDetectionResult.SupportsUpnp => "Supports UPNP",
        (NatTypeDetectionResult)TypeCount => "NAT_TYPE_COUNT",
        _ => "Error, unknown enum in NATTypeDetectionResult",
    };

    /// <summary>
    /// NATTypeDetectionResultToStringFriendly: "Open" and "Relaxed" connect to anything, "Moderate" to moderate or
    /// less, "Strict" to relaxed or less.
    /// </summary>
    public static string ToFriendlyString(this NatTypeDetectionResult type) => type switch
    {
        NatTypeDetectionResult.None => "Open",
        NatTypeDetectionResult.FullCone => "Relaxed",
        NatTypeDetectionResult.AddressRestricted => "Relaxed",
        NatTypeDetectionResult.PortRestricted => "Moderate",
        NatTypeDetectionResult.Symmetric => "Strict",
        NatTypeDetectionResult.Unknown => "Unknown",
        NatTypeDetectionResult.DetectionInProgress => "In Progress",
        NatTypeDetectionResult.SupportsUpnp => "Supports UPNP",
        (NatTypeDetectionResult)TypeCount => "NAT_TYPE_COUNT",
        _ => "Error, unknown enum in NATTypeDetectionResult",
    };
}
