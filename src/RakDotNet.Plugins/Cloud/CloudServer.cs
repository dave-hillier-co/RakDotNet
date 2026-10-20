using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// CloudServer: stores what connected <see cref="CloudClient"/>s post, answers their queries and subscriptions, and
/// shares them with other servers added with <see cref="AddServer"/>, which forward queries to each other and relay
/// changes to keys their clients subscribe to.
/// </summary>
/// <remarks>
/// <para>
/// Ported from CloudServer.cpp, keeping its bookkeeping and quirks so a mixed cloud of native and C# servers answers
/// alike:
/// </para>
/// <list type="bullet">
/// <item>Every message is HIGH_PRIORITY, RELIABLE_ORDERED on channel 0. Server-to-server messages are
/// ID_CLOUD_SERVER_TO_SERVER_COMMAND followed by a command byte (CloudServer.cpp:20-30).</item>
/// <item>A query for keys another server may hold is forwarded to it, and answered when every such server has
/// responded or disconnected. The 3 s timeout in RakNet's Update never fires, because it reads its clock as
/// RakNet::Time(), which is 0 (CloudServer.cpp:162-186), so it is left out. Pending queries are found with RakNet's
/// comparison, which never says a key is greater (CloudServer.cpp:82-89), so with three or more pending, a response may
/// not find its query.</item>
/// <item>A server that has not sent its keys yet is asked every query and told every change.</item>
/// <item>Local rows come first, by key then by client GUID, then rows from other servers in GUID order;
/// <see cref="CloudQuery.StartingRowIndex"/> applies even with no row limit. When rows from other servers are added the
/// row count is patched into place as RakNet patches it, writing over the bits already there
/// (CloudServer.cpp:1238-1244).</item>
/// <item>A subscription to specific systems adds an empty row for each system that has not posted the key, which a
/// later query returns once anyone has posted it, until the subscriber disconnects.</item>
/// <item>The address of the server in a row is what the client sees it as, with a loopback address replaced by
/// <see cref="RakPeer.InternalAddresses"/>[0] for new rows, or <see cref="ForcedExternalAddress"/> with that port;
/// a port of 0 becomes the bound port.</item>
/// <item><see cref="MaxBytesPerDownload"/> is stored but, as in RakNet, never applied.</item>
/// </list>
/// <para>
/// Malformed requests are dropped. A post whose data is shorter than its stated length is dropped before the filters,
/// where RakNet stores uninitialised bytes; a release of a key whose data RakNet lost track of after an upload over the
/// limit forgets the key, where RakNet reads past its list.
/// </para>
/// <para>
/// So that a client cannot exhaust the server's memory, which RakNet allows: a get that would take a client past
/// <see cref="MaxSubscriptionsPerClient"/> is dropped, and when a client disconnects the empty rows its specific
/// subscriptions added, and keys left with nothing, are released, where RakNet keeps them (CloudServer.cpp:957-991).
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class CloudServer : IRakPlugin
{
    // ServerToServerCommands (CloudServer.cpp:20-30).
    private const byte ProcessGetRequest = 0;
    private const byte ProcessGetResponse = 1;
    private const byte AddUploadedAndSubscribedKeys = 2;
    private const byte AddUploadedKey = 3;
    private const byte AddSubscribedKey = 4;
    private const byte RemoveUploadedKey = 5;
    private const byte RemoveSubscribedKey = 6;
    private const byte DataChanged = 7;

    private readonly Lock _gate = new();
    private readonly List<CloudDataList> _dataRepository = [];
    private readonly Dictionary<RakNetGuid, RemoteCloudClient> _remoteSystems = [];
    private readonly List<RemoteServer> _remoteServers = [];
    private readonly List<GetRequest> _getRequests = [];
    private readonly List<ICloudServerQueryFilter> _queryFilters = [];
    private uint _nextGetRequestId;
    private IPluginHost? _host;

    /// <summary>
    /// SetMaxUploadBytesPerClient: the most a client may have posted at once; posts beyond it are dropped silently.
    /// 0 (the default) for no limit.
    /// </summary>
    public ulong MaxUploadBytesPerClient
    {
        get { lock (_gate) return field; }
        set { lock (_gate) field = value; }
    }

    /// <summary>The default <see cref="MaxSubscriptionsPerClient"/>.</summary>
    public const int DefaultMaxSubscriptionsPerClient = 4096;

    /// <summary>
    /// The most subscriptions a client may hold at once, counting a key subscribed to specific systems once per system
    /// and a key subscribed to every system once. A get that subscribes past it is dropped. 0 for no limit. RakNet has
    /// no such limit, so one small request could make it allocate a row for every key and system named.
    /// </summary>
    public int MaxSubscriptionsPerClient
    {
        get { lock (_gate) return field; }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            lock (_gate) field = value;
        }
    } = DefaultMaxSubscriptionsPerClient;

    /// <summary>SetMaxBytesPerDownload: stored only; RakNet never applies it.</summary>
    public ulong MaxBytesPerDownload
    {
        get { lock (_gate) return field; }
        set { lock (_gate) field = value; }
    }

    /// <summary>
    /// ForceExternalSystemAddress: the address rows report for this server, with the port the client sees, instead of
    /// what the client sees; <see cref="SystemAddress.Unassigned"/> (the default) for the latter. Applies to later posts.
    /// </summary>
    public SystemAddress ForcedExternalAddress
    {
        get { lock (_gate) return field; }
        set { lock (_gate) field = value; }
    }

    /// <summary>GetRemoteServers: the servers added with <see cref="AddServer"/>, in GUID order.</summary>
    public IReadOnlyList<RakNetGuid> RemoteServers
    {
        get
        {
            lock (_gate)
                return [.. _remoteServers.Select(s => s.Guid)];
        }
    }

    private IPluginHost Host => _host ?? throw new InvalidOperationException("The CloudServer is not attached to a peer.");

    /// <summary>
    /// AddServer (CloudServer.cpp:1128-1144): treats the connected system as another server of the cloud, which must also
    /// add this one, and sends it the keys posted and subscribed to here. Ignored for a system that is not connected.
    /// </summary>
    /// <exception cref="InvalidOperationException">The plugin is not attached to a peer.</exception>
    public void AddServer(RakNetGuid server)
    {
        lock (_gate)
        {
            var host = Host;
            var state = host.Peer?.GetConnectionState(server) ?? ConnectionState.NotConnected;
            if (state is ConnectionState.Disconnected or ConnectionState.NotConnected)
                return;
            var index = IndexOf(_remoteServers, s => server.CompareTo(s.Guid), out var exists);
            if (exists)
                return;
            _remoteServers.Insert(index, new RemoteServer(server));
            SendUploadedAndSubscribedKeysToServer(host, server);
        }
    }

    /// <summary>RemoveServer (CloudServer.cpp:1145-1154).</summary>
    public void RemoveServer(RakNetGuid server)
    {
        lock (_gate)
        {
            var index = IndexOf(_remoteServers, s => server.CompareTo(s.Guid), out var exists);
            if (exists)
                _remoteServers.RemoveAt(index);
        }
    }

    /// <summary>Clear (CloudServer.cpp:1002-1044): forgets every key, client, server and pending query; filters stay.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _dataRepository.Clear();
            _remoteServers.Clear();
            _getRequests.Clear();
            _remoteSystems.Clear();
        }
    }

    /// <summary>AddQueryFilter: ignored for a filter already added.</summary>
    public void AddQueryFilter(ICloudServerQueryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_gate)
        {
            if (!_queryFilters.Contains(filter))
                _queryFilters.Add(filter);
        }
    }

    /// <summary>RemoveQueryFilter</summary>
    public void RemoveQueryFilter(ICloudServerQueryFilter filter)
    {
        lock (_gate)
            _queryFilters.Remove(filter);
    }

    /// <summary>RemoveAllQueryFilters</summary>
    public void RemoveAllQueryFilters()
    {
        lock (_gate)
            _queryFilters.Clear();
    }

    void IRakPlugin.OnAttached(IPluginHost host) => _host = host;

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>OnRakPeerShutdown: Clear.</summary>
    void IRakPlugin.OnShutdown() => Clear();

    /// <summary>OnReceive (CloudServer.cpp:188-238).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty || _host is not { } host)
            return PluginResult.Continue;
        var sender = e.RakNetGuid;
        var address = e.Address;
        lock (_gate)
        {
            switch ((MessageId)data[0])
            {
                case MessageId.CloudPostRequest:
                    OnPostRequest(host, data, sender, address);
                    return PluginResult.Consume;
                case MessageId.CloudReleaseRequest:
                    OnReleaseRequest(host, data, sender, address);
                    return PluginResult.Consume;
                case MessageId.CloudGetRequest:
                    OnGetRequest(host, data, sender, address);
                    return PluginResult.Consume;
                case MessageId.CloudUnsubscribeRequest:
                    OnUnsubscribeRequest(host, data, sender, address);
                    return PluginResult.Consume;
                case MessageId.CloudServerToServerCommand:
                    if (data.Length > 1)
                    {
                        switch (data[1])
                        {
                            case ProcessGetRequest:
                                OnServerToServerGetRequest(host, data, sender);
                                break;
                            case ProcessGetResponse:
                                OnServerToServerGetResponse(host, data, sender);
                                break;
                            case AddUploadedAndSubscribedKeys:
                                OnSendUploadedAndSubscribedKeysToServer(data, sender);
                                break;
                            case AddUploadedKey:
                                OnServerKeyCommand(data, sender, uploaded: true, add: true);
                                break;
                            case AddSubscribedKey:
                                OnServerKeyCommand(data, sender, uploaded: false, add: true);
                                break;
                            case RemoveUploadedKey:
                                OnServerKeyCommand(data, sender, uploaded: true, add: false);
                                break;
                            case RemoveSubscribedKey:
                                OnServerKeyCommand(data, sender, uploaded: false, add: false);
                                break;
                            case DataChanged:
                                OnServerDataChanged(host, data, sender);
                                break;
                        }
                    }
                    return PluginResult.Consume;
                default:
                    return PluginResult.Continue;
            }
        }
    }

    /// <summary>OnClosedConnection (CloudServer.cpp:865-997).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        if (_host is not { } host)
            return;
        var guid = connection.RakNetGuid;
        lock (_gate)
        {
            var serverIndex = IndexOf(_remoteServers, s => guid.CompareTo(s.Guid), out var isServer);
            if (isServer)
            {
                // Answer the queries that were waiting only for this server.
                var requestIndex = 0;
                while (requestIndex < _getRequests.Count)
                {
                    var request = _getRequests[requestIndex];
                    var responseIndex = IndexOf(request.RemoteServerResponses, r => guid.CompareTo(r.Server), out var waiting);
                    if (waiting)
                    {
                        request.RemoteServerResponses.RemoveAt(responseIndex);
                        if (request.AllRemoteServersHaveResponded)
                        {
                            ProcessAndTransmitGetRequest(host, request);
                            _getRequests.RemoveAt(requestIndex);
                        }
                        else
                        {
                            requestIndex++;
                        }
                    }
                    else
                    {
                        requestIndex++;
                    }
                }
                _remoteServers.RemoveAt(serverIndex);
            }

            if (!_remoteSystems.TryGetValue(guid, out var client))
                return;
            foreach (var uploadedKey in client.UploadedKeys)
            {
                var repositoryIndex = IndexOf(_dataRepository, l => uploadedKey.CompareTo(l.Key), out var listExists);
                if (!listExists)
                    continue;
                var list = _dataRepository[repositoryIndex];
                var keyDataIndex = IndexOf(list.KeyData, d => guid.CompareTo(d.ClientGuid), out var keyDataExists);
                if (!keyDataExists)
                    continue;
                var cloudData = list.KeyData[keyDataIndex];
                list.UploaderCount--;
                NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, cloudData.SpecificSubscribers, false);
                NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, list.NonSpecificSubscribers, false);
                NotifyServerSubscribersOfDataChange(host, cloudData, list.Key, false);
                cloudData.Clear();
                if (!cloudData.IsUnused)
                    continue;
                list.KeyData.RemoveAt(keyDataIndex);
                if (list.IsNotUploaded)
                    SendKeyToServers(host, RemoveUploadedKey, list.Key);
                if (list.IsUnused)
                {
                    // RakNet tells the servers a second time here (CloudServer.cpp:946-948).
                    SendKeyToServers(host, RemoveUploadedKey, list.Key);
                    _dataRepository.RemoveAt(repositoryIndex);
                }
            }

            foreach (var subscription in client.SubscribedKeys)
            {
                var repositoryIndex = IndexOf(_dataRepository, l => subscription.Key.CompareTo(l.Key), out var listExists);
                if (!listExists)
                    continue;
                var list = _dataRepository[repositoryIndex];
                if (subscription.SpecificSystems.Count == 0)
                {
                    Remove(list.NonSpecificSubscribers, guid);
                    list.SubscriberCount--;
                }
                else
                {
                    foreach (var system in subscription.SpecificSystems)
                    {
                        var keyDataIndex = IndexOf(list.KeyData, d => system.CompareTo(d.ClientGuid), out var keyDataExists);
                        if (!keyDataExists)
                            continue;
                        var cloudData = list.KeyData[keyDataIndex];
                        Remove(cloudData.SpecificSubscribers, guid);
                        list.SubscriberCount--;
                        // RakNet keeps the row once unused (CloudServer.cpp:977-983); it is released, as
                        // RemoveSpecificSubscriber releases it, so a client cannot grow the server by subscribing and leaving.
                        if (cloudData.IsUnused)
                            list.KeyData.RemoveAt(keyDataIndex);
                    }
                }
                if (list.IsUnused)
                    _dataRepository.RemoveAt(repositoryIndex);
            }
            _remoteSystems.Remove(guid);
        }
    }

    /// <summary>OnPostRequest (CloudServer.cpp:239-436).</summary>
    private void OnPostRequest(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender, SystemAddress address)
    {
        var reader = new BitStreamReader(packet[1..]);
        if (!CloudKey.TryRead(ref reader, out var key) || !reader.TryReadUInt32(out var dataLength))
            return;
        if (MaxUploadBytesPerClient > 0 && dataLength > MaxUploadBytesPerClient)
            return;
        reader.AlignToByte();
        ReadOnlySpan<byte> data = default;
        if (dataLength > 0 && (dataLength > int.MaxValue || !reader.TryReadAlignedBytes((int)dataLength, out data)))
            return;
        foreach (var filter in _queryFilters)
        {
            if (!filter.OnPostRequest(sender, address, key, data))
                return;
        }

        // Add this system to remoteSystems if it is not there already.
        if (!_remoteSystems.TryGetValue(sender, out var client))
        {
            client = new RemoteCloudClient();
            Insert(client.UploadedKeys, key);
            _remoteSystems.Add(sender, client);
        }
        else
        {
            Insert(client.UploadedKeys, key);
        }

        var list = GetOrAllocateCloudDataList(key, out var repositoryExists, out var repositoryIndex);
        bool alreadyUploaded;
        if (!repositoryExists)
        {
            list.UploaderCount = 1;
            alreadyUploaded = false;
        }
        else
        {
            alreadyUploaded = list.UploaderCount > 0;
            list.UploaderCount++;
        }

        CloudData cloudData;
        var maxUpload = MaxUploadBytesPerClient;
        var keyDataIndex = IndexOf(list.KeyData, d => sender.CompareTo(d.ClientGuid), out var keyDataExists);
        if (!keyDataExists)
        {
            if (maxUpload > 0 && client.UploadedBytes + dataLength > maxUpload)
            {
                // RakNet's undo removes the key's list even when other clients had posted to it (CloudServer.cpp:315-320).
                _dataRepository.RemoveAt(repositoryIndex);
                if (client.IsUnused)
                    _remoteSystems.Remove(sender);
                return;
            }

            var serverAddress = ExternalAddressOrForced(host, address);
            if (!ForcedExternalAddress.IsAssigned && serverAddress.IsLoopback)
                serverAddress = LocalAddressWithPort(host, serverAddress.Port);
            cloudData = new CloudData
            {
                IsUploaded = true,
                ServerSystemAddress = FixPort(host, serverAddress),
                ClientSystemAddress = address,
                ServerGuid = host.RakNetGuid,
                ClientGuid = sender,
            };
            list.KeyData.Insert(keyDataIndex, cloudData);
        }
        else
        {
            cloudData = list.KeyData[keyDataIndex];
            if (!alreadyUploaded)
            {
                cloudData.ServerSystemAddress = FixPort(host, ExternalAddressOrForced(host, address));
                cloudData.ClientSystemAddress = address;
            }

            if (maxUpload > 0 && client.UploadedBytes - (ulong)cloudData.Data.Length + dataLength > maxUpload)
            {
                if (!repositoryExists)
                    _dataRepository.RemoveAt(repositoryIndex);
                return;
            }
            // Subtract the bytes being overwritten.
            client.UploadedBytes -= (ulong)cloudData.Data.Length;
        }

        cloudData.Data = data.ToArray();
        client.UploadedBytes += dataLength;

        if (!alreadyUploaded)
            SendKeyToServers(host, AddUploadedKey, list.Key);
        NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, cloudData.SpecificSubscribers, true);
        NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, list.NonSpecificSubscribers, true);
        NotifyServerSubscribersOfDataChange(host, cloudData, list.Key, true);
    }

    /// <summary>OnReleaseRequest (CloudServer.cpp:437-527).</summary>
    private void OnReleaseRequest(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender, SystemAddress address)
    {
        var reader = new BitStreamReader(packet[1..]);
        if (!reader.TryReadUInt16(out var keyCount) || keyCount == 0)
            return;
        if (!_remoteSystems.TryGetValue(sender, out var client))
            return;
        if (!TryReadKeys(ref reader, keyCount, out var keys))
            return;
        foreach (var filter in _queryFilters)
        {
            if (!filter.OnReleaseRequest(sender, address, keys))
                return;
        }

        for (var i = 0; i < keyCount && i < keys.Count; i++)
        {
            var key = keys[i];
            var uploadedIndex = IndexOf(client.UploadedKeys, k => key.CompareTo(k), out var uploaded);
            if (!uploaded)
                continue;
            var repositoryIndex = IndexOf(_dataRepository, l => key.CompareTo(l.Key), out var listExists);
            var keyDataIndex = 0;
            var keyDataExists = false;
            if (listExists)
                keyDataIndex = IndexOf(_dataRepository[repositoryIndex].KeyData, d => sender.CompareTo(d.ClientGuid), out keyDataExists);
            if (!keyDataExists)
            {
                // Lost by RakNet's undo of an upload over the limit; RakNet would read past its lists here.
                client.UploadedKeys.RemoveAt(uploadedIndex);
                continue;
            }

            var list = _dataRepository[repositoryIndex];
            var cloudData = list.KeyData[keyDataIndex];
            client.UploadedKeys.RemoveAt(uploadedIndex);
            client.UploadedBytes -= (ulong)cloudData.Data.Length;
            list.UploaderCount--;

            // Broadcast the release of this key to subscribers.
            NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, cloudData.SpecificSubscribers, false);
            NotifyClientSubscribersOfDataChange(host, cloudData, list.Key, list.NonSpecificSubscribers, false);
            NotifyServerSubscribersOfDataChange(host, cloudData, list.Key, false);

            cloudData.Clear();
            if (cloudData.IsUnused)
            {
                list.KeyData.RemoveAt(keyDataIndex);
                // Tell other servers this key is no longer uploaded here, so they do not query it.
                if (list.IsNotUploaded)
                    SendKeyToServers(host, RemoveUploadedKey, list.Key);
                if (list.IsUnused)
                    _dataRepository.RemoveAt(repositoryIndex);
            }

            if (client.IsUnused)
            {
                _remoteSystems.Remove(sender);
                break;
            }
        }
    }

    /// <summary>OnGetRequest (CloudServer.cpp:528-718).</summary>
    private void OnGetRequest(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender, SystemAddress address)
    {
        var reader = new BitStreamReader(packet[1..]);
        if (!TryReadQueryWithAddresses(ref reader, out var query, out var specificSystems))
            return;
        if (query.Keys.Count == 0)
            return;
        foreach (var filter in _queryFilters)
        {
            if (!filter.OnGetRequest(sender, address, query, specificSystems))
                return;
        }
        if (query.SubscribeToResults && WouldExceedSubscriptionLimit(sender, query.Keys, specificSystems))
            return;

        var request = new GetRequest(query, specificSystems, sender, host.GetTime(), _nextGetRequestId++);

        // Send the request to the servers that have this data.
        var serversWithData = GetServersWithUploadedKeys(query.Keys);
        if (serversWithData.Count == 0)
        {
            ProcessAndTransmitGetRequest(host, request);
        }
        else
        {
            using var stream = new BitStream();
            stream.WriteByte((byte)MessageId.CloudServerToServerCommand);
            stream.WriteByte(ProcessGetRequest);
            WriteQueryWithAddresses(stream, query, specificSystems);
            stream.WriteUInt32(request.RequestId);
            foreach (var server in serversWithData)
            {
                var guid = server.Guid;
                var index = IndexOf(request.RemoteServerResponses, r => guid.CompareTo(r.Server), out var exists);
                if (!exists)
                    request.RemoteServerResponses.Insert(index, new BufferedGetResponse(guid));
                Send(host, stream, guid);
            }
            // Record that this system made this request.
            var requestIndex = IndexOf(_getRequests, r => GetRequestComp(request.RequestId, r), out var duplicate);
            if (!duplicate)
                _getRequests.Insert(requestIndex, request);
        }

        if (query.SubscribeToResults)
            Subscribe(host, request, sender);
    }

    // Whether subscribing to these keys would take the client past MaxSubscriptionsPerClient. Subscribing to a key the
    // client already holds replaces that subscription, so its count is taken out first.
    private bool WouldExceedSubscriptionLimit(RakNetGuid sender, List<CloudKey> keys, List<RakNetGuid> specificSystems)
    {
        var limit = MaxSubscriptionsPerClient;
        if (limit == 0)
            return false;
        var distinctKeys = keys.ToHashSet();
        long total = (long)distinctKeys.Count * SubscriptionCount(specificSystems.Distinct().Count());
        if (_remoteSystems.TryGetValue(sender, out var client))
        {
            foreach (var subscription in client.SubscribedKeys)
            {
                if (!distinctKeys.Contains(subscription.Key))
                    total += SubscriptionCount(subscription.SpecificSystems.Count);
            }
        }
        return total > limit;
    }

    private static int SubscriptionCount(int specificSystemCount) => Math.Max(1, specificSystemCount);

    // The subscription half of OnGetRequest (CloudServer.cpp:594-716).
    private void Subscribe(IPluginHost host, GetRequest request, RakNetGuid sender)
    {
        if (!_remoteSystems.TryGetValue(sender, out var client))
        {
            client = new RemoteCloudClient();
            _remoteSystems.Add(sender, client);
        }

        var keys = request.Query.Keys;
        for (var keyIndex = 0; keyIndex < keys.Count; keyIndex++)
        {
            var key = keys[keyIndex];
            var subscriberIndex = IndexOf(client.SubscribedKeys, s => key.CompareTo(s.Key), out var hasSubscription);
            if (hasSubscription)
                UnsubscribeFromKey(host, client, sender, subscriberIndex, key, []);

            var subscription = new KeySubscription(key);
            foreach (var system in request.SpecificSystems)
                Insert(subscription.SpecificSystems, system);
            // At the index found before unsubscribing, as RakNet inserts it (CloudServer.cpp:634).
            client.SubscribedKeys.Insert(subscriberIndex, subscription);

            var list = GetOrAllocateCloudDataList(key, out _, out _);
            // The first local subscriber to the key tells the other servers.
            if (list.SubscriberCount == 0)
                SendKeyToServers(host, AddSubscribedKey, key);

            if (request.SpecificSystems.Count > 0)
            {
                foreach (var system in request.SpecificSystems)
                {
                    var keyDataIndex = IndexOf(list.KeyData, d => system.CompareTo(d.ClientGuid), out var keyDataExists);
                    CloudData cloudData;
                    if (!keyDataExists)
                    {
                        cloudData = new CloudData
                        {
                            ServerSystemAddress = SystemAddress.Unassigned,
                            ClientSystemAddress = SystemAddress.Unassigned,
                            ServerGuid = host.RakNetGuid,
                            ClientGuid = system,
                        };
                        list.KeyData.Insert(keyDataIndex, cloudData);
                    }
                    else
                    {
                        cloudData = list.KeyData[keyDataIndex];
                    }
                    list.SubscriberCount++;
                    Insert(cloudData.SpecificSubscribers, sender);
                }
            }
            else
            {
                list.SubscriberCount++;
                Insert(list.NonSpecificSubscribers, sender);

                // Remove the client from the specific subscribers of every row of the key.
                var listKey = list.Key;
                var index = IndexOf(client.SubscribedKeys, s => listKey.CompareTo(s.Key), out var exists);
                if (exists)
                {
                    foreach (var system in client.SubscribedKeys[index].SpecificSystems)
                    {
                        var keyDataIndex = IndexOf(list.KeyData, d => system.CompareTo(d.ClientGuid), out var keyDataExists);
                        if (!keyDataExists)
                            continue;
                        Remove(list.KeyData[keyDataIndex].SpecificSubscribers, sender);
                        list.SubscriberCount--;
                    }
                }
            }
        }

        if (client.SubscribedKeys.Count == 0)
            _remoteSystems.Remove(sender);
    }

    /// <summary>OnUnsubscribeRequest (CloudServer.cpp:719-786).</summary>
    private void OnUnsubscribeRequest(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender, SystemAddress address)
    {
        if (!_remoteSystems.TryGetValue(sender, out var client))
            return;
        var reader = new BitStreamReader(packet[1..]);
        if (!reader.TryReadUInt16(out var keyCount) || !TryReadKeys(ref reader, keyCount, out var keys))
            return;
        if (!reader.TryReadUInt16(out var systemCount) || !TryReadGuids(ref reader, systemCount, out var specificSystems))
            return;
        foreach (var filter in _queryFilters)
        {
            if (!filter.OnUnsubscribeRequest(sender, address, keys, specificSystems))
                return;
        }

        for (var i = 0; i < keyCount && i < keys.Count; i++)
        {
            var key = keys[i];
            IndexOf(_dataRepository, l => key.CompareTo(l.Key), out var listExists);
            if (!listExists)
                continue;
            var subscriberIndex = IndexOf(client.SubscribedKeys, s => key.CompareTo(s.Key), out var hasSubscription);
            if (!hasSubscription)
                continue;
            UnsubscribeFromKey(host, client, sender, subscriberIndex, key, specificSystems);
        }

        if (client.IsUnused)
            _remoteSystems.Remove(sender);
    }

    /// <summary>OnServerToServerGetRequest (CloudServer.cpp:787-814).</summary>
    private void OnServerToServerGetRequest(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender)
    {
        IndexOf(_remoteServers, s => sender.CompareTo(s.Guid), out var isServer);
        if (!isServer)
            return;
        var reader = new BitStreamReader(packet[2..]);
        if (!TryReadQueryWithAddresses(ref reader, out var query, out var specificSystems) || !reader.TryReadUInt32(out var requestId))
            return;

        var results = ProcessCloudQueryWithAddresses(query, specificSystems);
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudServerToServerCommand);
        stream.WriteByte(ProcessGetResponse);
        stream.WriteUInt32(requestId);
        stream.WriteUInt32((uint)results.Count);
        foreach (var (key, cloudData) in results)
            stream.Write(cloudData.ToRow(key));
        Send(host, stream, sender);
    }

    /// <summary>OnServerToServerGetResponse (CloudServer.cpp:815-864).</summary>
    private void OnServerToServerGetResponse(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender)
    {
        IndexOf(_remoteServers, s => sender.CompareTo(s.Guid), out var isServer);
        if (!isServer)
            return;
        var reader = new BitStreamReader(packet[2..]);
        if (!reader.TryReadUInt32(out var requestId))
            return;

        // Looked up with RakNet's GetRequestComp, which can miss a pending request.
        var requestIndex = IndexOf(_getRequests, r => GetRequestComp(requestId, r), out var hasRequest);
        if (!hasRequest)
            return;
        var request = _getRequests[requestIndex];
        var responseIndex = IndexOf(request.RemoteServerResponses, r => sender.CompareTo(r.Server), out var hasServer);
        if (!hasServer)
            return;
        var response = request.RemoteServerResponses[responseIndex];
        if (response.GotResult)
            return;
        response.GotResult = true;
        if (CloudQueryResult.TryReadRows(ref reader, out var rows))
            response.Rows = rows;

        // When every server has responded, answer the client.
        if (request.AllRemoteServersHaveResponded)
        {
            ProcessAndTransmitGetRequest(host, request);
            _getRequests.RemoveAt(requestIndex);
        }
    }

    /// <summary>OnSendUploadedAndSubscribedKeysToServer (CloudServer.cpp:1365-1407).</summary>
    private void OnSendUploadedAndSubscribedKeysToServer(ReadOnlySpan<byte> packet, RakNetGuid sender)
    {
        var index = IndexOf(_remoteServers, s => sender.CompareTo(s.Guid), out var isServer);
        if (!isServer)
            return;
        var server = _remoteServers[index];
        server.GotSubscribedAndUploadedKeys = true;
        var reader = new BitStreamReader(packet[2..]);
        if (!reader.TryReadUInt16(out var uploadedCount))
            return;
        for (var i = 0; i < uploadedCount; i++)
        {
            if (!CloudKey.TryRead(ref reader, out var key))
                return;
            Insert(server.UploadedKeys, key);
        }
        if (!reader.TryReadUInt16(out var subscribedCount))
            return;
        for (var i = 0; i < subscribedCount; i++)
        {
            if (!CloudKey.TryRead(ref reader, out var key))
                return;
            Insert(server.SubscribedKeys, key);
        }
    }

    /// <summary>
    /// OnSendUploadedKeyToServers, OnSendSubscribedKeyToServers, OnRemoveUploadedKeyFromServers and
    /// OnRemoveSubscribedKeyFromServers (CloudServer.cpp:1408-1483).
    /// </summary>
    private void OnServerKeyCommand(ReadOnlySpan<byte> packet, RakNetGuid sender, bool uploaded, bool add)
    {
        var index = IndexOf(_remoteServers, s => sender.CompareTo(s.Guid), out var isServer);
        if (!isServer)
            return;
        var reader = new BitStreamReader(packet[2..]);
        if (!CloudKey.TryRead(ref reader, out var key))
            return;
        var server = _remoteServers[index];
        var keys = uploaded ? server.UploadedKeys : server.SubscribedKeys;
        if (add)
            Insert(keys, key);
        else
            Remove(keys, key);
    }

    /// <summary>OnServerDataChanged (CloudServer.cpp:1484-1521): relays another server's change to the local subscribers.</summary>
    private void OnServerDataChanged(IPluginHost host, ReadOnlySpan<byte> packet, RakNetGuid sender)
    {
        IndexOf(_remoteServers, s => sender.CompareTo(s.Guid), out var isServer);
        if (!isServer)
            return;
        var reader = new BitStreamReader(packet[2..]);
        if (!reader.TryReadBool(out var wasUpdated) || !CloudQueryRow.TryRead(ref reader, out var row))
            return;
        var key = row.Key;
        var repositoryIndex = IndexOf(_dataRepository, l => key.CompareTo(l.Key), out var listExists);
        if (!listExists)
            return;
        var list = _dataRepository[repositoryIndex];
        var client = row.ClientGuid;
        var keyDataIndex = IndexOf(list.KeyData, d => client.CompareTo(d.ClientGuid), out var keyDataExists);
        if (keyDataExists)
            NotifyClientSubscribersOfDataChange(host, row, list.KeyData[keyDataIndex].SpecificSubscribers, wasUpdated);
        NotifyClientSubscribersOfDataChange(host, row, list.NonSpecificSubscribers, wasUpdated);
    }

    /// <summary>ProcessAndTransmitGetRequest (CloudServer.cpp:1165-1248).</summary>
    private void ProcessAndTransmitGetRequest(IPluginHost host, GetRequest request)
    {
        var query = request.Query;
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudGetResponse);
        // CloudQueryResult::SerializeHeader: the query, then subscribeToResults again.
        stream.Write(query);
        stream.WriteBool(query.SubscribeToResults);

        var results = ProcessCloudQueryWithAddresses(query, request.SpecificSystems);
        var unlimitedRows = query.MaxRowsToReturn == 0;
        var start = query.StartingRowIndex;
        var localNumRows = (uint)results.Count;
        if (!unlimitedRows && localNumRows > start && localNumRows - start > query.MaxRowsToReturn)
            localNumRows = start + query.MaxRowsToReturn;

        var rowCountOffset = stream.BitsWritten;
        uint localRowsToWrite;
        uint skipRows;
        if (localNumRows > start)
        {
            localRowsToWrite = localNumRows - start;
            skipRows = 0;
        }
        else
        {
            localRowsToWrite = 0;
            skipRows = start - localNumRows;
        }
        stream.WriteUInt32(localRowsToWrite);
        for (var i = start; i < localNumRows; i++)
            stream.Write(results[(int)i].CloudData.ToRow(results[(int)i].Key));

        // Append rows from other servers for the remaining rows.
        if (unlimitedRows || query.MaxRowsToReturn > localRowsToWrite)
        {
            uint remainingRows = 0;
            uint additionalRowsWritten = 0;
            if (!unlimitedRows)
                remainingRows = query.MaxRowsToReturn - localRowsToWrite;
            foreach (var response in request.RemoteServerResponses)
            {
                foreach (var row in response.Rows)
                {
                    if (skipRows > 0)
                    {
                        skipRows--;
                        continue;
                    }
                    stream.Write(row);
                    additionalRowsWritten++;
                    if (!unlimitedRows && --remainingRows == 0)
                        break;
                }
                if (!unlimitedRows && remainingRows == 0)
                    break;
            }

            if (additionalRowsWritten > 0)
            {
                // Patched in place over the bits already written, as RakNet's SetWriteOffset and Write do.
                var end = stream.BitsWritten;
                stream.SetWriteOffset(rowCountOffset);
                stream.WriteUInt32(localRowsToWrite + additionalRowsWritten);
                stream.SetWriteOffset(end);
            }
        }
        Send(host, stream, request.RequestingClient);
    }

    /// <summary>ProcessCloudQueryWithAddresses (CloudServer.cpp:1249-1299): the rows for each key in order.</summary>
    private List<(CloudKey Key, CloudData CloudData)> ProcessCloudQueryWithAddresses(CloudQuery query, List<RakNetGuid> specificSystems)
    {
        var results = new List<(CloudKey, CloudData)>();
        foreach (var key in query.Keys)
        {
            var repositoryIndex = IndexOf(_dataRepository, l => key.CompareTo(l.Key), out var listExists);
            if (!listExists)
                continue;
            var list = _dataRepository[repositoryIndex];
            if (list.UploaderCount == 0)
                continue;
            if (specificSystems.Count > 0)
            {
                // The rows of the matching systems.
                foreach (var system in specificSystems)
                {
                    var keyDataIndex = IndexOf(list.KeyData, d => system.CompareTo(d.ClientGuid), out var uploaderExists);
                    if (uploaderExists)
                        results.Add((key, list.KeyData[keyDataIndex]));
                }
            }
            else
            {
                foreach (var cloudData in list.KeyData)
                    results.Add((key, cloudData));
            }
        }
        return results;
    }

    /// <summary>SendUploadedAndSubscribedKeysToServer (CloudServer.cpp:1300-1328).</summary>
    private void SendUploadedAndSubscribedKeysToServer(IPluginHost host, RakNetGuid server)
    {
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudServerToServerCommand);
        stream.WriteByte(AddUploadedAndSubscribedKeys);
        stream.WriteUInt16((ushort)_dataRepository.Count);
        foreach (var list in _dataRepository)
            stream.Write(list.Key);

        var countOffset = stream.BitsWritten;
        ushort subscribedKeyCount = 0;
        stream.WriteUInt16(subscribedKeyCount);
        foreach (var list in _dataRepository)
        {
            if (list.SubscriberCount > 0)
            {
                stream.Write(list.Key);
                subscribedKeyCount++;
            }
        }
        var end = stream.BitsWritten;
        stream.SetWriteOffset(countOffset);
        stream.WriteUInt16(subscribedKeyCount);
        stream.SetWriteOffset(end);

        if (_dataRepository.Count > 0 || subscribedKeyCount > 0)
            Send(host, stream, server);
    }

    /// <summary>
    /// SendUploadedKeyToServers, SendSubscribedKeyToServers, RemoveUploadedKeyFromServers and
    /// RemoveSubscribedKeyFromServers (CloudServer.cpp:1329-1364): the command and the key, to every server.
    /// </summary>
    private void SendKeyToServers(IPluginHost host, byte command, CloudKey key)
    {
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudServerToServerCommand);
        stream.WriteByte(command);
        stream.Write(key);
        foreach (var server in _remoteServers)
            Send(host, stream, server.Guid);
    }

    /// <summary>NotifyClientSubscribersOfDataChange (CloudServer.cpp:1067-1100).</summary>
    private static void NotifyClientSubscribersOfDataChange(IPluginHost host, CloudData cloudData, CloudKey key, List<RakNetGuid> subscribers, bool wasUpdated) =>
        NotifyClientSubscribersOfDataChange(host, cloudData.ToRow(key), subscribers, wasUpdated);

    private static void NotifyClientSubscribersOfDataChange(IPluginHost host, CloudQueryRow row, List<RakNetGuid> subscribers, bool wasUpdated)
    {
        if (subscribers.Count == 0)
            return;
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudSubscriptionNotification);
        stream.WriteBool(wasUpdated);
        stream.Write(row);
        foreach (var subscriber in subscribers)
            Send(host, stream, subscriber);
    }

    /// <summary>NotifyServerSubscribersOfDataChange (CloudServer.cpp:1101-1127).</summary>
    private void NotifyServerSubscribersOfDataChange(IPluginHost host, CloudData cloudData, CloudKey key, bool wasUpdated)
    {
        using var stream = new BitStream();
        stream.WriteByte((byte)MessageId.CloudServerToServerCommand);
        stream.WriteByte(DataChanged);
        stream.WriteBool(wasUpdated);
        stream.Write(cloudData.ToRow(key));
        foreach (var server in _remoteServers)
        {
            if (!server.GotSubscribedAndUploadedKeys || Contains(server.SubscribedKeys, key))
                Send(host, stream, server.Guid);
        }
    }

    /// <summary>GetServersWithUploadedKeys (CloudServer.cpp:1522-1560).</summary>
    private List<RemoteServer> GetServersWithUploadedKeys(List<CloudKey> keys)
    {
        var servers = new List<RemoteServer>();
        foreach (var server in _remoteServers)
        {
            if (!server.GotSubscribedAndUploadedKeys || keys.Any(k => Contains(server.UploadedKeys, k)))
                servers.Add(server);
        }
        return servers;
    }

    /// <summary>GetOrAllocateCloudDataList (CloudServer.cpp:1561-1581).</summary>
    private CloudDataList GetOrAllocateCloudDataList(CloudKey key, out bool exists, out int index)
    {
        index = IndexOf(_dataRepository, l => key.CompareTo(l.Key), out exists);
        if (exists)
            return _dataRepository[index];
        var list = new CloudDataList(key);
        _dataRepository.Insert(index, list);
        return list;
    }

    /// <summary>UnsubscribeFromKey (CloudServer.cpp:1582-1640).</summary>
    private void UnsubscribeFromKey(IPluginHost host, RemoteCloudClient client, RakNetGuid clientGuid, int subscriberIndex, CloudKey key, List<RakNetGuid> specificSystems)
    {
        var subscription = client.SubscribedKeys[subscriberIndex];

        // Removing specific systems from a subscription to every system fails.
        if (subscription.SpecificSystems.Count == 0 && specificSystems.Count > 0)
            return;
        var repositoryIndex = IndexOf(_dataRepository, l => key.CompareTo(l.Key), out var listExists);
        if (!listExists)
            return;
        var list = _dataRepository[repositoryIndex];
        if (specificSystems.Count == 0)
        {
            // Remove the subscriber to every system; failing that, its specific subscriptions.
            if (!list.RemoveSubscriber(clientGuid))
            {
                foreach (var system in subscription.SpecificSystems)
                    RemoveSpecificSubscriber(system, list, clientGuid);
            }
            subscription.SpecificSystems.Clear();
        }
        else
        {
            foreach (var system in specificSystems)
            {
                var index = IndexOf(subscription.SpecificSystems, s => system.CompareTo(s), out var subscribed);
                if (!subscribed)
                    continue;
                RemoveSpecificSubscriber(system, list, clientGuid);
                subscription.SpecificSystems.RemoveAt(index);
            }
        }

        if (subscription.SpecificSystems.Count == 0)
            client.SubscribedKeys.RemoveAt(subscriberIndex);
        if (list.SubscriberCount == 0)
            SendKeyToServers(host, RemoveSubscribedKey, key);
        if (list.IsUnused)
            _dataRepository.RemoveAt(repositoryIndex);
    }

    /// <summary>RemoveSpecificSubscriber (CloudServer.cpp:1641-1662).</summary>
    private static void RemoveSpecificSubscriber(RakNetGuid specificSubscriber, CloudDataList list, RakNetGuid clientGuid)
    {
        var keyDataIndex = IndexOf(list.KeyData, d => specificSubscriber.CompareTo(d.ClientGuid), out var keyDataExists);
        if (!keyDataExists)
            return;
        var cloudData = list.KeyData[keyDataIndex];
        var index = IndexOf(cloudData.SpecificSubscribers, s => clientGuid.CompareTo(s), out var subscribed);
        if (!subscribed)
            return;
        cloudData.SpecificSubscribers.RemoveAt(index);
        list.SubscriberCount--;
        if (cloudData.IsUnused)
            list.KeyData.RemoveAt(keyDataIndex);
    }

    // GetExternalID(packet->systemAddress): the address the client reported seeing this peer at.
    private static SystemAddress ExternalAddressFor(IPluginHost host, SystemAddress client) =>
        host.Peer is { } peer && peer.TryGetConnection(client, out var connection) ? connection.ExternalAddress : SystemAddress.Unassigned;

    // forceAddress with the port the client sees, when set (CloudServer.cpp:337-341).
    private SystemAddress ExternalAddressOrForced(IPluginHost host, SystemAddress client)
    {
        var external = ExternalAddressFor(host, client);
        return ForcedExternalAddress.IsAssigned ? ForcedExternalAddress.WithPort(external.Port) : external;
    }

    // FromString(GetLocalIP(0)): ipList[0]'s IP with the port kept (RakNetTypes.cpp:477-560); an unassigned ipList[0]
    // names no host, so the address becomes unassigned.
    private static SystemAddress LocalAddressWithPort(IPluginHost host, ushort port) =>
        host.Peer is { InternalAddresses: [var local, ..] } && local.IsAssigned ? local.WithPort(port) : SystemAddress.Unassigned;

    // "Fix localhost port": a port of 0 becomes the bound port (CloudServer.cpp:348-352).
    private static SystemAddress FixPort(IPluginHost host, SystemAddress address) =>
        address.Port == 0 && host.Peer is { Sockets.Count: > 0 } peer ? address.WithPort(peer.Sockets[0].LocalAddress.Port) : address;

    private static void Send(IPluginHost host, BitStream stream, RakNetGuid target) =>
        host.Send(stream, target, Reliability.ReliableOrdered, Priority.High, 0);

    // CloudQueryWithAddresses::Serialize (CloudServer.cpp:90-114) and the same layout in OnGetRequest.
    private static void WriteQueryWithAddresses(BitStream stream, CloudQuery query, List<RakNetGuid> specificSystems)
    {
        stream.Write(query);
        stream.WriteUInt16((ushort)specificSystems.Count);
        foreach (var system in specificSystems)
            stream.WriteRakNetGuid(system);
    }

    private static bool TryReadQueryWithAddresses(ref BitStreamReader reader, out CloudQuery query, out List<RakNetGuid> specificSystems)
    {
        specificSystems = [];
        if (!CloudQuery.TryRead(ref reader, out var read) || !reader.TryReadUInt16(out var count) || !TryReadGuids(ref reader, count, out specificSystems))
        {
            query = null!;
            return false;
        }
        query = read;
        return true;
    }

    private static bool TryReadKeys(ref BitStreamReader reader, int count, out List<CloudKey> keys)
    {
        keys = [];
        if (count > reader.BitsRemaining / CloudKey.MinimumBits)
            return false;
        for (var i = 0; i < count; i++)
        {
            if (!CloudKey.TryRead(ref reader, out var key))
                return false;
            keys.Add(key);
        }
        return true;
    }

    private static bool TryReadGuids(ref BitStreamReader reader, int count, out List<RakNetGuid> guids)
    {
        guids = [];
        if (count > reader.BitsRemaining / (sizeof(ulong) * 8))
            return false;
        for (var i = 0; i < count; i++)
        {
            if (!reader.TryReadRakNetGuid(out var guid))
                return false;
            guids.Add(guid);
        }
        return true;
    }

    // GetRequestComp (CloudServer.cpp:82-89): never returns a positive value.
    private static int GetRequestComp(uint key, GetRequest request) => key == request.RequestId ? 0 : -1;

    /// <summary>
    /// DataStructures::OrderedList::GetIndexFromKey (DS_OrderedList.h:119-172): RakNet's binary search, where the
    /// lists are ordered, and where a comparison is inconsistent, as GetRequestComp is.
    /// </summary>
    private static int IndexOf<T>(List<T> list, Func<T, int> compareKeyTo, out bool exists)
    {
        exists = false;
        if (list.Count == 0)
            return 0;
        var upperBound = list.Count - 1;
        var lowerBound = 0;
        var index = list.Count / 2;
        while (true)
        {
            var result = compareKeyTo(list[index]);
            if (result == 0)
            {
                exists = true;
                return index;
            }
            if (result < 0)
                upperBound = index - 1;
            else
                lowerBound = index + 1;
            index = lowerBound + (upperBound - lowerBound) / 2;
            if (lowerBound > upperBound)
                return lowerBound;
            if (index < 0 || index >= list.Count)
                return 0;
        }
    }

    private static bool Contains<T>(List<T> list, T item)
        where T : IComparable<T>
    {
        IndexOf(list, i => item.CompareTo(i), out var exists);
        return exists;
    }

    // OrderedList::Insert, which ignores a duplicate.
    private static void Insert<T>(List<T> list, T item)
        where T : IComparable<T>
    {
        var index = IndexOf(list, i => item.CompareTo(i), out var exists);
        if (!exists)
            list.Insert(index, item);
    }

    // OrderedList::Remove, which ignores a missing item in a release build.
    private static void Remove<T>(List<T> list, T item)
        where T : IComparable<T>
    {
        var index = IndexOf(list, i => item.CompareTo(i), out var exists);
        if (exists)
            list.RemoveAt(index);
    }

    /// <summary>CloudData (CloudServer.h:139-175): one client's row under a key.</summary>
    private sealed class CloudData
    {
        public byte[] Data { get; set; } = [];

        public bool IsUploaded { get; set; }

        public SystemAddress ServerSystemAddress { get; set; }

        public SystemAddress ClientSystemAddress { get; set; }

        public RakNetGuid ServerGuid { get; init; }

        public RakNetGuid ClientGuid { get; init; }

        /// <summary>When this client's data under the key changes, notify these subscribers.</summary>
        public List<RakNetGuid> SpecificSubscribers { get; } = [];

        public bool IsUnused => !IsUploaded && SpecificSubscribers.Count == 0;

        public void Clear()
        {
            Data = [];
            IsUploaded = false;
        }

        public CloudQueryRow ToRow(CloudKey key) => new(key, Data, ServerSystemAddress, ClientSystemAddress, ServerGuid, ClientGuid);
    }

    /// <summary>CloudDataList (CloudServer.h:180-208): every row under a key.</summary>
    private sealed class CloudDataList(CloudKey key)
    {
        public CloudKey Key { get; } = key;

        // unsigned int, wrapping as RakNet's does.
        public uint UploaderCount { get; set; }

        public uint SubscriberCount { get; set; }

        /// <summary>Rows posted by or subscribed to for each client, by client GUID.</summary>
        public List<CloudData> KeyData { get; } = [];

        /// <summary>When the key changes for any client, notify these subscribers.</summary>
        public List<RakNetGuid> NonSpecificSubscribers { get; } = [];

        public bool IsUnused => KeyData.Count == 0 && NonSpecificSubscribers.Count == 0;

        public bool IsNotUploaded => UploaderCount == 0;

        public bool RemoveSubscriber(RakNetGuid subscriber)
        {
            var index = IndexOf(NonSpecificSubscribers, s => subscriber.CompareTo(s), out var exists);
            if (!exists)
                return false;
            SubscriberCount--;
            NonSpecificSubscribers.RemoveAt(index);
            return true;
        }
    }

    /// <summary>KeySubscriberID (CloudServer.h:212-217).</summary>
    private sealed class KeySubscription(CloudKey key)
    {
        public CloudKey Key { get; } = key;

        public List<RakNetGuid> SpecificSystems { get; } = [];
    }

    /// <summary>RemoteCloudClient (CloudServer.h:221-229): what a connected client posted and subscribed to.</summary>
    private sealed class RemoteCloudClient
    {
        public List<CloudKey> UploadedKeys { get; } = [];

        public List<KeySubscription> SubscribedKeys { get; } = [];

        // uint64_t, wrapping as RakNet's does.
        public ulong UploadedBytes { get; set; }

        public bool IsUnused => UploadedKeys.Count == 0 && SubscribedKeys.Count == 0;
    }

    /// <summary>RemoteServer (CloudServer.h:243-257).</summary>
    private sealed class RemoteServer(RakNetGuid guid)
    {
        public RakNetGuid Guid { get; } = guid;

        /// <summary>The server needs to know when these keys change.</summary>
        public List<CloudKey> SubscribedKeys { get; } = [];

        /// <summary>The server holds these keys and needs to be queried for them.</summary>
        public List<CloudKey> UploadedKeys { get; } = [];

        /// <summary>False until the server sends its keys, so it is sent everything.</summary>
        public bool GotSubscribedAndUploadedKeys { get; set; }
    }

    /// <summary>BufferedGetResponseFromServer (CloudServer.h:262-269).</summary>
    private sealed class BufferedGetResponse(RakNetGuid server)
    {
        public RakNetGuid Server { get; } = server;

        public IReadOnlyList<CloudQueryRow> Rows { get; set; } = [];

        public bool GotResult { get; set; }
    }

    /// <summary>GetRequest (CloudServer.h:280-296): a client's query waiting for other servers.</summary>
    private sealed class GetRequest(CloudQuery query, List<RakNetGuid> specificSystems, RakNetGuid requestingClient, ulong requestStartTime, uint requestId)
    {
        public CloudQuery Query { get; } = query;

        public List<RakNetGuid> SpecificSystems { get; } = specificSystems;

        public RakNetGuid RequestingClient { get; } = requestingClient;

        /// <summary>requestStartTime, kept as RakNet keeps it for the timeout that never fires.</summary>
        public ulong RequestStartTime { get; } = requestStartTime;

        public uint RequestId { get; } = requestId;

        public List<BufferedGetResponse> RemoteServerResponses { get; } = [];

        public bool AllRemoteServersHaveResponded => RemoteServerResponses.All(r => r.GotResult);
    }
}
