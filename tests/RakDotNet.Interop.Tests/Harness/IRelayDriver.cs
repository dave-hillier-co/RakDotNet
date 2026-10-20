using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Drives a UDPForwarder of the peer's process and the relaying plugins on the peer: the udpForwarder*, udpProxy*,
/// router2* and relay* commands of docs/interop-protocol.md. Both drivers implement it; every plugin command but the
/// attach commands fails until that plugin is attached. Router2 and RelayPlugin results arrive as packets; the UDPProxy
/// result handlers' calls are read with <see cref="GetUdpProxyCallbacksAsync"/>.
/// </summary>
public interface IRelayDriver
{
    /// <summary>Creates the process's UDPForwarder if need be, sets its maximum entries when given, and starts it.</summary>
    Task StartUdpForwarderAsync(int? maxEntries = null, CancellationToken cancellationToken = default);

    /// <summary>StartForwarding; the UDPForwarderResult name and the port.</summary>
    Task<(string Result, int Port)> StartForwardingAsync(IPEndPoint source, IPEndPoint destination, TimeSpan timeout, string? forceHost = null,
                                                         CancellationToken cancellationToken = default);

    /// <summary>StopForwarding.</summary>
    Task StopForwardingAsync(IPEndPoint source, IPEndPoint destination, CancellationToken cancellationToken = default);

    /// <summary>GetMaxForwardEntries and GetUsedForwardEntries.</summary>
    Task<(int MaxEntries, int UsedEntries)> GetUdpForwarderInfoAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a UDPProxyCoordinator and SetRemoteLoginPassword.</summary>
    Task AttachUdpProxyCoordinatorAsync(string password, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a UDPProxyServer with SetServerPublicIP, and SetMaxForwardEntries when given.</summary>
    Task AttachUdpProxyServerAsync(string publicIp = "", int? maxForwardEntries = null, CancellationToken cancellationToken = default);

    /// <summary>LoginToCoordinator at <paramref name="coordinator"/>, or at GetInternalID(UNASSIGNED_SYSTEM_ADDRESS, 0) when null.</summary>
    Task<bool> LoginToCoordinatorAsync(string password, IPEndPoint? coordinator = null, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a UDPProxyClient.</summary>
    Task AttachUdpProxyClientAsync(CancellationToken cancellationToken = default);

    /// <summary>RequestForwarding to the system with <paramref name="target"/>'s GUID, from <paramref name="source"/> or this system.</summary>
    Task<bool> RequestForwardingAsync(IPEndPoint coordinator, ulong target, TimeSpan timeout, IPEndPoint? source = null,
                                      CancellationToken cancellationToken = default);

    /// <summary>RequestForwarding to the system at <paramref name="targetAddress"/>.</summary>
    Task<bool> RequestForwardingToAddressAsync(IPEndPoint coordinator, IPEndPoint targetAddress, TimeSpan timeout, IPEndPoint? source = null,
                                               CancellationToken cancellationToken = default);

    /// <summary>Every call the UDPProxyClient's and UDPProxyServer's result handlers had, in order.</summary>
    Task<IReadOnlyList<UdpProxyCallback>> GetUdpProxyCallbacksAsync(CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a Router2, and SetMaximumForwardingRequests when given.</summary>
    Task AttachRouter2Async(int? maxForwardingRequests = null, CancellationToken cancellationToken = default);

    /// <summary>EstablishRouting to the system with <paramref name="target"/>'s GUID.</summary>
    Task EstablishRoutingAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a RelayPlugin and SetAcceptAddParticipantRequests.</summary>
    Task AttachRelayAsync(bool acceptAddParticipantRequests, CancellationToken cancellationToken = default);

    /// <summary>AddParticipantOnServer; the RelayPluginEnums name.</summary>
    Task<string> AddParticipantOnServerAsync(string key, ulong target, CancellationToken cancellationToken = default);

    /// <summary>RemoveParticipantOnServer.</summary>
    Task RemoveParticipantOnServerAsync(ulong target, CancellationToken cancellationToken = default);

    /// <summary>AddParticipantRequestFromClient.</summary>
    Task AddParticipantRequestAsync(string key, ulong server, CancellationToken cancellationToken = default);

    /// <summary>RemoveParticipantRequestFromClient.</summary>
    Task RemoveParticipantRequestAsync(ulong server, CancellationToken cancellationToken = default);

    /// <summary>SendToParticipant of the first <paramref name="bits"/> of <paramref name="data"/>, most significant first.</summary>
    Task SendToParticipantAsync(ulong server, string key, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                                int channel = 0, CancellationToken cancellationToken = default);

    /// <summary>SendGroupMessage of the first <paramref name="bits"/> of <paramref name="data"/>.</summary>
    Task SendGroupMessageAsync(ulong server, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                               int channel = 0, CancellationToken cancellationToken = default);

    /// <summary>JoinGroupRequest.</summary>
    Task JoinGroupAsync(ulong server, string group, CancellationToken cancellationToken = default);

    /// <summary>LeaveGroup.</summary>
    Task LeaveGroupAsync(ulong server, CancellationToken cancellationToken = default);

    /// <summary>GetGroupList.</summary>
    Task GetGroupListAsync(ulong server, CancellationToken cancellationToken = default);
}

/// <summary>
/// One call of a UDPProxyClientResultHandler or UDPProxyServerResultHandler. Addresses and the proxy's are null for the
/// server's login callbacks, the password null for the client's.
/// </summary>
public sealed record UdpProxyCallback(string Name, string? ProxyIp = null, int ProxyPort = 0, IPEndPoint? Coordinator = null, IPEndPoint? Source = null,
                                      IPEndPoint? Target = null, ulong TargetGuid = 0, string? Password = null);
