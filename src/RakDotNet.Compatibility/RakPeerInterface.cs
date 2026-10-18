using System.Buffers;
using RakDotNet.Protocol.Wire;
using RakDotNet.Security;

namespace RakDotNet.Compatibility;

/// <summary>
/// A RakPeerInterface-shaped facade over one <see cref="RakPeer"/>, with RakNet's names and shapes, for code ported
/// from RakNet or Brutal.RakNet. It implements docs/api-design.md's RakPeerInterface mapping in reverse.
/// </summary>
/// <remarks>
/// The peer runs in <see cref="PeerThreading.Manual"/>: <see cref="Receive"/> runs one update, reads, skips events
/// whose <see cref="PeerEvent.IsRakNetPacket"/> is false, and detaches the rest into a <see cref="Packet"/>. Hostnames
/// are strings. No plugin may depend on this assembly.
/// </remarks>
public sealed class RakPeerInterface : IDisposable
{
    /// <summary>The peer behind the facade; null until <see cref="Startup"/>.</summary>
    public RakPeer? Peer => throw new NotImplementedException();

    public static RakPeerInterface GetInstance() => throw new NotImplementedException();

    public static void DestroyInstance(RakPeerInterface instance) => throw new NotImplementedException();

    /// <summary>Thread priority is not configurable, so RakNet's threadPriority argument is absent.</summary>
    public StartupResult Startup(int maxConnections, ReadOnlySpan<SocketDescriptor> socketDescriptors) => throw new NotImplementedException();

    public bool InitializeSecurity(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> privateKey, bool requireClientKey = false) =>
        throw new NotImplementedException();

    public void DisableSecurity() => throw new NotImplementedException();

    public void AddToSecurityExceptionList(string ip) => throw new NotImplementedException();

    /// <summary>A null <paramref name="ip"/> clears the list.</summary>
    public void RemoveFromSecurityExceptionList(string? ip) => throw new NotImplementedException();

    public bool IsInSecurityExceptionList(string ip) => throw new NotImplementedException();

    public void SetMaximumIncomingConnections(ushort numberAllowed) => throw new NotImplementedException();

    public ushort GetMaximumIncomingConnections() => throw new NotImplementedException();

    public ushort NumberOfConnections() => throw new NotImplementedException();

    public void SetIncomingPassword(ReadOnlySpan<byte> passwordData) => throw new NotImplementedException();

    public ReadOnlyMemory<byte> GetIncomingPassword() => throw new NotImplementedException();

    public ConnectionAttemptResult Connect(string host, ushort remotePort, ReadOnlySpan<byte> passwordData = default,
                                           ConnectSecurity? publicKey = null, uint connectionSocketIndex = 0,
                                           uint sendConnectionAttemptCount = 12, uint timeBetweenSendConnectionAttemptsMS = 500,
                                           uint timeoutTime = 0) => throw new NotImplementedException();

    public void Shutdown(uint blockDuration, byte orderingChannel = 0, Priority disconnectionNotificationPriority = Priority.Low) =>
        throw new NotImplementedException();

    public bool IsActive() => throw new NotImplementedException();

    /// <summary>Fills <paramref name="remoteSystems"/> and returns the number of connected systems.</summary>
    public int GetConnectionList(Span<SystemAddress> remoteSystems) => throw new NotImplementedException();

    public uint GetNextSendReceipt() => throw new NotImplementedException();

    public uint IncrementNextSendReceipt() => throw new NotImplementedException();

    public uint Send(ReadOnlySpan<byte> data, Priority priority, Reliability reliability, byte orderingChannel,
                     AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0) =>
        throw new NotImplementedException();

    public uint Send(BitStream bitStream, Priority priority, Reliability reliability, byte orderingChannel,
                     AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0) =>
        throw new NotImplementedException();

    public uint SendList(in ReadOnlySequence<byte> data, Priority priority, Reliability reliability, byte orderingChannel,
                         AddressOrGuid systemIdentifier, bool broadcast, uint forceReceiptNumber = 0) =>
        throw new NotImplementedException();

    public void SendLoopback(ReadOnlySpan<byte> data) => throw new NotImplementedException();

    public Packet? Receive() => throw new NotImplementedException();

    /// <summary>Idempotent; optional, because an undisposed packet is collected.</summary>
    public void DeallocatePacket(Packet? packet) => throw new NotImplementedException();

    public ushort GetMaximumNumberOfPeers() => throw new NotImplementedException();

    public void CloseConnection(AddressOrGuid target, bool sendDisconnectionNotification, byte orderingChannel = 0,
                                Priority disconnectionNotificationPriority = Priority.Low) => throw new NotImplementedException();

    public ConnectionState GetConnectionState(AddressOrGuid systemIdentifier) => throw new NotImplementedException();

    public void CancelConnectionAttempt(SystemAddress target) => throw new NotImplementedException();

    public int GetIndexFromSystemAddress(SystemAddress systemAddress) => throw new NotImplementedException();

    public SystemAddress GetSystemAddressFromIndex(int index) => throw new NotImplementedException();

    public RakNetGuid GetGUIDFromIndex(int index) => throw new NotImplementedException();

    public void GetSystemList(ICollection<SystemAddress> addresses, ICollection<RakNetGuid> guids) => throw new NotImplementedException();

    public void AddToBanList(string ip, uint milliseconds = 0) => throw new NotImplementedException();

    public void RemoveFromBanList(string ip) => throw new NotImplementedException();

    public void ClearBanList() => throw new NotImplementedException();

    public bool IsBanned(string ip) => throw new NotImplementedException();

    public void SetLimitIPConnectionFrequency(bool value) => throw new NotImplementedException();

    public void Ping(SystemAddress target) => throw new NotImplementedException();

    public bool Ping(string host, ushort remotePort, bool onlyReplyOnAcceptingConnections, uint connectionSocketIndex = 0) =>
        throw new NotImplementedException();

    /// <summary>-1 when there is no sample yet.</summary>
    public int GetAveragePing(AddressOrGuid systemIdentifier) => throw new NotImplementedException();

    /// <inheritdoc cref="GetAveragePing"/>
    public int GetLastPing(AddressOrGuid systemIdentifier) => throw new NotImplementedException();

    /// <inheritdoc cref="GetAveragePing"/>
    public int GetLowestPing(AddressOrGuid systemIdentifier) => throw new NotImplementedException();

    public void SetOccasionalPing(bool doPing) => throw new NotImplementedException();

    public long GetClockDifferential(AddressOrGuid systemIdentifier) => throw new NotImplementedException();

    public void SetOfflinePingResponse(ReadOnlySpan<byte> data) => throw new NotImplementedException();

    public ReadOnlyMemory<byte> GetOfflinePingResponse() => throw new NotImplementedException();

    /// <summary>For <see cref="SystemAddress.Unassigned"/>, this peer's own internal address.</summary>
    public SystemAddress GetInternalID(SystemAddress systemAddress = default, int index = 0) => throw new NotImplementedException();

    public void SetInternalID(SystemAddress systemAddress, int index = 0) => throw new NotImplementedException();

    public SystemAddress GetExternalID(SystemAddress target) => throw new NotImplementedException();

    public RakNetGuid GetMyGUID() => throw new NotImplementedException();

    public SystemAddress GetMyBoundAddress(int socketIndex = 0) => throw new NotImplementedException();

    public static ulong Get64BitUniqueRandomNumber() => throw new NotImplementedException();

    public RakNetGuid GetGuidFromSystemAddress(SystemAddress input) => throw new NotImplementedException();

    public SystemAddress GetSystemAddressFromGuid(RakNetGuid input) => throw new NotImplementedException();

    public bool GetClientPublicKeyFromSystemAddress(SystemAddress input, Span<byte> clientPublicKey) => throw new NotImplementedException();

    public void SetTimeoutTime(uint timeMS, SystemAddress target) => throw new NotImplementedException();

    public uint GetTimeoutTime(SystemAddress target) => throw new NotImplementedException();

    public int GetMTUSize(SystemAddress target) => throw new NotImplementedException();

    public uint GetNumberOfAddresses() => throw new NotImplementedException();

    public string GetLocalIP(uint index) => throw new NotImplementedException();

    public bool IsLocalIP(string ip) => throw new NotImplementedException();

    public void AllowConnectionResponseIPMigration(bool allow) => throw new NotImplementedException();

    public bool AdvertiseSystem(string host, ushort remotePort, ReadOnlySpan<byte> data, uint connectionSocketIndex = 0) =>
        throw new NotImplementedException();

    public void SetSplitMessageProgressInterval(int interval) => throw new NotImplementedException();

    public int GetSplitMessageProgressInterval() => throw new NotImplementedException();

    public void SetUnreliableTimeout(uint timeoutMS) => throw new NotImplementedException();

    public void SendTTL(string host, ushort remotePort, int ttl, uint connectionSocketIndex = 0) => throw new NotImplementedException();

    public void AttachPlugin(IRakPlugin plugin) => throw new NotImplementedException();

    public void DetachPlugin(IRakPlugin plugin) => throw new NotImplementedException();

    /// <summary>Consumes <paramref name="packet"/>; it must not be used or deallocated afterwards.</summary>
    public void PushBackPacket(Packet packet, bool pushAtHead) => throw new NotImplementedException();

    public void ChangeSystemAddress(RakNetGuid rakNetGuid, SystemAddress systemAddress) => throw new NotImplementedException();

    public void WriteOutOfBandHeader(BitStream bitStream) => throw new NotImplementedException();

    public bool SendOutOfBand(string host, ushort remotePort, ReadOnlySpan<byte> data, uint connectionSocketIndex = 0) =>
        throw new NotImplementedException();

    public void ApplyNetworkSimulator(float packetloss, ushort minExtraPing, ushort extraPingVariance) => throw new NotImplementedException();

    public bool IsNetworkSimulatorActive() => throw new NotImplementedException();

    public void SetPerConnectionOutgoingBandwidthLimit(uint maxBitsPerSecond) => throw new NotImplementedException();

    /// <summary>Null when <paramref name="systemAddress"/> is not connected.</summary>
    public RakNetStatistics? GetStatistics(SystemAddress systemAddress) => throw new NotImplementedException();

    public bool GetStatistics(int index, out RakNetStatistics rns) => throw new NotImplementedException();

    public void GetStatisticsList(ICollection<SystemAddress> addresses, ICollection<RakNetGuid> guids,
                                  ICollection<RakNetStatistics> statistics) => throw new NotImplementedException();

    public uint GetReceiveBufferSize() => throw new NotImplementedException();

    public void SetIncomingDatagramEventHandler(IncomingDatagramFilter? handler) => throw new NotImplementedException();

    /// <summary>DestroyInstance.</summary>
    public void Dispose() => throw new NotImplementedException();
}
