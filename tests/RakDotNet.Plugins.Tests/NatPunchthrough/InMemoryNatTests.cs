using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.NatPunchthrough;

public sealed class InMemoryNatTests
{
    [Fact]
    public void A_symmetric_router_maps_each_destination_past_the_ports_punchthrough_predicts()
    {
        using var mesh = new NatMesh();
        var nat = mesh.Nat("203.0.113.1", NatBehaviour.Symmetric);
        Assert.True(nat.TryBind(new SocketBinding(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 0)), out var inside, out _, out _));

        inside.TrySend([1], new SystemAddress(IPAddress.Parse("198.51.100.1"), 1000));
        inside.TrySend([2], new SystemAddress(IPAddress.Parse("198.51.100.2"), 1000));

        // NatPunchthroughClient tries the reported port and the next MAX_PREDICTIVE_PORT_RANGE (2) ports.
        var ports = nat.MappedAddresses.Select(a => a.Port).ToArray();
        Assert.Equal(2, ports.Length);
        Assert.True(ports[1] - ports[0] > 2, $"mapped to ports {ports[0]} and {ports[1]}");
    }
}
