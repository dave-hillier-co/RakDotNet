using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : ITopologyDriver
{
    public Task AttachConnectionGraph2Async(bool autoProcess = true, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cg2Attach", new JsonObject { ["autoProcess"] = autoProcess }, cancellationToken);

    public Task SetConnectionGraph2AutoProcessAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cg2SetAutoProcess", new JsonObject { ["enabled"] = enabled }, cancellationToken);

    public Task AddConnectionGraph2ParticipantAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("cg2AddParticipant", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public async Task<ConnectionGraph2Info> GetConnectionGraph2InfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("cg2Info", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return new ConnectionGraph2Info(
            [.. reply["participants"]!.AsArray().Select(p => new ConnectionGraph2Participant(
                ParseGuid(p!["guid"]),
                [.. p["connections"]!.AsArray().Select(c => new ConnectionGraph2Connection(
                    ParseGuid(c!["guid"]), IPEndPoint.Parse((string)c["address"]!), (int)c["ping"]!))]))],
            ParseGuid(reply["lowestAveragePingSystem"]));
    }

    public Task AttachFullyConnectedMesh2Async(MeshOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return SendCommandAsync("fcm2Attach", new JsonObject
        {
            ["autoParticipate"] = options.AutoParticipate,
            ["connectOnNewRemote"] = options.ConnectOnNewRemote,
            ["password"] = Convert.ToHexStringLower(options.Password ?? []),
            ["vjsUserData"] = Convert.ToHexStringLower(options.JoinStartUserData ?? []),
            ["vjcUserData"] = Convert.ToHexStringLower(options.JoinCapableUserData ?? []),
        }, cancellationToken);
    }

    public Task AddMeshParticipantAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("fcm2AddParticipant", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public Task ResetHostCalculationAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("fcm2ResetHostCalculation", new JsonObject(), cancellationToken);

    public async Task<MeshInfo> GetMeshInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("fcm2Info", new JsonObject(), cancellationToken).ConfigureAwait(false);
        var address = (string)reply["connectedHostAddress"]!;
        return new MeshInfo(
            ParseGuid(reply["hostSystem"]),
            (bool)reply["isHostSystem"]!,
            ParseGuid(reply["connectedHost"]),
            (bool)reply["isConnectedHost"]!,
            address.Length == 0 ? null : IPEndPoint.Parse(address),
            [.. reply["participants"]!.AsArray().Select(ParseGuid)],
            [.. reply["hostOrder"]!.AsArray().Select(ParseGuid)],
            (uint)reply["totalConnectionCount"]!);
    }

    public Task StartVerifiedJoinAsync(ulong client, CancellationToken cancellationToken = default) =>
        SendCommandAsync("fcm2StartVerifiedJoin", new JsonObject { ["target"] = FormatGuid(client) }, cancellationToken);

    public Task RespondOnVerifiedJoinCapableAsync(ReceivedPacket packet, bool accept, ReadOnlyMemory<byte> additionalData = default, CancellationToken cancellationToken = default)
    {
        var fields = PacketFields(packet);
        fields["accept"] = accept;
        fields["additionalData"] = Convert.ToHexStringLower(additionalData.Span);
        return SendCommandAsync("fcm2RespondOnVerifiedJoinCapable", fields, cancellationToken);
    }

    public async Task<IReadOnlyList<VerifiedJoinRequired>> GetVerifiedJoinRequiredAsync(ulong host, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("fcm2VerifiedJoinRequired", new JsonObject { ["host"] = FormatGuid(host) }, cancellationToken).ConfigureAwait(false);
        return [.. reply["systems"]!.AsArray().Select(s => new VerifiedJoinRequired(
            ParseGuid(s!["guid"]),
            IPEndPoint.Parse((string)s["address"]!),
            Convert.FromHexString((string)s["userData"]!),
            (int)s["userDataBits"]!))];
    }

    public async Task<VerifiedJoinAcceptedInfo> GetVerifiedJoinAcceptedAsync(ReceivedPacket packet, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("fcm2VerifiedJoinAccepted", PacketFields(packet), cancellationToken).ConfigureAwait(false);
        return new VerifiedJoinAcceptedInfo(
            (bool)reply["thisSystemAccepted"]!,
            [.. reply["systemsAccepted"]!.AsArray().Select(ParseGuid)],
            Convert.FromHexString((string)reply["additionalData"]!));
    }

    public Task ConnectToRemoteNewIncomingConnectionsAsync(ReceivedPacket packet, CancellationToken cancellationToken = default) =>
        SendCommandAsync("fcm2ConnectToRemoteNewIncomingConnections", PacketFields(packet), cancellationToken);

    // A received packet as the fcm2 commands that take one rebuild it.
    private static JsonObject PacketFields(ReceivedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return new JsonObject
        {
            ["data"] = Convert.ToHexStringLower(packet.Data.Span),
            ["guid"] = FormatGuid(packet.PeerGuid),
            ["address"] = packet.Address.ToString(),
        };
    }
}
