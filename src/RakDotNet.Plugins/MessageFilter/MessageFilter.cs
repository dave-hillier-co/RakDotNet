using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>A system sent a message its filter set does not allow: MessageFilter's invalidMessageCallback.</summary>
/// <param name="system">The sender, with both its GUID and address as RakNet's AddressOrGUID(packet) has them.</param>
/// <param name="filterSetId">The sender's filter set.</param>
/// <param name="messageId">The packet's first byte: ID_TIMESTAMP for a timestamped message (MessageFilter.cpp:327).</param>
public delegate void DisallowedMessageHandler(FilteredSystem system, int filterSetId, byte messageId);

/// <summary>A system stayed in its filter set longer than allowed and is being kicked: MessageFilter's timeoutCallback.</summary>
public delegate void FilterTimeoutHandler(FilteredSystem system, int filterSetId);

/// <summary>A system as MessageFilter keys it: RakNet's AddressOrGUID, which may hold a GUID, an address or both.</summary>
public readonly record struct FilteredSystem(RakNetGuid RakNetGuid, SystemAddress Address)
{
    /// <summary>
    /// AddressOrGUID::ToString(false) (RakNetTypes.cpp:66-71): the GUID when it is assigned, otherwise the address
    /// without its port.
    /// </summary>
    public override string ToString() => RakNetGuid.IsAssigned ? RakNetGuid.ToString() : Address.ToString(writePort: false);

    internal AddressOrGuid Target => RakNetGuid.IsAssigned ? RakNetGuid : Address;

    internal static FilteredSystem From(AddressOrGuid target) => new(target.RakNetGuid, target.Address);
}

/// <summary>
/// MessageFilter: assigns systems to filter sets, each of which limits the message IDs its systems may send. A message
/// a filter set does not allow is dropped, and may get its sender kicked or banned; a system can also be limited in how
/// long it stays in a filter set. Attach it before the plugins it protects.
/// </summary>
/// <remarks>
/// <para>
/// Ported from MessageFilter.cpp. Filter sets are created with default settings the first time any member names them,
/// and allow nothing until told to. Systems are keyed as RakNet's hash keys them, by GUID when there is one and by
/// address otherwise, so a system added by address is not found from its packets, which always carry a GUID.
/// </para>
/// <para>
/// RakNet's behaviour is kept where it surprises: a timestamped message is filtered on the byte after a 4-byte time
/// (sizeof(RakNet::TimeMS)), although the time RakPeer writes is 8 bytes, and a timestamped packet shorter than 5 bytes is
/// dropped even from an unfiltered system (MessageFilter.cpp:315-320); a ban on leaving a filter set by timeout bans the
/// system's GUID text, which the ban list ignores as longer than 15 characters (MessageFilter.cpp:278-283); the time
/// limit is checked every 2 s (MessageFilter.cpp:262, 296). <see cref="AutoAddNewConnectionsToFilter"/> starts at -1, where
/// the C++ leaves it uninitialised.
/// </para>
/// <para>Members may be called from any thread; hooks run on the dispatch step.</para>
/// </remarks>
public sealed class MessageFilter : IRakPlugin
{
    /// <summary>MESSAGE_FILTER_MAX_MESSAGE_ID: the IDs a filter set holds a flag for.</summary>
    public const int MaxMessageId = 256;

    // RakNet::GetTime is 64-bit; GreaterThan compares across wraparound (GetTime.h).
    private const ulong TimeoutCheckIntervalMs = 1000;

    private readonly Lock _gate = new();
    private readonly SortedList<int, FilterSet> _filterSets = [];
    private readonly Dictionary<SystemKey, Membership> _systems = [];
    private IPluginHost? _host;
    private int _autoAddNewConnectionsToFilter = -1;
    private ulong _whenLastTimeoutCheck;

    /// <summary>
    /// SetAutoAddNewConnectionsToFilter: every new connection joins this filter set; below 0, none is added.
    /// </summary>
    public int AutoAddNewConnectionsToFilter
    {
        get => _autoAddNewConnectionsToFilter;
        set => _autoAddNewConnectionsToFilter = value;
    }

    /// <summary>GetFilterSetCount</summary>
    public int FilterSetCount
    {
        get
        {
            lock (_gate)
                return _filterSets.Count;
        }
    }

    /// <summary>
    /// SetAllowMessageID: allows or disallows <paramref name="messageIdStart"/> to <paramref name="messageIdEnd"/>
    /// inclusive. The connection notifications MessageFilter.cpp:301-316 lists are always allowed.
    /// </summary>
    public void SetAllowMessageId(bool allow, int messageIdStart, int messageIdEnd, int filterSetId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageIdStart);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(messageIdStart, messageIdEnd);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(messageIdEnd, MaxMessageId);
        lock (_gate)
        {
            var filterSet = GetOrCreate(filterSetId);
            for (var id = messageIdStart; id <= messageIdEnd; id++)
                filterSet.AllowedIds[id] = allow;
        }
    }

    /// <summary>
    /// SetAllowRPC4: allows or disallows one RPC4 function by its registered name; allowing the first also allows
    /// ID_RPC_PLUGIN, and disallowing the last disallows it (MessageFilter.cpp:79-104).
    /// </summary>
    public void SetAllowRpc4(bool allow, string uniqueId, int filterSetId)
    {
        ArgumentNullException.ThrowIfNull(uniqueId);
        lock (_gate)
        {
            var filterSet = GetOrCreate(filterSetId);
            if (allow)
            {
                if (filterSet.AllowedRpc4.Add(uniqueId))
                    filterSet.AllowedIds[(int)MessageId.RpcPlugin] = true;
            }
            else if (filterSet.AllowedRpc4.Remove(uniqueId) && filterSet.AllowedRpc4.Count == 0)
            {
                filterSet.AllowedIds[(int)MessageId.RpcPlugin] = false;
            }
        }
    }

    /// <summary>
    /// SetActionOnDisallowedMessage: what happens to a system that sends a disallowed message, beyond the message being
    /// dropped. A ban of <see cref="TimeSpan.Zero"/> is permanent.
    /// </summary>
    public void SetActionOnDisallowedMessage(bool kickOnDisallowed, bool banOnDisallowed, TimeSpan banDuration, int filterSetId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(banDuration, TimeSpan.Zero);
        lock (_gate)
        {
            var filterSet = GetOrCreate(filterSetId);
            filterSet.KickOnDisallowedMessage = kickOnDisallowed;
            filterSet.BanOnDisallowedMessage = banOnDisallowed;
            filterSet.DisallowedMessageBanTime = banDuration;
        }
    }

    /// <summary>SetDisallowedMessageCallback; called on the dispatch step, before any kick or ban.</summary>
    public void SetDisallowedMessageCallback(int filterSetId, DisallowedMessageHandler? callback)
    {
        lock (_gate)
            GetOrCreate(filterSetId).DisallowedMessageCallback = callback;
    }

    /// <summary>SetTimeoutCallback; called on the dispatch step, before the system is banned and kicked.</summary>
    public void SetTimeoutCallback(int filterSetId, FilterTimeoutHandler? callback)
    {
        lock (_gate)
            GetOrCreate(filterSetId).TimeoutCallback = callback;
    }

    /// <summary>
    /// SetFilterMaxTime: a system that stays in the filter set this long is kicked, and banned when
    /// <paramref name="banOnExceed"/>. <see cref="TimeSpan.Zero"/> is no limit, the default.
    /// </summary>
    public void SetFilterMaxTime(TimeSpan allowedTime, bool banOnExceed, TimeSpan banDuration, int filterSetId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(allowedTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(banDuration, TimeSpan.Zero);
        lock (_gate)
        {
            var filterSet = GetOrCreate(filterSetId);
            filterSet.MaxMemberTimeMs = (ulong)allowedTime.TotalMilliseconds;
            filterSet.BanOnFilterTimeExceed = banOnExceed;
            filterSet.TimeExceedBanTime = banDuration;
        }
    }

    /// <summary>GetSystemFilterSet: the system's filter set, or -1 for none.</summary>
    public int GetSystemFilterSet(AddressOrGuid system)
    {
        lock (_gate)
            return _systems.TryGetValue(SystemKey.From(system), out var membership) ? membership.FilterSet.Id : -1;
    }

    /// <summary>
    /// SetSystemFilterSet: moves the system into a filter set, restarting its time there, or out of every filter set
    /// with a negative <paramref name="filterSetId"/>.
    /// </summary>
    public void SetSystemFilterSet(AddressOrGuid system, int filterSetId)
    {
        if (system.IsUndefined)
            throw new ArgumentException("The system is undefined.", nameof(system));
        lock (_gate)
            SetSystemFilterSet(new FilteredSystem(system.RakNetGuid, system.Address), filterSetId);
    }

    /// <summary>GetSystemCount: systems in <paramref name="filterSetId"/>, or in any filter set for -1.</summary>
    public int GetSystemCount(int filterSetId = -1)
    {
        lock (_gate)
            return filterSetId == -1 ? _systems.Count : _systems.Values.Count(m => m.FilterSet.Id == filterSetId);
    }

    /// <summary>GetFilterSetIDByIndex, in ascending ID order.</summary>
    public int GetFilterSetIdByIndex(int index)
    {
        lock (_gate)
            return _filterSets.Keys[index];
    }

    /// <summary>DeleteFilterSet: the systems in it become unrestricted.</summary>
    public void DeleteFilterSet(int filterSetId)
    {
        lock (_gate)
        {
            if (!_filterSets.Remove(filterSetId, out var filterSet))
                return;
            foreach (var (key, membership) in _systems.ToList())
            {
                if (ReferenceEquals(membership.FilterSet, filterSet))
                    _systems.Remove(key);
            }
        }
    }

    void IRakPlugin.OnAttached(IPluginHost host)
    {
        _host = host;
        // The constructor's whenLastTimeoutCheck=RakNet::GetTime() (MessageFilter.cpp:44-47), on the host's clock.
        lock (_gate)
            _whenLastTimeoutCheck = host.GetTime();
    }

    void IRakPlugin.OnDetached() => _host = null;

    /// <summary>MessageFilter::Update (MessageFilter.cpp:258-297): kicks the systems that outstayed their filter set.</summary>
    void IRakPlugin.OnUpdate()
    {
        if (_host is not { } host)
            return;
        var expired = new List<(FilteredSystem System, FilterSet FilterSet)>();
        lock (_gate)
        {
            var now = host.GetTime();
            if (!GreaterThan(now - TimeoutCheckIntervalMs, _whenLastTimeoutCheck))
                return;
            foreach (var membership in _systems.Values)
            {
                var filterSet = membership.FilterSet;
                if (filterSet.MaxMemberTimeMs > 0 && now - membership.TimeEnteredMs >= filterSet.MaxMemberTimeMs)
                    expired.Add((membership.System, filterSet));
            }
            foreach (var (system, _) in expired)
                _systems.Remove(SystemKey.From(system));
            _whenLastTimeoutCheck = now + TimeoutCheckIntervalMs;
        }

        foreach (var (system, filterSet) in expired)
        {
            filterSet.TimeoutCallback?.Invoke(system, filterSet.Id);
            if (host.Peer is { } peer)
            {
                if (filterSet.BanOnFilterTimeExceed)
                    peer.BanList.Add(system.ToString(), filterSet.TimeExceedBanTime);
                peer.Close(system.Target, notify: true, orderingChannel: 0);
            }
        }
    }

    /// <summary>MessageFilter::OnNewConnection (MessageFilter.cpp:298-310).</summary>
    void IRakPlugin.OnConnected(RakConnection connection, bool isIncoming)
    {
        var system = new FilteredSystem(connection.RakNetGuid, connection.Address);
        lock (_gate)
        {
            if (_autoAddNewConnectionsToFilter >= 0 && !_systems.ContainsKey(SystemKey.From(system)))
                SetSystemFilterSet(system, _autoAddNewConnectionsToFilter);
        }
    }

    /// <summary>MessageFilter::OnClosedConnection (MessageFilter.cpp:311-320).</summary>
    void IRakPlugin.OnDisconnected(RakConnection connection, DisconnectReason reason)
    {
        lock (_gate)
            _systems.Remove(SystemKey.From(new FilteredSystem(connection.RakNetGuid, connection.Address)));
    }

    /// <summary>MessageFilter::OnReceive (MessageFilter.cpp:321-376).</summary>
    PluginResult IRakPlugin.OnEvent(PeerEvent e)
    {
        var data = e.Data;
        if (data.IsEmpty)
            return PluginResult.Continue;
        switch ((MessageId)data[0])
        {
            case MessageId.NewIncomingConnection:
            case MessageId.ConnectionRequestAccepted:
            case MessageId.ConnectionLost:
            case MessageId.DisconnectionNotification:
            case MessageId.ConnectionAttemptFailed:
            case MessageId.NoFreeIncomingConnections:
            case MessageId.IpRecentlyConnected:
            case MessageId.ConnectionBanned:
            case MessageId.InvalidPassword:
            case MessageId.UnconnectedPong:
            case MessageId.AlreadyConnected:
            case MessageId.AdvertiseSystem:
            case MessageId.RemoteDisconnectionNotification:
            case MessageId.RemoteConnectionLost:
            case MessageId.RemoteNewIncomingConnection:
            case MessageId.DownloadProgress:
                return PluginResult.Continue;
        }

        byte messageId;
        if (data[0] == (byte)MessageId.Timestamp)
        {
            // sizeof(MessageID) + sizeof(RakNet::TimeMS): 4 bytes of time, not the 8 RakPeer writes.
            if (data.Length < 1 + sizeof(uint))
                return PluginResult.Consume;
            messageId = data[1 + sizeof(uint)];
        }
        else
        {
            messageId = data[0];
        }

        var system = new FilteredSystem(e.RakNetGuid, e.Address);
        FilterSet filterSet;
        lock (_gate)
        {
            if (!_systems.TryGetValue(SystemKey.From(system), out var membership))
                return PluginResult.Continue;
            filterSet = membership.FilterSet;
            var allowed = filterSet.AllowedIds[messageId];
            if (allowed && data[0] == (byte)MessageId.RpcPlugin)
                allowed = filterSet.AllowedRpc4.Contains(ReadRpc4FunctionName(data));
            if (allowed)
                return PluginResult.Continue;
        }
        OnInvalidMessage(filterSet, system, data[0]);
        return PluginResult.Consume;
    }

    // MessageFilter::OnInvalidMessage (MessageFilter.cpp:239-257).
    private void OnInvalidMessage(FilterSet filterSet, FilteredSystem system, byte messageId)
    {
        filterSet.DisallowedMessageCallback?.Invoke(system, filterSet.Id, messageId);
        if (_host?.Peer is not { } peer)
            return;
        if (filterSet.BanOnDisallowedMessage)
            peer.BanList.Add(system.Address.ToString(writePort: false), filterSet.DisallowedMessageBanTime);
        if (filterSet.KickOnDisallowedMessage)
            peer.Close(system.Target, notify: true, orderingChannel: 0);
    }

    // bsIn.IgnoreBytes(2); bsIn.ReadCompressed(functionName): an unreadable name is RakString's empty string.
    private static string ReadRpc4FunctionName(ReadOnlySpan<byte> data)
    {
        var reader = new BitStreamReader(data);
        if (reader.BitsRemaining < 16)
            return "";
        reader.IgnoreBits(16);
        return reader.TryReadCompressedRakStringBytes(out var name) ? Encoding.UTF8.GetString(name) : "";
    }

    // MessageFilter::SetSystemFilterSet (MessageFilter.cpp:139-176), under the lock.
    private void SetSystemFilterSet(FilteredSystem system, int filterSetId)
    {
        var key = SystemKey.From(system);
        var now = _host?.GetTime() ?? 0;
        if (!_systems.TryGetValue(key, out var membership))
        {
            if (filterSetId < 0)
                return;
            _systems[key] = new Membership(system, GetOrCreate(filterSetId), now);
        }
        else if (filterSetId >= 0)
        {
            _systems[key] = membership with { FilterSet = GetOrCreate(filterSetId), TimeEnteredMs = now };
        }
        else
        {
            _systems.Remove(key);
        }
    }

    // GetFilterSetByID (MessageFilter.cpp:211-238): created with default settings when missing.
    private FilterSet GetOrCreate(int filterSetId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(filterSetId);
        if (!_filterSets.TryGetValue(filterSetId, out var filterSet))
        {
            filterSet = new FilterSet(filterSetId);
            _filterSets.Add(filterSetId, filterSet);
        }
        return filterSet;
    }

    // GreaterThan(RakNet::Time, RakNet::Time) (GetTime.h): a is after b, across wraparound.
    private static bool GreaterThan(ulong a, ulong b) => b != a && b - a > ulong.MaxValue / 2;

    private sealed class FilterSet(int id)
    {
        public int Id { get; } = id;

        public bool[] AllowedIds { get; } = new bool[MaxMessageId];

        public SortedSet<string> AllowedRpc4 { get; } = new(StringComparer.Ordinal);

        public bool BanOnFilterTimeExceed { get; set; }

        public bool KickOnDisallowedMessage { get; set; }

        public bool BanOnDisallowedMessage { get; set; }

        public TimeSpan DisallowedMessageBanTime { get; set; }

        public TimeSpan TimeExceedBanTime { get; set; }

        public ulong MaxMemberTimeMs { get; set; }

        public DisallowedMessageHandler? DisallowedMessageCallback { get; set; }

        public FilterTimeoutHandler? TimeoutCallback { get; set; }
    }

    private sealed record Membership(FilteredSystem System, FilterSet FilterSet, ulong TimeEnteredMs);

    // DataStructures::Hash<AddressOrGUID, ..., AddressOrGUID::ToInteger>: hashed by GUID when assigned, else by address.
    private readonly record struct SystemKey(bool ByGuid, RakNetGuid RakNetGuid, SystemAddress Address)
    {
        public static SystemKey From(FilteredSystem system) => system.RakNetGuid.IsAssigned
            ? new SystemKey(true, system.RakNetGuid, default)
            : new SystemKey(false, default, system.Address);

        public static SystemKey From(AddressOrGuid target) => From(FilteredSystem.From(target));
    }
}
