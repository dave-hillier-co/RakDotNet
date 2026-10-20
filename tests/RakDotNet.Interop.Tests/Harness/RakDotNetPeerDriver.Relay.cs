using System.Collections.Concurrent;
using System.Net;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The udpForwarder*, udpProxy*, router2* and relay* commands, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : IRelayDriver
{
    private readonly ProxyResultRecorder _proxyResults = new();
    private UdpForwarder? _udpForwarder;
    private UdpProxyCoordinator? _udpProxyCoordinator;
    private UdpProxyServer? _udpProxyServer;
    private UdpProxyClient? _udpProxyClient;
    private Router2? _router2;
    private RelayPlugin? _relay;

    /// <summary>What Router2's debug interface reported.</summary>
    public IReadOnlyCollection<string> Router2DebugMessages => _router2Debug.ToArray();

    private readonly ConcurrentQueue<string> _router2Debug = new();

    public Task StartUdpForwarderAsync(int? maxEntries = null, CancellationToken cancellationToken = default)
    {
        // A forwarder of the process's own, on the network the peer is on.
        _udpForwarder ??= new UdpForwarder(TransportFactory);
        if (maxEntries is { } max)
            _udpForwarder.MaxForwardEntries = max;
        _udpForwarder.Startup();
        return Task.CompletedTask;
    }

    public Task<(string Result, int Port)> StartForwardingAsync(IPEndPoint source, IPEndPoint destination, TimeSpan timeout, string? forceHost = null,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var forwarding = RequireForwarder().StartForwarding(source, destination, timeout, forceHost);
        return Task.FromResult((ForwarderResultName(forwarding.Result), (int)forwarding.ForwardingPort));
    }

    public Task StopForwardingAsync(IPEndPoint source, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        RequireForwarder().StopForwarding(source, destination);
        return Task.CompletedTask;
    }

    public Task<(int MaxEntries, int UsedEntries)> GetUdpForwarderInfoAsync(CancellationToken cancellationToken = default)
    {
        var forwarder = RequireForwarder();
        return Task.FromResult((forwarder.MaxForwardEntries, forwarder.UsedForwardEntries));
    }

    public async Task AttachUdpProxyCoordinatorAsync(string password, CancellationToken cancellationToken = default)
    {
        var peer = Started("udpProxyCoordinatorAttach");
        if (_udpProxyCoordinator is null)
        {
            _udpProxyCoordinator = new UdpProxyCoordinator();
            await AttachAsync(peer, _udpProxyCoordinator, cancellationToken).ConfigureAwait(false);
        }
        _udpProxyCoordinator.RemoteLoginPassword = password;
    }

    public async Task AttachUdpProxyServerAsync(string publicIp = "", int? maxForwardEntries = null, CancellationToken cancellationToken = default)
    {
        var peer = Started("udpProxyServerAttach");
        if (_udpProxyServer is null)
        {
            _udpProxyServer = new UdpProxyServer { ResultHandler = _proxyResults };
            await AttachAsync(peer, _udpProxyServer, cancellationToken).ConfigureAwait(false);
        }
        _udpProxyServer.ServerPublicIp = publicIp;
        if (maxForwardEntries is { } max)
            _udpProxyServer.MaxForwardEntries = max;
    }

    public Task<bool> LoginToCoordinatorAsync(string password, IPEndPoint? coordinator = null, CancellationToken cancellationToken = default)
    {
        var server = _udpProxyServer ?? throw new PeerCommandException("udpProxyServerLogin", "no UDPProxyServer is attached; send udpProxyServerAttach first");
        // NATCompleteServer logs into a coordinator on the same peer at GetInternalID(UNASSIGNED_SYSTEM_ADDRESS, 0).
        var address = coordinator is not null ? (SystemAddress)coordinator : Started("udpProxyServerLogin").InternalAddresses[0];
        return Task.FromResult(server.LoginToCoordinator(password, address));
    }

    public async Task AttachUdpProxyClientAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("udpProxyClientAttach");
        if (_udpProxyClient is not null)
            return;
        _udpProxyClient = new UdpProxyClient { ResultHandler = _proxyResults };
        await AttachAsync(peer, _udpProxyClient, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> RequestForwardingAsync(IPEndPoint coordinator, ulong target, TimeSpan timeout, IPEndPoint? source = null,
                                             CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        return Task.FromResult(RequireProxyClient().RequestForwarding(coordinator, Address(source), new RakNetGuid(target), timeout));
    }

    public Task<bool> RequestForwardingToAddressAsync(IPEndPoint coordinator, IPEndPoint targetAddress, TimeSpan timeout, IPEndPoint? source = null,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(targetAddress);
        return Task.FromResult(RequireProxyClient().RequestForwarding(coordinator, Address(source), (SystemAddress)targetAddress, timeout));
    }

    public Task<IReadOnlyList<UdpProxyCallback>> GetUdpProxyCallbacksAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UdpProxyCallback>>(_proxyResults.Calls);

    public async Task AttachRouter2Async(int? maxForwardingRequests = null, CancellationToken cancellationToken = default)
    {
        var peer = Started("router2Attach");
        if (_router2 is null)
        {
            _router2 = new Router2 { FailureMessage = _router2Debug.Enqueue, DiagnosticMessage = _router2Debug.Enqueue };
            await AttachAsync(peer, _router2, cancellationToken).ConfigureAwait(false);
        }
        if (maxForwardingRequests is { } max)
            _router2.MaximumForwardingRequests = max;
    }

    public Task EstablishRoutingAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var router2 = _router2 ?? throw new PeerCommandException("router2EstablishRouting", "no Router2 is attached; send router2Attach first");
        router2.EstablishRouting(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public async Task AttachRelayAsync(bool acceptAddParticipantRequests, CancellationToken cancellationToken = default)
    {
        var peer = Started("relayAttach");
        if (_relay is null)
        {
            _relay = new RelayPlugin();
            await AttachAsync(peer, _relay, cancellationToken).ConfigureAwait(false);
        }
        _relay.AcceptAddParticipantRequests = acceptAddParticipantRequests;
    }

    public Task<string> AddParticipantOnServerAsync(string key, ulong target, CancellationToken cancellationToken = default) =>
        Task.FromResult(RelayResultName(RequireRelay().AddParticipantOnServer(key, new RakNetGuid(target))));

    public Task RemoveParticipantOnServerAsync(ulong target, CancellationToken cancellationToken = default)
    {
        RequireRelay().RemoveParticipantOnServer(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task AddParticipantRequestAsync(string key, ulong server, CancellationToken cancellationToken = default)
    {
        RequireRelay().AddParticipantRequestFromClient(key, new RakNetGuid(server));
        return Task.CompletedTask;
    }

    public Task RemoveParticipantRequestAsync(ulong server, CancellationToken cancellationToken = default)
    {
        RequireRelay().RemoveParticipantRequestFromClient(new RakNetGuid(server));
        return Task.CompletedTask;
    }

    public Task SendToParticipantAsync(ulong server, string key, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                                       int channel = 0, CancellationToken cancellationToken = default)
    {
        var relay = RequireRelay();
        using var stream = Bits(data, bits);
        relay.SendToParticipant(new RakNetGuid(server), key, stream, priority, reliability, (byte)channel);
        return Task.CompletedTask;
    }

    public Task SendGroupMessageAsync(ulong server, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                                      int channel = 0, CancellationToken cancellationToken = default)
    {
        var relay = RequireRelay();
        using var stream = Bits(data, bits);
        relay.SendGroupMessage(new RakNetGuid(server), stream, priority, reliability, (byte)channel);
        return Task.CompletedTask;
    }

    public Task JoinGroupAsync(ulong server, string group, CancellationToken cancellationToken = default)
    {
        RequireRelay().JoinGroupRequest(new RakNetGuid(server), group);
        return Task.CompletedTask;
    }

    public Task LeaveGroupAsync(ulong server, CancellationToken cancellationToken = default)
    {
        RequireRelay().LeaveGroup(new RakNetGuid(server));
        return Task.CompletedTask;
    }

    public Task GetGroupListAsync(ulong server, CancellationToken cancellationToken = default)
    {
        RequireRelay().GetGroupList(new RakNetGuid(server));
        return Task.CompletedTask;
    }

    // The first bits of the data, most significant first, as interop-peer's WriteBits(data, bits, false).
    private static BitStream Bits(byte[] data, int bits)
    {
        if (bits < 0 || bits > data.Length * 8)
            throw new PeerCommandException("relay", "field 'bits' is out of range");
        var stream = new BitStream(data.Length);
        var writer = stream.BeginWrite(data.Length);
        writer.WriteBits(data, bits, rightAlignedBits: false);
        stream.EndWrite(writer);
        return stream;
    }

    private static SystemAddress Address(IPEndPoint? endPoint) => endPoint is null ? SystemAddress.Unassigned : endPoint;

    private static IPEndPoint? EndPoint(SystemAddress address) => address.IsAssigned ? address.ToIPEndPoint() : null;

    private UdpForwarder RequireForwarder() =>
        _udpForwarder ?? throw new PeerCommandException("udpForwarder", "no UDPForwarder is running; send udpForwarderStartup first");

    private UdpProxyClient RequireProxyClient() =>
        _udpProxyClient ?? throw new PeerCommandException("udpProxyClientRequest", "no UDPProxyClient is attached; send udpProxyClientAttach first");

    private RelayPlugin RequireRelay() =>
        _relay ?? throw new PeerCommandException("relay", "no RelayPlugin is attached; send relayAttach first");

    private static string ForwarderResultName(UdpForwarderResult result) => result switch
    {
        UdpForwarderResult.ForwardingAlreadyExists => "UDPFORWARDER_FORWARDING_ALREADY_EXISTS",
        UdpForwarderResult.NoSockets => "UDPFORWARDER_NO_SOCKETS",
        UdpForwarderResult.BindFailed => "UDPFORWARDER_BIND_FAILED",
        UdpForwarderResult.InvalidParameters => "UDPFORWARDER_INVALID_PARAMETERS",
        UdpForwarderResult.NotRunning => "UDPFORWARDER_NOT_RUNNING",
        UdpForwarderResult.Success => "UDPFORWARDER_SUCCESS",
        _ => "UDPFORWARDER_RESULT_COUNT",
    };

    private static string RelayResultName(RelayPluginMessage result) => result switch
    {
        RelayPluginMessage.AddClientNotAllowed => "RPE_ADD_CLIENT_NOT_ALLOWED",
        RelayPluginMessage.AddClientTargetNotConnected => "RPE_ADD_CLIENT_TARGET_NOT_CONNECTED",
        RelayPluginMessage.AddClientNameAlreadyInUse => "RPE_ADD_CLIENT_NAME_ALREADY_IN_USE",
        RelayPluginMessage.AddClientSuccess => "RPE_ADD_CLIENT_SUCCESS",
        _ => "RPE_UNKNOWN",
    };

    /// <summary>interop-peer's ProxyResultRecorder.</summary>
    private sealed class ProxyResultRecorder : IUdpProxyClientResultHandler, IUdpProxyServerResultHandler
    {
        private readonly ConcurrentQueue<UdpProxyCallback> _calls = new();

        public IReadOnlyList<UdpProxyCallback> Calls => [.. _calls];

        public void OnForwardingSuccess(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                        SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Forwarding("OnForwardingSuccess", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnForwardingNotification(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                             SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Forwarding("OnForwardingNotification", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnForwardingInProgress(string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                           SystemAddress targetAddress, RakNetGuid targetGuid, UdpProxyClient proxyClient) =>
            Forwarding("OnForwardingInProgress", proxyIPAddress, proxyPort, proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnNoServersOnline(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                      UdpProxyClient proxyClient) =>
            Failure("OnNoServersOnline", proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnRecipientNotConnected(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                            UdpProxyClient proxyClient) =>
            Failure("OnRecipientNotConnected", proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnAllServersBusy(SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid,
                                     UdpProxyClient proxyClient) =>
            Failure("OnAllServersBusy", proxyCoordinator, sourceAddress, targetAddress, targetGuid);

        public void OnLoginSuccess(string usedPassword, UdpProxyServer proxyServer) => _calls.Enqueue(new UdpProxyCallback("OnLoginSuccess", Password: usedPassword));

        public void OnAlreadyLoggedIn(string usedPassword, UdpProxyServer proxyServer) => _calls.Enqueue(new UdpProxyCallback("OnAlreadyLoggedIn", Password: usedPassword));

        public void OnNoPasswordSet(string usedPassword, UdpProxyServer proxyServer) => _calls.Enqueue(new UdpProxyCallback("OnNoPasswordSet", Password: usedPassword));

        public void OnWrongPassword(string usedPassword, UdpProxyServer proxyServer) => _calls.Enqueue(new UdpProxyCallback("OnWrongPassword", Password: usedPassword));

        private void Forwarding(string name, string proxyIPAddress, ushort proxyPort, SystemAddress proxyCoordinator, SystemAddress sourceAddress,
                                SystemAddress targetAddress, RakNetGuid targetGuid) =>
            _calls.Enqueue(new UdpProxyCallback(name, proxyIPAddress, proxyPort, EndPoint(proxyCoordinator), EndPoint(sourceAddress), EndPoint(targetAddress), targetGuid.Value));

        private void Failure(string name, SystemAddress proxyCoordinator, SystemAddress sourceAddress, SystemAddress targetAddress, RakNetGuid targetGuid) =>
            _calls.Enqueue(new UdpProxyCallback(name, Coordinator: EndPoint(proxyCoordinator), Source: EndPoint(sourceAddress), Target: EndPoint(targetAddress), TargetGuid: targetGuid.Value));
    }
}
