using System.Buffers;
using System.Net;
using System.Net.Sockets;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;
using RakDotNet.Security;

namespace RakDotNet.Compatibility;

/// <summary>
/// A RakPeerInterface-shaped facade over one <see cref="RakPeer"/>, with RakNet's names and shapes, for code ported
/// from RakNet or Brutal.RakNet. It implements docs/api-design.md's RakPeerInterface mapping in reverse.
/// </summary>
/// <remarks>
/// <para>
/// The peer runs in <see cref="PeerThreading.Manual"/>: <see cref="Receive"/> runs one update, reads, skips events
/// whose <see cref="PeerEvent.IsRakNetPacket"/> is false, and detaches the rest into a <see cref="Packet"/>. Hostnames
/// are strings and are resolved on the caller, as RakNet resolves them. No plugin may depend on this assembly.
/// </para>
/// <para>
/// As in RakNet, settings made before <see cref="Startup"/> are kept and applied when it starts, and a peer that was
/// shut down can be started again; each start creates a new <see cref="RakPeer"/>, since a RakPeer starts once.
/// Calls RakNet answers with 0, false or an unassigned value when the peer is not started answer the same way here.
/// </para>
/// </remarks>
public sealed class RakPeerInterface : IDisposable
{
    private readonly List<IRakPlugin> _pendingPlugins = [];
    private readonly List<string> _pendingSecurityExceptions = [];
    private RakPeerOptions _pending = new() { MaxConnections = 1 };
    private IncomingDatagramFilter? _pendingFilter;
    private RakPeer? _peer;

    private RakPeerInterface() { }

    /// <summary>The peer behind the facade; null until <see cref="Startup"/>.</summary>
    public RakPeer? Peer => _peer;

    public static RakPeerInterface GetInstance() => new();

    public static void DestroyInstance(RakPeerInterface instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        instance.Dispose();
    }

    /// <summary>Thread priority is not configurable, so RakNet's threadPriority argument is absent.</summary>
    public StartupResult Startup(int maxConnections, ReadOnlySpan<SocketDescriptor> socketDescriptors)
    {
        // RakPeer.cpp:374-422: already started, then the descriptors, then maxConnections.
        if (IsActive())
            return StartupResult.RakNetAlreadyStarted;
        if (socketDescriptors.IsEmpty)
            return StartupResult.InvalidSocketDescriptors;
        if (maxConnections <= 0)
            return StartupResult.InvalidMaxConnections;

        var bindings = new List<SocketBinding>(socketDescriptors.Length);
        foreach (var descriptor in socketDescriptors)
        {
            var any = descriptor.SocketFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
            IPAddress address;
            if (string.IsNullOrEmpty(descriptor.HostAddress))
                address = any;
            else if (!IPAddress.TryParse(descriptor.HostAddress, out address!))
                return StartupResult.SocketFailedToBind;
            bindings.Add(new SocketBinding(new IPEndPoint(address, descriptor.Port)));
        }

        var options = _pending with
        {
            MaxConnections = maxConnections,
            Bindings = bindings,
            Threading = PeerThreading.Manual,
            Plugins = [.. _pendingPlugins],
            SecurityExceptions = [.. _pendingSecurityExceptions],
        };
        try
        {
            _peer = RakPeer.Start(options);
        }
        catch (RakStartupException exception)
        {
            return exception.Error switch
            {
                StartupError.SocketFamilyNotSupported => StartupResult.SocketFamilyNotSupported,
                StartupError.SocketPortAlreadyInUse => StartupResult.SocketPortAlreadyInUse,
                StartupError.SocketFailedToBind => StartupResult.SocketFailedToBind,
                StartupError.SocketFailedTestSend => StartupResult.SocketFailedTestSend,
                StartupError.FailedToCreateNetworkThread => StartupResult.FailedToCreateNetworkThread,
                _ => StartupResult.StartupOtherFailure,
            };
        }
        _peer.LowLevel.IncomingDatagramFilter = _pendingFilter;
        _pendingPlugins.Clear();
        return StartupResult.RakNetStarted;
    }

    /// <summary>InitializeSecurity: only while stopped (RakPeer.cpp:765-766); applied at the next <see cref="Startup"/>.</summary>
    public bool InitializeSecurity(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey, bool requireClientKey = false)
    {
        if (IsActive() || !RakKeyPair.TryImport(publicKey, privateKey, out var keys))
            return false;
        _pending = _pending with { Security = new ServerSecurity(keys) { RequireClientKey = requireClientKey } };
        return true;
    }

    /// <summary>DisableSecurity: only while stopped.</summary>
    public void DisableSecurity()
    {
        if (!IsActive())
            _pending = _pending with { Security = null };
    }

    public void AddToSecurityExceptionList(string ip)
    {
        ArgumentException.ThrowIfNullOrEmpty(ip);
        if (_peer is { IsActive: true } peer)
            peer.SecurityExceptions.Add(ip);
        else
            _pendingSecurityExceptions.Add(ip);
    }

    /// <summary>A null <paramref name="ip"/> clears the list.</summary>
    public void RemoveFromSecurityExceptionList(string? ip)
    {
        if (_peer is { IsActive: true } peer)
        {
            if (ip is null)
                peer.SecurityExceptions.Clear();
            else
                peer.SecurityExceptions.Remove(ip);
        }
        else if (ip is null)
        {
            _pendingSecurityExceptions.Clear();
        }
        else
        {
            _pendingSecurityExceptions.Remove(ip);
        }
    }

    public bool IsInSecurityExceptionList(string ip) =>
        _peer is { IsActive: true } peer ? peer.SecurityExceptions.Contains(ip) : _pendingSecurityExceptions.Contains(ip);

    public void SetMaximumIncomingConnections(ushort numberAllowed)
    {
        _pending = _pending with { MaxIncomingConnections = numberAllowed };
        if (_peer is { IsActive: true } peer)
            peer.MaxIncomingConnections = numberAllowed;
    }

    public ushort GetMaximumIncomingConnections() =>
        (ushort)(_peer is { IsActive: true } peer ? peer.MaxIncomingConnections : _pending.MaxIncomingConnections);

    /// <summary>The connected systems: NumberOfConnections (RakPeer.cpp:920-926) counts GetSystemList, which lists only CONNECTED systems (RakPeer.cpp:1832-1850).</summary>
    public ushort NumberOfConnections() => (ushort)ConnectedSystems().Count();

    public void SetIncomingPassword(ReadOnlySpan<byte> passwordData)
    {
        var password = passwordData[..Math.Min(passwordData.Length, 255)].ToArray();
        if (_peer is { IsActive: true } peer)
            peer.IncomingPassword = password;
        _pending = _pending with { IncomingPassword = password };
    }

    public ReadOnlyMemory<byte> GetIncomingPassword() =>
        _peer is { IsActive: true } peer ? peer.IncomingPassword : _pending.IncomingPassword;

    /// <summary>
    /// RakPeer::Connect (RakPeer.cpp:993-1016): INVALID_PARAMETER when the peer is not started, the host is empty, the
    /// port is 0 or the socket index does not exist; the password is cut to 255 bytes.
    /// </summary>
    public ConnectionAttemptResult Connect(string host, ushort remotePort, ReadOnlySpan<byte> passwordData = default,
                                           ConnectSecurity? publicKey = null, uint connectionSocketIndex = 0,
                                           uint sendConnectionAttemptCount = 12, uint timeBetweenSendConnectionAttemptsMS = 500,
                                           uint timeoutTime = 0)
    {
        if (_peer is not { IsActive: true } peer || string.IsNullOrEmpty(host) || remotePort == 0
            || connectionSocketIndex >= (uint)peer.Sockets.Count)
            return ConnectionAttemptResult.InvalidParameter;
        if (Resolve(host, remotePort) is not { } remote)
            return ConnectionAttemptResult.CannotResolveDomainName;

        var result = peer.Connect(remote, new ConnectOptions
        {
            Password = passwordData[..Math.Min(passwordData.Length, 255)].ToArray(),
            Security = publicKey ?? ConnectSecurity.Insecure,
            SocketIndex = (int)connectionSocketIndex,
            AttemptCount = (int)Math.Min(sendConnectionAttemptCount, int.MaxValue),
            AttemptInterval = TimeSpan.FromMilliseconds(timeBetweenSendConnectionAttemptsMS),
            Timeout = TimeSpan.FromMilliseconds(timeoutTime),
        });
        return result switch
        {
            ConnectAttemptResult.Started => ConnectionAttemptResult.ConnectionAttemptStarted,
            ConnectAttemptResult.CannotResolveDomainName => ConnectionAttemptResult.CannotResolveDomainName,
            ConnectAttemptResult.AlreadyConnectedToEndpoint => ConnectionAttemptResult.AlreadyConnectedToEndpoint,
            ConnectAttemptResult.ConnectionAttemptAlreadyInProgress => ConnectionAttemptResult.ConnectionAttemptAlreadyInProgress,
            _ => ConnectionAttemptResult.SecurityInitializationFailed,
        };
    }

    /// <summary>RakPeer::Shutdown; blocks for at most <paramref name="blockDuration"/> milliseconds.</summary>
    public void Shutdown(uint blockDuration, byte orderingChannel = 0, Priority disconnectionNotificationPriority = Priority.Low) =>
        _peer?.ShutdownAsync(TimeSpan.FromMilliseconds(blockDuration), orderingChannel, disconnectionNotificationPriority)
            .AsTask().GetAwaiter().GetResult();

    public bool IsActive() => _peer?.IsActive ?? false;

    /// <summary>
    /// GetConnectionList: fills <paramref name="remoteSystems"/> with the connected systems and returns how many it
    /// filled, or, for an empty span, how many there are.
    /// </summary>
    public int GetConnectionList(Span<SystemAddress> remoteSystems)
    {
        var count = 0;
        foreach (var connection in ConnectedSystems())
        {
            if (remoteSystems.IsEmpty)
            {
                count++;
                continue;
            }
            if (count == remoteSystems.Length)
                break;
            remoteSystems[count++] = connection.Address;
        }
        return count;
    }

    public uint GetNextSendReceipt() => _peer?.PeekNextReceipt().Value ?? 1;

    public uint IncrementNextSendReceipt() => _peer?.ReserveReceipt().Value ?? 1;

    /// <summary>RakPeer::Send (RakPeer.cpp:1308-1349): 0 on bad input or when the peer is not active.</summary>
    public uint Send(ReadOnlySpan<byte> data, Priority priority, Reliability reliability, byte orderingChannel,
                     AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0) =>
        Send(new OutgoingMessage(data), priority, reliability, orderingChannel, systemIdentifier, broadcast, forceReceiptNumber);

    /// <summary>RakPeer::Send(BitStream*): the stream's exact bit length is sent.</summary>
    public uint Send(BitStream bitStream, Priority priority, Reliability reliability, byte orderingChannel,
                     AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0)
    {
        ArgumentNullException.ThrowIfNull(bitStream);
        return Send((OutgoingMessage)bitStream, priority, reliability, orderingChannel, systemIdentifier, broadcast, forceReceiptNumber);
    }

    public uint SendList(in ReadOnlySequence<byte> data, Priority priority, Reliability reliability, byte orderingChannel,
                         AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0) =>
        Send(new OutgoingMessage(data), priority, reliability, orderingChannel, systemIdentifier, broadcast, forceReceiptNumber);

    /// <summary>RakPeer::SendLoopback; an empty message is ignored.</summary>
    public void SendLoopback(ReadOnlySpan<byte> data)
    {
        if (_peer is { IsActive: true } peer && !data.IsEmpty)
            peer.SendLoopback(data);
    }

    /// <summary>
    /// RakPeer::Receive: runs one I/O cycle, then returns the next packet a RakNet peer would return, or null.
    /// </summary>
    public Packet? Receive()
    {
        if (_peer is not { IsActive: true } peer)
            return null;
        try
        {
            peer.Update();
            while (peer.Events.TryRead(out var e))
            {
                if (e.IsRakNetPacket)
                    return new Packet(e.Detach());
            }
        }
        catch (ObjectDisposedException) when (!peer.IsActive)
        {
            // Shut down by another thread.
        }
        return null;
    }

    /// <summary>Idempotent; optional, because an undisposed packet is collected.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "RakPeerInterface::DeallocatePacket is an instance member, kept so ported calls read the same.")]
    public void DeallocatePacket(Packet? packet) => packet?.Dispose();

    public ushort GetMaximumNumberOfPeers() => (ushort)(_peer?.MaxConnections ?? 0);

    public void CloseConnection(AddressOrGuid target, bool sendDisconnectionNotification, byte orderingChannel = 0,
                                Priority disconnectionNotificationPriority = Priority.Low)
    {
        if (_peer is { IsActive: true } peer && !target.IsUndefined)
            peer.Close(target, sendDisconnectionNotification, orderingChannel, disconnectionNotificationPriority);
    }

    public ConnectionState GetConnectionState(AddressOrGuid systemIdentifier) =>
        _peer?.GetConnectionState(systemIdentifier) ?? ConnectionState.NotConnected;

    public void CancelConnectionAttempt(SystemAddress target)
    {
        if (_peer is { IsActive: true } peer)
            peer.CancelConnectionAttempt(target);
    }

    /// <summary>-1 when the address is not connected.</summary>
    public int GetIndexFromSystemAddress(SystemAddress systemAddress) =>
        _peer is not null && _peer.TryGetConnection(systemAddress, out var connection) ? connection.SystemIndex : -1;

    public SystemAddress GetSystemAddressFromIndex(int index) =>
        _peer is not null && _peer.TryGetConnectionByIndex(index, out var connection) ? connection.Address : SystemAddress.Unassigned;

    public RakNetGuid GetGUIDFromIndex(int index) =>
        _peer is not null && _peer.TryGetConnectionByIndex(index, out var connection) ? connection.RakNetGuid : RakNetGuid.Unassigned;

    /// <summary>GetSystemList: clears both collections, then adds every connected system.</summary>
    public void GetSystemList(ICollection<SystemAddress> addresses, ICollection<RakNetGuid> guids)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(guids);
        addresses.Clear();
        guids.Clear();
        foreach (var connection in ConnectedSystems())
        {
            addresses.Add(connection.Address);
            guids.Add(connection.RakNetGuid);
        }
    }

    public void AddToBanList(string ip, uint milliseconds = 0) => RequirePeer().BanList.Add(ip, TimeSpan.FromMilliseconds(milliseconds));

    public void RemoveFromBanList(string ip) => RequirePeer().BanList.Remove(ip);

    public void ClearBanList() => RequirePeer().BanList.Clear();

    public bool IsBanned(string ip) => _peer is not null && _peer.BanList.IsBanned(ip);

    public void SetLimitIPConnectionFrequency(bool value)
    {
        if (_peer is { IsActive: true } peer)
            peer.LimitIpConnectionFrequency = value;
        _pending = _pending with { LimitIpConnectionFrequency = value };
    }

    /// <summary>Ping(SystemAddress): a connected ping to a connected system.</summary>
    public void Ping(SystemAddress target)
    {
        if (_peer is not null && _peer.TryGetConnection(target, out var connection))
            connection.Ping();
    }

    /// <summary>Ping(host, port, ...): an unconnected ping; false when the peer is not started or the host does not resolve.</summary>
    public bool Ping(string host, ushort remotePort, bool onlyReplyOnAcceptingConnections, uint connectionSocketIndex = 0)
    {
        if (_peer is not { IsActive: true } peer || string.IsNullOrEmpty(host) || connectionSocketIndex >= (uint)peer.Sockets.Count)
            return false;
        return Resolve(host, remotePort) is { } remote && peer.PingUnconnected(remote, onlyReplyOnAcceptingConnections, (int)connectionSocketIndex);
    }

    /// <summary>-1 when there is no sample yet.</summary>
    public int GetAveragePing(AddressOrGuid systemIdentifier) => Milliseconds(ConnectionOf(systemIdentifier)?.AveragePing);

    /// <inheritdoc cref="GetAveragePing"/>
    public int GetLastPing(AddressOrGuid systemIdentifier) => Milliseconds(ConnectionOf(systemIdentifier)?.LastPing);

    /// <inheritdoc cref="GetAveragePing"/>
    public int GetLowestPing(AddressOrGuid systemIdentifier) => Milliseconds(ConnectionOf(systemIdentifier)?.LowestPing);

    public void SetOccasionalPing(bool doPing)
    {
        if (_peer is { IsActive: true } peer)
            peer.OccasionalPing = doPing;
        _pending = _pending with { OccasionalPing = doPing };
    }

    public long GetClockDifferential(AddressOrGuid systemIdentifier) => ConnectionOf(systemIdentifier)?.ClockDifferential ?? 0;

    public void SetOfflinePingResponse(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        if (_peer is { IsActive: true } peer)
            peer.OfflinePingResponse = copy;
        _pending = _pending with { OfflinePingResponse = copy };
    }

    public ReadOnlyMemory<byte> GetOfflinePingResponse() =>
        _peer is { IsActive: true } peer ? peer.OfflinePingResponse : _pending.OfflinePingResponse;

    /// <summary>For <see cref="SystemAddress.Unassigned"/>, this peer's own internal address.</summary>
    public SystemAddress GetInternalID(SystemAddress systemAddress = default, int index = 0)
    {
        if (_peer is null || index < 0)
            return SystemAddress.Unassigned;
        if (!systemAddress.IsAssigned)
            return index < _peer.InternalAddresses.Count ? _peer.InternalAddresses[index] : SystemAddress.Unassigned;
        return _peer.TryGetConnection(systemAddress, out var connection) && index < connection.RemoteInternalAddresses.Count
            ? connection.RemoteInternalAddresses[index]
            : SystemAddress.Unassigned;
    }

    public void SetInternalID(SystemAddress systemAddress, int index = 0) => RequirePeer().SetInternalAddress(systemAddress, index);

    /// <summary>GetExternalID: for <see cref="SystemAddress.Unassigned"/>, the first address a remote reported for this peer.</summary>
    public SystemAddress GetExternalID(SystemAddress target)
    {
        if (_peer is null)
            return SystemAddress.Unassigned;
        if (!target.IsAssigned)
            return _peer.ExternalAddress;
        return _peer.TryGetConnection(target, out var connection) ? connection.ExternalAddress : SystemAddress.Unassigned;
    }

    public RakNetGuid GetMyGUID() => _peer?.RakNetGuid ?? RakNetGuid.Unassigned;

    public SystemAddress GetMyBoundAddress(int socketIndex = 0) =>
        _peer is not null && socketIndex >= 0 && socketIndex < _peer.Sockets.Count ? _peer.Sockets[socketIndex].LocalAddress : SystemAddress.Unassigned;

    public static ulong Get64BitUniqueRandomNumber() => RandomSource.Shared.NextUInt64();

    public RakNetGuid GetGuidFromSystemAddress(SystemAddress input) => _peer?.GetGuidFromAddress(input) ?? RakNetGuid.Unassigned;

    public SystemAddress GetSystemAddressFromGuid(RakNetGuid input) => _peer?.GetAddressFromGuid(input) ?? SystemAddress.Unassigned;

    /// <summary>False when the system is not connected or sent no identity key.</summary>
    public bool GetClientPublicKeyFromSystemAddress(SystemAddress input, Span<byte> clientPublicKey)
    {
        if (_peer is null || !_peer.TryGetConnection(input, out var connection) || connection.RemoteIdentityKey is not { } key)
            return false;
        key.Bytes.CopyTo(clientPublicKey);
        return true;
    }

    /// <summary>SetTimeoutTime: <see cref="SystemAddress.Unassigned"/> sets the default and every connection.</summary>
    public void SetTimeoutTime(uint timeMS, SystemAddress target)
    {
        var timeout = TimeSpan.FromMilliseconds(timeMS);
        if (target.IsAssigned)
        {
            if (_peer is not null && _peer.TryGetConnection(target, out var connection))
                connection.Timeout = timeout;
            return;
        }
        if (_peer is { IsActive: true } peer)
            peer.DefaultTimeout = timeout;
        _pending = _pending with { DefaultTimeout = timeout };
    }

    /// <summary>The connection's timeout, or the default for <see cref="SystemAddress.Unassigned"/> or an unknown system.</summary>
    public uint GetTimeoutTime(SystemAddress target)
    {
        if (_peer is not null && target.IsAssigned && _peer.TryGetConnection(target, out var connection))
            return (uint)connection.Timeout.TotalMilliseconds;
        return (uint)(_peer?.DefaultTimeout ?? _pending.DefaultTimeout).TotalMilliseconds;
    }

    /// <summary>
    /// GetMTUSize: the connection's MTU, else defaultMTUSize (RakPeer.cpp:2572-2581), which is the smallest MTU tried
    /// (RakPeer.cpp:206).
    /// </summary>
    public int GetMTUSize(SystemAddress target) =>
        _peer is not null && target.IsAssigned && _peer.TryGetConnection(target, out var connection) ? connection.Mtu : RakNetConstants.MtuSizes[^1];

    public uint GetNumberOfAddresses() => (uint)(_peer?.LocalAddresses.Count ?? 0);

    /// <summary>The address as text, or an empty string when there is none at <paramref name="index"/>.</summary>
    public string GetLocalIP(uint index) =>
        _peer is not null && index < (uint)_peer.LocalAddresses.Count ? _peer.LocalAddresses[(int)index].ToString() : string.Empty;

    public bool IsLocalIP(string ip) => _peer is not null && IPAddress.TryParse(ip, out var address) && _peer.IsLocalAddress(address);

    public void AllowConnectionResponseIPMigration(bool allow)
    {
        if (_peer is { IsActive: true } peer)
            peer.AllowConnectionResponseIpMigration = allow;
        _pending = _pending with { AllowConnectionResponseIpMigration = allow };
    }

    /// <summary>AdvertiseSystem; false when the peer is not started or the host does not resolve.</summary>
    public bool AdvertiseSystem(string host, ushort remotePort, ReadOnlySpan<byte> data, uint connectionSocketIndex = 0) =>
        _peer is { IsActive: true } peer && !string.IsNullOrEmpty(host) && connectionSocketIndex < (uint)peer.Sockets.Count
        && Resolve(host, remotePort) is { } remote && peer.AdvertiseSystem(remote, data, (int)connectionSocketIndex);

    public void SetSplitMessageProgressInterval(int interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(interval);
        if (_peer is { IsActive: true } peer)
            peer.SplitMessageProgressInterval = interval;
        _pending = _pending with { SplitMessageProgressInterval = interval };
    }

    public int GetSplitMessageProgressInterval() => _peer?.SplitMessageProgressInterval ?? _pending.SplitMessageProgressInterval;

    public void SetUnreliableTimeout(uint timeoutMS)
    {
        var timeout = TimeSpan.FromMilliseconds(timeoutMS);
        if (_peer is { IsActive: true } peer)
            peer.UnreliableTimeout = timeout;
        _pending = _pending with { UnreliableTimeout = timeout };
    }

    public void SendTTL(string host, ushort remotePort, int ttl, uint connectionSocketIndex = 0)
    {
        if (_peer is { IsActive: true } peer && !string.IsNullOrEmpty(host) && connectionSocketIndex < (uint)peer.Sockets.Count
            && Resolve(host, remotePort) is { } remote)
            peer.LowLevel.SendTtl(remote, ttl, (int)connectionSocketIndex);
    }

    /// <summary>AttachPlugin: before Startup the plugin is attached at start and gets OnStartup, as in RakNet.</summary>
    public void AttachPlugin(IRakPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_peer is { IsActive: true } peer)
            peer.Plugins.Attach(plugin);
        else if (!_pendingPlugins.Contains(plugin))
            _pendingPlugins.Add(plugin);
    }

    public void DetachPlugin(IRakPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (_peer is { IsActive: true } peer)
            peer.Plugins.Detach(plugin);
        else
            _pendingPlugins.Remove(plugin);
    }

    /// <summary>Consumes <paramref name="packet"/>; it must not be used or deallocated afterwards.</summary>
    public void PushBackPacket(Packet packet, bool pushAtHead)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RequirePeer().Inject(packet.Take(), pushAtHead);
    }

    public void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress systemAddress) =>
        RequirePeer().LowLevel.ChangeSystemAddress(rakNetGuid, systemAddress);

    /// <summary>WriteOutOfBandHeader: the header with this peer's GUID, unassigned before Startup.</summary>
    public void WriteOutOfBandHeader(BitStream bitStream)
    {
        ArgumentNullException.ThrowIfNull(bitStream);
        bitStream.Write(new OutOfBandHeader(GetMyGUID()));
    }

    /// <summary>SendOutOfBand; false when the peer is not started or the host does not resolve (RakPeer.cpp:2999-3003).</summary>
    public bool SendOutOfBand(string host, ushort remotePort, ReadOnlySpan<byte> data, uint connectionSocketIndex = 0) =>
        _peer is { IsActive: true } peer && !string.IsNullOrEmpty(host) && connectionSocketIndex < (uint)peer.Sockets.Count
        && Resolve(host, remotePort) is { } remote && peer.LowLevel.SendOutOfBand(remote, data, (int)connectionSocketIndex);

    /// <summary>ApplyNetworkSimulator, with milliseconds as RakNet takes them.</summary>
    public void ApplyNetworkSimulator(float packetloss, ushort minExtraPing, ushort extraPingVariance)
    {
        var options = new NetworkSimulatorOptions(packetloss, TimeSpan.FromMilliseconds(minExtraPing), TimeSpan.FromMilliseconds(extraPingVariance));
        if (_peer is { IsActive: true } peer)
            peer.LowLevel.NetworkSimulator = options;
        _pending = _pending with { NetworkSimulator = options };
    }

    public bool IsNetworkSimulatorActive() => _peer?.LowLevel.IsNetworkSimulatorActive ?? false;

    public void SetPerConnectionOutgoingBandwidthLimit(uint maxBitsPerSecond)
    {
        if (_peer is { IsActive: true } peer)
            peer.PerConnectionOutgoingBandwidthLimit = maxBitsPerSecond;
        _pending = _pending with { PerConnectionOutgoingBandwidthLimit = maxBitsPerSecond };
    }

    /// <summary>
    /// Null when <paramref name="systemAddress"/> is not connected; for <see cref="SystemAddress.Unassigned"/>, the sum
    /// over every connection, as RakNet adds them up (RakPeer.cpp:3051-3094).
    /// </summary>
    public RakNetStatistics? GetStatistics(SystemAddress systemAddress)
    {
        if (_peer is null)
            return null;
        if (systemAddress.IsAssigned)
            return _peer.TryGetConnection(systemAddress, out var connection) ? connection.GetStatistics() : null;
        var sum = default(RakNetStatistics);
        foreach (var connection in ConnectedSystems())
            sum += connection.GetStatistics();
        return sum;
    }

    public bool GetStatistics(int index, out RakNetStatistics rns)
    {
        if (_peer is not null && _peer.TryGetConnectionByIndex(index, out var connection))
        {
            rns = connection.GetStatistics();
            return true;
        }
        rns = default;
        return false;
    }

    /// <summary>GetStatisticsList: clears the collections, then adds every connected system.</summary>
    public void GetStatisticsList(ICollection<SystemAddress> addresses, ICollection<RakNetGuid> guids,
                                  ICollection<RakNetStatistics> statistics)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(guids);
        ArgumentNullException.ThrowIfNull(statistics);
        addresses.Clear();
        guids.Clear();
        statistics.Clear();
        foreach (var connection in ConnectedSystems())
        {
            addresses.Add(connection.Address);
            guids.Add(connection.RakNetGuid);
            statistics.Add(connection.GetStatistics());
        }
    }

    public uint GetReceiveBufferSize() => (uint)(_peer?.Events.Count ?? 0);

    public void SetIncomingDatagramEventHandler(IncomingDatagramFilter? handler)
    {
        _pendingFilter = handler;
        if (_peer is { IsActive: true } peer)
            peer.LowLevel.IncomingDatagramFilter = handler;
    }

    /// <summary>DestroyInstance.</summary>
    public void Dispose() => _peer?.Dispose();

    private uint Send(OutgoingMessage message, Priority priority, Reliability reliability, byte orderingChannel,
                      AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber)
    {
        // RakNet asserts on these in debug builds and returns 0 or misbehaves in release ones.
        if (_peer is not { IsActive: true } peer || message.BitLength == 0 || orderingChannel >= 32
            || (uint)reliability >= ReliabilityExtensions.NumberOfReliabilities || priority > Priority.Low
            || (!broadcast && systemIdentifier.IsUndefined))
            return 0;
        var receipt = new SendReceipt(forceReceiptNumber);
        var result = broadcast
            ? peer.Broadcast(message, reliability, priority, orderingChannel, systemIdentifier, receipt)
            : peer.Send(message, systemIdentifier, reliability, priority, orderingChannel, receipt);
        return result.Receipt.Value;
    }

    private IEnumerable<RakConnection> ConnectedSystems() =>
        _peer is null ? [] : _peer.Connections.Where(c => c.State == ConnectionState.Connected);

    private RakConnection? ConnectionOf(AddressOrGuid target)
    {
        if (_peer is null || target.IsUndefined)
            return null;
        if (target.RakNetGuid.IsAssigned)
            return _peer.TryGetConnection(target.RakNetGuid, out var byGuid) ? byGuid : null;
        return _peer.TryGetConnection(target.Address, out var byAddress) ? byAddress : null;
    }

    private RakPeer RequirePeer() =>
        _peer ?? throw new InvalidOperationException("Startup has not been called.");

    private static int Milliseconds(TimeSpan? ping) => ping is { } value ? (int)value.TotalMilliseconds : -1;

    // RakNet resolves on the caller (SystemAddress::FromStringExplicitPort); IPv4 only, as RAKNET_SUPPORT_IPV6 is 0.
    private static SystemAddress? Resolve(string host, ushort port)
    {
        if (IPAddress.TryParse(host, out var literal))
            return new SystemAddress(literal, port);
        try
        {
            var address = Array.Find(Dns.GetHostAddresses(host), a => a.AddressFamily == AddressFamily.InterNetwork);
            return address is null ? null : (SystemAddress?)new SystemAddress(address, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
