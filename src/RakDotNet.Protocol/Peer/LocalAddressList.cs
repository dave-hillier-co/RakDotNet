using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Protocol.Peer;

/// <summary>
/// Builds ipList, the peer's own addresses that ID_CONNECTION_REQUEST_ACCEPTED and ID_NEW_INCOMING_CONNECTION carry.
/// </summary>
internal static class LocalAddressList
{
    /// <summary>
    /// The list exactly as RakPeer fills it: the first <see cref="RakNetConstants.MaximumNumberOfInternalIds"/> IPv4
    /// host addresses (GetMyIP_Windows_Linux_IPV4, RakNetSocket2_Windows_Linux.cpp:70-103, as RAKNET_SUPPORT_IPV6 is 0),
    /// sorted by FillIPList (RakPeer.cpp:6463-6493), then given the bound port (RakPeer.cpp:585-598), padded with
    /// unassigned entries.
    /// </summary>
    /// <param name="hostAddresses">The host's addresses in resolver order (gethostbyname of the host name).</param>
    /// <param name="boundPort">The port the peer's socket is bound to.</param>
    public static IReadOnlyList<IPEndPoint> Build(IEnumerable<IPAddress> hostAddresses, ushort boundPort)
    {
        ArgumentNullException.ThrowIfNull(hostAddresses);
        const int count = RakNetConstants.MaximumNumberOfInternalIds;
        var addresses = hostAddresses
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any))
            .Take(count)
            .ToList();

        // The C++ selection sort compares each candidate with the starting entry rather than the lowest found so far,
        // and never looks at the last slot; both quirks are kept so the list matches byte for byte.
        for (var start = 0; start < count - 1 && start < addresses.Count; start++)
        {
            var lowest = start;
            for (var current = start + 1; current < count - 1 && current < addresses.Count; current++)
            {
                if (SortKey(addresses[current]) < SortKey(addresses[start]))
                    lowest = current;
            }
            (addresses[start], addresses[lowest]) = (addresses[lowest], addresses[start]);
        }

        return InternalAddressList.Pad(addresses.Select(a => new IPEndPoint(a, boundPort)), count);
    }

    // SystemAddress::operator< (RakNetTypes.cpp:177-191) with equal ports compares sin_addr.s_addr, the network-order
    // bytes read as a host-order integer; every supported platform is little-endian.
    private static uint SortKey(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
