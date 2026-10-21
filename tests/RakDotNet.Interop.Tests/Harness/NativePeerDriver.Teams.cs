using System.Globalization;
using System.Text.Json.Nodes;
using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : ITeamDriver
{
    public Task AttachTeamManagerAsync(TeamManagerTopology topology = TeamManagerTopology.PeerToPeer, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmAttach", new JsonObject
        {
            ["topology"] = topology == TeamManagerTopology.ClientServer ? "TM_CLIENT_SERVER" : "TM_PEER_TO_PEER",
        }, cancellationToken);

    public Task SetTeamAutoManageConnectionsAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmSetAutoManageConnections", new JsonObject { ["enabled"] = enabled }, cancellationToken);

    public Task AddTeamWorldAsync(byte worldId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmAddWorld", new JsonObject { ["worldId"] = worldId }, cancellationToken);

    public Task SetTeamHostAsync(byte worldId, ulong host, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmSetHost", new JsonObject { ["worldId"] = worldId, ["host"] = FormatGuid(host) }, cancellationToken);

    public Task AddTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmAddParticipant", new JsonObject { ["worldId"] = worldId, ["target"] = FormatGuid(target) }, cancellationToken);

    public Task RemoveTeamParticipantAsync(byte worldId, ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmRemoveParticipant", new JsonObject { ["worldId"] = worldId, ["target"] = FormatGuid(target) }, cancellationToken);

    public Task ReferenceTeamAsync(byte worldId, ulong team, bool applyBalancing, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmReferenceTeam", new JsonObject
        {
            ["worldId"] = worldId,
            ["team"] = FormatGuid(team),
            ["applyBalancing"] = applyBalancing,
        }, cancellationToken);

    public Task ReferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmReferenceTeamMember", new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member) }, cancellationToken);

    public Task DereferenceTeamAsync(byte worldId, ulong team, byte noTeamId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmDereferenceTeam", new JsonObject
        {
            ["worldId"] = worldId,
            ["team"] = FormatGuid(team),
            ["noTeamId"] = noTeamId,
        }, cancellationToken);

    public Task DereferenceTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tmDereferenceTeamMember", new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member) }, cancellationToken);

    public Task<bool> RequestTeamAsync(byte worldId, ulong member, JoinTeamType selection, ulong team = 0, byte noTeamId = 0, CancellationToken cancellationToken = default) =>
        ResultAsync("tmRequestTeam", new JsonObject
        {
            ["worldId"] = worldId,
            ["member"] = FormatGuid(member),
            ["selection"] = JoinTeamTypeName(selection),
            ["team"] = FormatGuid(team),
            ["noTeamId"] = noTeamId,
        }, cancellationToken);

    public Task<bool> RequestTeamSwitchAsync(byte worldId, ulong member, ulong teamToJoin, ulong? teamToLeave, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member), ["teamToJoin"] = FormatGuid(teamToJoin) };
        if (teamToLeave is { } leave)
            fields["teamToLeave"] = FormatGuid(leave);
        return ResultAsync("tmRequestTeamSwitch", fields, cancellationToken);
    }

    public Task<bool> CancelTeamRequestAsync(byte worldId, ulong member, ulong? team, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member) };
        if (team is { } cancelled)
            fields["team"] = FormatGuid(cancelled);
        return ResultAsync("tmCancelTeamRequest", fields, cancellationToken);
    }

    public Task<bool> LeaveTeamAsync(byte worldId, ulong member, ulong team, byte noTeamId, CancellationToken cancellationToken = default) =>
        ResultAsync("tmLeaveTeam", new JsonObject
        {
            ["worldId"] = worldId,
            ["member"] = FormatGuid(member),
            ["team"] = FormatGuid(team),
            ["noTeamId"] = noTeamId,
        }, cancellationToken);

    public Task<bool> LeaveAllTeamsAsync(byte worldId, ulong member, byte noTeamId, CancellationToken cancellationToken = default) =>
        ResultAsync("tmLeaveAllTeams", new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member), ["noTeamId"] = noTeamId }, cancellationToken);

    public Task<bool> SetTeamMemberLimitAsync(byte worldId, ulong team, ushort limit, byte noTeamId, CancellationToken cancellationToken = default) =>
        ResultAsync("tmSetMemberLimit", new JsonObject
        {
            ["worldId"] = worldId,
            ["team"] = FormatGuid(team),
            ["limit"] = limit,
            ["noTeamId"] = noTeamId,
        }, cancellationToken);

    public Task<bool> SetTeamJoinPermissionsAsync(byte worldId, ulong team, JoinPermissions permissions, CancellationToken cancellationToken = default) =>
        ResultAsync("tmSetJoinPermissions", new JsonObject
        {
            ["worldId"] = worldId,
            ["team"] = FormatGuid(team),
            ["permissions"] = (byte)permissions,
        }, cancellationToken);

    public Task<bool> SetBalanceTeamsAsync(byte worldId, bool enabled, byte noTeamId, CancellationToken cancellationToken = default) =>
        ResultAsync("tmSetBalanceTeams", new JsonObject { ["worldId"] = worldId, ["enabled"] = enabled, ["noTeamId"] = noTeamId }, cancellationToken);

    public async Task<byte[]> SerializeTeamAsync(byte worldId, ulong team, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("tmSerializeTeam", new JsonObject { ["worldId"] = worldId, ["team"] = FormatGuid(team) }, cancellationToken).ConfigureAwait(false);
        return Convert.FromHexString((string)reply["data"]!);
    }

    public async Task<byte[]> SerializeTeamMemberAsync(byte worldId, ulong member, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("tmSerializeTeamMember", new JsonObject { ["worldId"] = worldId, ["member"] = FormatGuid(member) }, cancellationToken).ConfigureAwait(false);
        return Convert.FromHexString((string)reply["data"]!);
    }

    public Task<bool> DeserializeTeamAsync(byte[] data, CancellationToken cancellationToken = default) =>
        ResultAsync("tmDeserializeTeam", new JsonObject { ["data"] = Convert.ToHexStringLower(data) }, cancellationToken);

    public Task<bool> DeserializeTeamMemberAsync(byte[] data, CancellationToken cancellationToken = default) =>
        ResultAsync("tmDeserializeTeamMember", new JsonObject { ["data"] = Convert.ToHexStringLower(data) }, cancellationToken);

    public async Task<TeamWorldInfo> GetTeamWorldInfoAsync(byte worldId, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("tmInfo", new JsonObject { ["worldId"] = worldId }, cancellationToken).ConfigureAwait(false);
        return new TeamWorldInfo(
            ParseGuid(reply["host"]),
            (bool)reply["balanceTeams"]!,
            [.. reply["participants"]!.AsArray().Select(ParseGuid)],
            [.. reply["teams"]!.AsArray().Select(t => new TeamInfo(
                ParseGuid(t!["team"]),
                [.. t["members"]!.AsArray().Select(ParseGuid)],
                (ushort)t["memberLimit"]!,
                (ushort)t["memberLimitSetting"]!,
                (JoinPermissions)(byte)t["joinPermissions"]!,
                (bool)t["balancingApplies"]!))],
            [.. reply["members"]!.AsArray().Select(m => new TeamMemberInfo(
                ParseGuid(m!["member"]),
                [.. m["teams"]!.AsArray().Select(ParseGuid)],
                [.. m["lastTeams"]!.AsArray().Select(ParseGuid)],
                (byte)m["noTeamId"]!,
                [.. m["requestedTeams"]!.AsArray().Select(ParseGuid)],
                ParseJoinTeamType((string)m["requestedTeam"]!)))]);
    }

    public Task AttachTeamBalancerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbAttach", new JsonObject(), cancellationToken);

    public Task SetTeamSizeLimitAsync(byte team, ushort limit, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbSetTeamSizeLimit", new JsonObject { ["team"] = team, ["limit"] = limit }, cancellationToken);

    public Task SetDefaultAssignmentAlgorithmAsync(DefaultAssignmentAlgorithm algorithm, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbSetDefaultAssignmentAlgorithm", new JsonObject
        {
            ["algorithm"] = algorithm == DefaultAssignmentAlgorithm.FillInOrder ? "FILL_IN_ORDER" : "SMALLEST_TEAM",
        }, cancellationToken);

    public Task SetForceEvenTeamsAsync(bool force, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbSetForceEvenTeams", new JsonObject { ["force"] = force }, cancellationToken);

    public Task SetLockTeamsAsync(bool lockTeams, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbSetLockTeams", new JsonObject { ["lock"] = lockTeams }, cancellationToken);

    public Task RequestSpecificTeamAsync(ulong member, byte team, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbRequestSpecificTeam", new JsonObject { ["member"] = FormatGuid(member), ["team"] = team }, cancellationToken);

    public Task CancelRequestSpecificTeamAsync(ulong member, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbCancelRequestSpecificTeam", new JsonObject { ["member"] = FormatGuid(member) }, cancellationToken);

    public Task RequestAnyTeamAsync(ulong member, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbRequestAnyTeam", new JsonObject { ["member"] = FormatGuid(member) }, cancellationToken);

    public async Task<byte> GetMyTeamAsync(ulong member, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("tbGetMyTeam", new JsonObject { ["member"] = FormatGuid(member) }, cancellationToken).ConfigureAwait(false);
        return (byte)reply["team"]!;
    }

    public Task DeleteBalancedMemberAsync(ulong member, CancellationToken cancellationToken = default) =>
        SendCommandAsync("tbDeleteMember", new JsonObject { ["member"] = FormatGuid(member) }, cancellationToken);

    private async Task<bool> ResultAsync(string command, JsonObject fields, CancellationToken cancellationToken)
    {
        var reply = await SendCommandAsync(command, fields, cancellationToken).ConfigureAwait(false);
        return (bool)reply["result"]!;
    }

    internal static string JoinTeamTypeName(JoinTeamType type) => type switch
    {
        JoinTeamType.AnyAvailableTeam => "JOIN_ANY_AVAILABLE_TEAM",
        JoinTeamType.SpecificTeam => "JOIN_SPECIFIC_TEAM",
        _ => "JOIN_NO_TEAM",
    };

    private static JoinTeamType ParseJoinTeamType(string name) => name switch
    {
        "JOIN_ANY_AVAILABLE_TEAM" => JoinTeamType.AnyAvailableTeam,
        "JOIN_SPECIFIC_TEAM" => JoinTeamType.SpecificTeam,
        "JOIN_NO_TEAM" => JoinTeamType.NoTeam,
        _ => throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Unknown JoinTeamType '{name}'.")),
    };
}
