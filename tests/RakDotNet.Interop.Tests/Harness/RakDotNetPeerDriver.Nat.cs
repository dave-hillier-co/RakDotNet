using System.Collections.Concurrent;
using System.Net;
using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The nat* commands on the NAT plugins, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : INatDriver
{
    private readonly ConcurrentQueue<string> _natDebugMessages = new();
    private IPEndPoint? _natGateway;
    private NatTypeDetectionClient? _natTypeDetectionClient;
    private NatTypeDetectionServer? _natTypeDetectionServer;
    private NatPunchthroughClient? _natPunchthroughClient;
    private NatPunchthroughServer? _natPunchthroughServer;

    /// <summary>What the NatPunchthroughClient's debug interface reported.</summary>
    public IReadOnlyCollection<string> NatDebugMessages => _natDebugMessages.ToArray();

    // The transport the peer starts with: behind the router of natGateway, or plain UDP.
    private IDatagramTransportFactory TransportFactory =>
        _natGateway is { } gateway ? new NatGatewayTransportFactory(gateway) : UdpTransportFactory.Instance;

    public Task SetNatGatewayAsync(IPEndPoint? gateway, CancellationToken cancellationToken = default)
    {
        // The peer's sockets are bound by its transport factory at start (docs/api-design.md decision 11.14).
        if (_peer is not null)
            throw new PeerCommandException("natGateway", "a RakDotNet peer takes natGateway only before startup");
        _natGateway = gateway;
        return Task.CompletedTask;
    }

    public async Task AttachNatTypeDetectionClientAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("natTypeDetectionClientAttach");
        if (_natTypeDetectionClient is not null)
            return;
        _natTypeDetectionClient = new NatTypeDetectionClient();
        await AttachAsync(peer, _natTypeDetectionClient, cancellationToken).ConfigureAwait(false);
    }

    public Task DetectNatTypeAsync(IPEndPoint server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        RequireNatTypeDetectionClient().DetectNatType(server);
        return Task.CompletedTask;
    }

    public async Task AttachNatTypeDetectionServerAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("natTypeDetectionServerAttach");
        if (_natTypeDetectionServer is not null)
            return;
        _natTypeDetectionServer = new NatTypeDetectionServer();
        await AttachAsync(peer, _natTypeDetectionServer, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<IPEndPoint>> StartNatTypeDetectionServerAsync(string ip2, string ip3, string ip4, CancellationToken cancellationToken = default)
    {
        var server = _natTypeDetectionServer ?? throw new PeerCommandException("natTypeDetectionServerStartup", "no NatTypeDetectionServer is attached; send natTypeDetectionServerAttach first");
        server.Startup(ip2, ip3, ip4);
        return Task.FromResult<IReadOnlyList<IPEndPoint>>([.. server.SocketAddresses.Select(a => a.ToIPEndPoint())]);
    }

    public async Task AttachNatPunchthroughServerAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("natPunchthroughServerAttach");
        if (_natPunchthroughServer is not null)
            return;
        _natPunchthroughServer = new NatPunchthroughServer();
        await AttachAsync(peer, _natPunchthroughServer, cancellationToken).ConfigureAwait(false);
    }

    public async Task AttachNatPunchthroughClientAsync(CancellationToken cancellationToken = default)
    {
        var peer = Started("natPunchthroughClientAttach");
        if (_natPunchthroughClient is not null)
            return;
        _natPunchthroughClient = new NatPunchthroughClient { DebugMessage = _natDebugMessages.Enqueue };
        await AttachAsync(peer, _natPunchthroughClient, cancellationToken).ConfigureAwait(false);
    }

    public Task ConfigureNatPunchthroughClientAsync(PunchthroughSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configuration = RequireNatPunchthroughClient().Configuration;
        static TimeSpan Ms(int value) => TimeSpan.FromMilliseconds(value);
        if (settings.TimeBetweenPunchAttemptsInternal is { } a)
            configuration.TimeBetweenPunchAttemptsInternal = Ms(a);
        if (settings.TimeBetweenPunchAttemptsExternal is { } b)
            configuration.TimeBetweenPunchAttemptsExternal = Ms(b);
        if (settings.UdpSendsPerPortInternal is { } c)
            configuration.UdpSendsPerPortInternal = c;
        if (settings.UdpSendsPerPortExternal is { } d)
            configuration.UdpSendsPerPortExternal = d;
        if (settings.InternalIpWaitAfterAttempts is { } e)
            configuration.InternalIpWaitAfterAttempts = Ms(e);
        if (settings.MaxPredictivePortRange is { } f)
            configuration.MaxPredictivePortRange = f;
        if (settings.ExternalIpWaitBetweenPorts is { } g)
            configuration.ExternalIpWaitBetweenPorts = Ms(g);
        if (settings.ExternalIpWaitAfterFirstTtl is { } h)
            configuration.ExternalIpWaitAfterFirstTtl = Ms(h);
        if (settings.ExternalIpWaitAfterAllAttempts is { } i)
            configuration.ExternalIpWaitAfterAllAttempts = Ms(i);
        if (settings.MaximumNumberOfInternalIdsToCheck is { } j)
            configuration.MaximumNumberOfInternalIdsToCheck = j;
        if (settings.RetryOnFailure is { } k)
            configuration.RetryOnFailure = k;
        return Task.CompletedTask;
    }

    public Task<bool> OpenNatAsync(ulong target, IPEndPoint facilitator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facilitator);
        return Task.FromResult(RequireNatPunchthroughClient().OpenNat(new RakNetGuid(target), facilitator));
    }

    public Task FindRouterPortStrideAsync(IPEndPoint facilitator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facilitator);
        RequireNatPunchthroughClient().FindRouterPortStride(facilitator);
        return Task.CompletedTask;
    }

    private NatTypeDetectionClient RequireNatTypeDetectionClient() =>
        _natTypeDetectionClient ?? throw new PeerCommandException("natTypeDetectionClient", "no NatTypeDetectionClient is attached; send natTypeDetectionClientAttach first");

    private NatPunchthroughClient RequireNatPunchthroughClient() =>
        _natPunchthroughClient ?? throw new PeerCommandException("natPunchthroughClient", "no NatPunchthroughClient is attached; send natPunchthroughClientAttach first");
}
