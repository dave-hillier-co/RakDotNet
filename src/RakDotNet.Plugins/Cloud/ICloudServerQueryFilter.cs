using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// CloudServerQueryFilter (CloudServer.h): restricts what clients connected to a <see cref="CloudServer"/> may do. Every
/// filter added with <see cref="CloudServer.AddQueryFilter"/> is asked in turn; the first that returns false rejects the
/// request silently.
/// </summary>
/// <remarks>
/// Filters run on the dispatch step, inside the server's lock, and see requests only from systems that sent them to this
/// server, never requests forwarded by other servers. As in RakNet, the lists and the query are the ones the server goes
/// on to use, so a filter may change them.
/// </remarks>
public interface ICloudServerQueryFilter
{
    /// <summary>OnPostRequest: a client posting <paramref name="data"/> under <paramref name="key"/>.</summary>
    bool OnPostRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudKey key, ReadOnlySpan<byte> data);

    /// <summary>OnReleaseRequest: a client releasing data it posted.</summary>
    bool OnReleaseRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys);

    /// <summary>OnGetRequest: a client querying, and with <see cref="CloudQuery.SubscribeToResults"/> subscribing. A rejected query gets no response.</summary>
    bool OnGetRequest(RakNetGuid clientGuid, SystemAddress clientAddress, CloudQuery query, List<RakNetGuid> specificSystems);

    /// <summary>OnUnsubscribeRequest: a client unsubscribing. A rejected client keeps getting notifications.</summary>
    bool OnUnsubscribeRequest(RakNetGuid clientGuid, SystemAddress clientAddress, List<CloudKey> keys, List<RakNetGuid> specificSystems);
}
