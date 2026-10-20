namespace RakDotNet.Plugins.Tests.NatTypeDetection;

public sealed class NatTypeDetectionResultTests
{
    [Fact]
    public void Values_are_positional_in_the_cpp_enum()
    {
        // NatTypeDetectionCommon.h:28-50.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7],
            new[]
            {
                NatTypeDetectionResult.None, NatTypeDetectionResult.FullCone, NatTypeDetectionResult.AddressRestricted,
                NatTypeDetectionResult.PortRestricted, NatTypeDetectionResult.Symmetric, NatTypeDetectionResult.Unknown,
                NatTypeDetectionResult.DetectionInProgress, NatTypeDetectionResult.SupportsUpnp,
            }.Select(v => (int)v));
    }

    [Theory]
    [InlineData(NatTypeDetectionResult.Symmetric, NatTypeDetectionResult.AddressRestricted, true)]
    [InlineData(NatTypeDetectionResult.Symmetric, NatTypeDetectionResult.PortRestricted, false)]
    [InlineData(NatTypeDetectionResult.PortRestricted, NatTypeDetectionResult.Symmetric, false)]
    [InlineData(NatTypeDetectionResult.Symmetric, NatTypeDetectionResult.Symmetric, false)]
    [InlineData(NatTypeDetectionResult.PortRestricted, NatTypeDetectionResult.PortRestricted, true)]
    [InlineData(NatTypeDetectionResult.None, NatTypeDetectionResult.Symmetric, true)]
    [InlineData(NatTypeDetectionResult.SupportsUpnp, NatTypeDetectionResult.Symmetric, true)]
    [InlineData(NatTypeDetectionResult.Unknown, NatTypeDetectionResult.None, false)]
    [InlineData(NatTypeDetectionResult.None, NatTypeDetectionResult.DetectionInProgress, false)]
    public void CanConnect_follows_the_connection_graph(NatTypeDetectionResult first, NatTypeDetectionResult second, bool expected)
    {
        // NatTypeDetectionCommon.cpp:21-37.
        Assert.Equal(expected, Plugins.NatTypeDetection.CanConnect(first, second));
    }

    [Fact]
    public void CanConnect_rejects_a_value_outside_the_enum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plugins.NatTypeDetection.CanConnect((NatTypeDetectionResult)8, NatTypeDetectionResult.None));
    }

    [Theory]
    [InlineData(NatTypeDetectionResult.None, "None", "Open")]
    [InlineData(NatTypeDetectionResult.FullCone, "Full cone", "Relaxed")]
    [InlineData(NatTypeDetectionResult.AddressRestricted, "Address restricted", "Relaxed")]
    [InlineData(NatTypeDetectionResult.PortRestricted, "Port restricted", "Moderate")]
    [InlineData(NatTypeDetectionResult.Symmetric, "Symmetric", "Strict")]
    [InlineData(NatTypeDetectionResult.SupportsUpnp, "Supports UPNP", "Supports UPNP")]
    [InlineData((NatTypeDetectionResult)8, "NAT_TYPE_COUNT", "NAT_TYPE_COUNT")]
    [InlineData((NatTypeDetectionResult)9, "Error, unknown enum in NATTypeDetectionResult", "Error, unknown enum in NATTypeDetectionResult")]
    public void Strings_are_RakNets(NatTypeDetectionResult type, string technical, string friendly)
    {
        // NatTypeDetectionCommon.cpp:39-104.
        Assert.Equal(technical, type.ToTechnicalString());
        Assert.Equal(friendly, type.ToFriendlyString());
    }
}
