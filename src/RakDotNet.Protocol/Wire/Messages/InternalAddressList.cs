using System.Net;

namespace RakDotNet.Protocol.Wire.Messages;

/// <summary>
/// The fixed-length list of a peer's own addresses carried by ID_CONNECTION_REQUEST_ACCEPTED and
/// ID_NEW_INCOMING_CONNECTION (RakPeer.cpp:3548-3549, 6196-6197). Its length is
/// MAXIMUM_NUMBER_OF_INTERNAL_IDS, a C++ build-time constant, so readers take the count as a parameter.
/// </summary>
public static class InternalAddressList
{
    /// <summary>UNASSIGNED_SYSTEM_ADDRESS: 0.0.0.0:0 (RakNetTypes.cpp:378-385), used for unused list entries (RakPeer.cpp:232).</summary>
    public static IPEndPoint Unassigned => new(IPAddress.Any, 0);

    /// <summary>A list of <paramref name="count"/> unassigned addresses.</summary>
    public static IReadOnlyList<IPEndPoint> CreateUnassigned(int count = RakNetConstants.MaximumNumberOfInternalIds) =>
        Pad([], count);

    /// <summary>
    /// Pads <paramref name="addresses"/> with unassigned entries to <paramref name="count"/> entries, as the C++ ipList is.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> Pad(
        IEnumerable<IPEndPoint> addresses,
        int count = RakNetConstants.MaximumNumberOfInternalIds
    )
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var list = addresses.ToList();
        if (list.Count > count)
            throw new ArgumentException($"{list.Count} addresses do not fit a list of {count}.", nameof(addresses));
        while (list.Count < count)
            list.Add(Unassigned);
        return list;
    }

    internal static void Write(ref BitStreamWriter writer, IReadOnlyList<IPEndPoint> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        foreach (var address in addresses)
            writer.WriteAddress(address);
    }

    internal static bool TryRead(ref BitStreamReader reader, int count, out IPEndPoint[] addresses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        // Each IPv4 address is 7 bytes; check up front so a hostile count cannot allocate more than the input holds.
        if (reader.BitsRemaining < count * 7L * 8)
        {
            addresses = [];
            return false;
        }
        addresses = new IPEndPoint[count];
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadAddress(out var address))
            {
                addresses = [];
                return false;
            }
            addresses[i] = address!;
        }
        return true;
    }
}
