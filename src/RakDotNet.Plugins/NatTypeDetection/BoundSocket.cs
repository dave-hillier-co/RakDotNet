using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// CreateNonblockingBoundSocket (NatTypeDetectionCommon.cpp:107-160): a datagram socket on <c>bindAddr</c> and an
/// ephemeral port, bound through the peer's <see cref="RakPeerLowLevel.TransportFactory"/> so it shares the peer's
/// network. RakNet hands what arrives to a polling thread that queues it for <c>Update</c>; here the plugin reads the
/// socket without blocking in <see cref="IRakPlugin.OnUpdate"/>, which is the same point.
/// </summary>
internal static class BoundSocket
{
    /// <summary>The largest datagram read: MAXIMUM_MTU_SIZE, the size of RNS2RecvStruct::data.</summary>
    public const int MaximumMtuSize = 1492;

    public static IDatagramTransport? TryBind(RakPeer peer, IPAddress address)
    {
        var binding = new SocketBinding(new IPEndPoint(address, 0));
        return peer.LowLevel.TransportFactory.TryBind(binding, out var transport, out _, out _) ? transport : null;
    }

    /// <summary>The address part of a socket's bound address, as SystemAddress::ToString(false) prints it.</summary>
    public static IPAddress AddressOf(SystemAddress bound) => bound.ToIPEndPoint().Address;
}
