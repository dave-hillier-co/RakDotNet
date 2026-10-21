using RakDotNet.Plugins;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>The cg2* and fcm2* commands on a <see cref="ConnectionGraph2"/> and <see cref="FullyConnectedMesh2"/>, with interop-peer's results and errors.</summary>
public sealed partial class RakDotNetPeerDriver : ITopologyDriver
{
    private ConnectionGraph2? _connectionGraph;
    private FullyConnectedMesh2? _mesh;

    public async Task AttachConnectionGraph2Async(bool autoProcess = true, CancellationToken cancellationToken = default)
    {
        var peer = Started("cg2Attach");
        if (_connectionGraph is not null)
            return;
        _connectionGraph = new ConnectionGraph2 { AutoProcessNewConnections = autoProcess };
        await AttachAsync(peer, _connectionGraph, cancellationToken).ConfigureAwait(false);
    }

    public Task SetConnectionGraph2AutoProcessAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireConnectionGraph().AutoProcessNewConnections = enabled;
        return Task.CompletedTask;
    }

    public Task AddConnectionGraph2ParticipantAsync(ulong target, CancellationToken cancellationToken = default)
    {
        var graph = RequireConnectionGraph();
        var guid = new RakNetGuid(target);
        graph.AddParticipant(Started("cg2AddParticipant").GetAddressFromGuid(guid), guid);
        return Task.CompletedTask;
    }

    public Task<ConnectionGraph2Info> GetConnectionGraph2InfoAsync(CancellationToken cancellationToken = default)
    {
        var graph = RequireConnectionGraph();
        var participants = graph.GetParticipantList().Select(p =>
        {
            graph.TryGetConnectionList(p, out var connections);
            return new ConnectionGraph2Participant(p.Value, [.. connections.Select(c => new ConnectionGraph2Connection(
                c.RakNetGuid.Value, c.Address.ToIPEndPoint(), graph.GetPingBetweenSystems(p, c.RakNetGuid)))]);
        });
        return Task.FromResult(new ConnectionGraph2Info([.. participants], graph.GetLowestAveragePingSystem().Value));
    }

    public async Task AttachFullyConnectedMesh2Async(MeshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var peer = Started("fcm2Attach");
        if (_mesh is not null)
            return;
        var joinStart = options.JoinStartUserData ?? [];
        var joinCapable = options.JoinCapableUserData ?? [];
        _mesh = new FullyConnectedMesh2
        {
            AutoParticipateConnections = options.AutoParticipate,
            ConnectOnNewRemoteConnection = options.ConnectOnNewRemote,
            ConnectionPassword = options.Password ?? [],
            // interop-peer's MeshWithUserData writes the same bytes for every system.
            VerifiedJoinStartUserData = joinStart.Length == 0 ? null : (bits, _) => bits.WriteBytes(joinStart),
            VerifiedJoinCapableUserData = joinCapable.Length == 0 ? null : bits => bits.WriteBytes(joinCapable),
        };
        await AttachAsync(peer, _mesh, cancellationToken).ConfigureAwait(false);
    }

    public Task AddMeshParticipantAsync(ulong target, CancellationToken cancellationToken = default)
    {
        RequireMesh().AddParticipant(new RakNetGuid(target));
        return Task.CompletedTask;
    }

    public Task ResetHostCalculationAsync(CancellationToken cancellationToken = default)
    {
        RequireMesh().ResetHostCalculation();
        return Task.CompletedTask;
    }

    public Task<MeshInfo> GetMeshInfoAsync(CancellationToken cancellationToken = default)
    {
        var mesh = RequireMesh();
        var address = mesh.ConnectedHostAddress;
        return Task.FromResult(new MeshInfo(
            mesh.HostSystem.Value,
            mesh.IsHostSystem,
            mesh.ConnectedHost.Value,
            mesh.IsConnectedHost,
            address.IsAssigned ? address.ToIPEndPoint() : null,
            [.. mesh.GetParticipantList().Select(g => g.Value)],
            [.. mesh.GetHostOrder().Select(g => g.Value)],
            mesh.TotalConnectionCount));
    }

    public Task StartVerifiedJoinAsync(ulong client, CancellationToken cancellationToken = default)
    {
        RequireMesh().StartVerifiedJoin(new RakNetGuid(client));
        return Task.CompletedTask;
    }

    public Task RespondOnVerifiedJoinCapableAsync(ReceivedPacket packet, bool accept, ReadOnlyMemory<byte> additionalData = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RequireMesh().RespondOnVerifiedJoinCapable(packet.Data.Span, packet.Address, new RakNetGuid(packet.PeerGuid), accept, additionalData.Span);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VerifiedJoinRequired>> GetVerifiedJoinRequiredAsync(ulong host, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<VerifiedJoinRequired>>([.. RequireMesh().GetVerifiedJoinRequiredProcessingList(new RakNetGuid(host))
            .Select(s => new VerifiedJoinRequired(s.RakNetGuid.Value, s.Address.ToIPEndPoint(), s.UserData.ToArray(), s.UserDataBitLength))]);

    public Task<VerifiedJoinAcceptedInfo> GetVerifiedJoinAcceptedAsync(ReceivedPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var accepted = RequireMesh().GetVerifiedJoinAcceptedAdditionalData(packet.Data.Span, new RakNetGuid(packet.PeerGuid));
        return Task.FromResult(new VerifiedJoinAcceptedInfo(
            accepted.ThisSystemAccepted, [.. accepted.SystemsAccepted.Select(g => g.Value)], accepted.AdditionalData.ToArray()));
    }

    public Task ConnectToRemoteNewIncomingConnectionsAsync(ReceivedPacket packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RequireMesh().ConnectToRemoteNewIncomingConnections(packet.Data.Span);
        return Task.CompletedTask;
    }

    // interop-peer's RequireConnectionGraph and RequireMesh.
    private ConnectionGraph2 RequireConnectionGraph() =>
        _connectionGraph ?? throw new PeerCommandException("cg2", "no ConnectionGraph2 is attached; send cg2Attach first");

    private FullyConnectedMesh2 RequireMesh() =>
        _mesh ?? throw new PeerCommandException("fcm2", "no FullyConnectedMesh2 is attached; send fcm2Attach first");
}
