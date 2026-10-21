using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Plugins;

/// <summary>
/// NetworkIDManager: hands out the 64-bit NetworkIDs of a group of <see cref="NetworkIdObject"/>s and finds an object by
/// its ID, so systems can name objects to each other. Use one per game instance; ReplicaManager3 and the other plugins
/// that refer to objects over the network take one.
/// </summary>
/// <remarks>
/// <para>
/// Ported from NetworkIDManager.cpp. IDs count up from a random 64-bit start (Get64BitUniqueRandomNumber), skipping IDs
/// in use and <see cref="RakNetConstants.UnassignedNetworkId"/>. Objects are kept in <see cref="HashLength"/> buckets
/// keyed by the ID modulo <see cref="HashLength"/>, each a chain in the order the objects were tracked, so of two objects
/// given the same ID the first is found until it goes, as in a RakNet release build, which asserts on it in debug.
/// </para>
/// <para>Not thread-safe, as in RakNet.</para>
/// </remarks>
public sealed class NetworkIdManager
{
    /// <summary>NETWORK_ID_MANAGER_HASH_LENGTH</summary>
    public const int HashLength = 1024;

    private readonly NetworkIdObject?[] _buckets = new NetworkIdObject?[HashLength];
    private ulong _startingOffset;

    /// <summary>Starts the IDs at a random value from <see cref="RandomSource.Shared"/>.</summary>
    public NetworkIdManager()
        : this(RandomSource.Shared)
    {
    }

    /// <summary>Starts the IDs at the next value of <paramref name="random"/>.</summary>
    public NetworkIdManager(RandomSource random)
    {
        ArgumentNullException.ThrowIfNull(random);
        _startingOffset = random.NextUInt64();
    }

    /// <summary>GET_BASE_OBJECT_FROM_ID: the object with <paramref name="networkId"/>, or null.</summary>
    public NetworkIdObject? GetBaseObject(ulong networkId)
    {
        for (var item = _buckets[BucketOf(networkId)]; item is not null; item = item.NextInBucket)
        {
            if (item.NetworkId == networkId)
                return item;
        }
        return null;
    }

    /// <summary>
    /// GET_OBJECT_FROM_ID: the <see cref="NetworkIdObject.Parent"/> of the object with <paramref name="networkId"/> when it
    /// has one, otherwise the object itself; null when there is none or it is not a <typeparamref name="T"/>, where RakNet
    /// casts without a check.
    /// </summary>
    public T? GetObject<T>(ulong networkId)
        where T : class
    {
        var item = GetBaseObject(networkId);
        if (item is null)
            return null;
        return (item.Parent ?? item) as T;
    }

    /// <summary>Clear: stops tracking every object. The objects keep their manager and ID.</summary>
    public void Clear() => Array.Clear(_buckets);

    // GetNewNetworkID (NetworkIDManager.cpp:48-58).
    internal ulong NewNetworkId()
    {
        while (GetBaseObject(++_startingOffset) is not null)
        {
        }
        if (_startingOffset == RakNetConstants.UnassignedNetworkId)
        {
            while (GetBaseObject(++_startingOffset) is not null)
            {
            }
        }
        return _startingOffset;
    }

    // TrackNetworkIDObject (NetworkIDManager.cpp:64-105): appended to the end of its bucket's chain.
    internal void Track(NetworkIdObject item)
    {
        item.NextInBucket = null;
        var bucket = BucketOf(item.NetworkId);
        if (_buckets[bucket] is not { } last)
        {
            _buckets[bucket] = item;
            return;
        }
        while (last.NextInBucket is { } next)
            last = next;
        last.NextInBucket = item;
    }

    // StopTrackingNetworkIDObject (NetworkIDManager.cpp:106-133): nothing happens for an object that is not tracked.
    internal void StopTracking(NetworkIdObject item)
    {
        var bucket = BucketOf(item.NetworkId);
        var current = _buckets[bucket];
        if (current is null)
            return;
        if (ReferenceEquals(current, item))
        {
            _buckets[bucket] = item.NextInBucket;
            return;
        }
        for (; current is not null; current = current.NextInBucket)
        {
            if (ReferenceEquals(current.NextInBucket, item))
            {
                current.NextInBucket = item.NextInBucket;
                return;
            }
        }
    }

    // NetworkIDToHashIndex (NetworkIDManager.cpp:59-63).
    private static int BucketOf(ulong networkId) => (int)(networkId % HashLength);
}
