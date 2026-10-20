using System.Net;
using RakDotNet.Protocol.Wire;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IRelayDriver
{
    public Task StartUdpForwarderAsync(int? maxEntries = null, CancellationToken cancellationToken = default) =>
        SendCommandAsync("udpForwarderStartup", maxEntries is { } max ? new JsonObject { ["maxEntries"] = max } : [], cancellationToken);

    public async Task<(string Result, int Port)> StartForwardingAsync(IPEndPoint source, IPEndPoint destination, TimeSpan timeout, string? forceHost = null,
                                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var fields = new JsonObject
        {
            ["source"] = source.ToString(),
            ["destination"] = destination.ToString(),
            ["timeoutMs"] = (long)timeout.TotalMilliseconds,
        };
        if (forceHost is not null)
            fields["forceHost"] = forceHost;
        var reply = await SendCommandAsync("udpForwarderStart", fields, cancellationToken).ConfigureAwait(false);
        return ((string)reply["result"]!, (int)reply["port"]!);
    }

    public Task StopForwardingAsync(IPEndPoint source, IPEndPoint destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        return SendCommandAsync("udpForwarderStop", new JsonObject { ["source"] = source.ToString(), ["destination"] = destination.ToString() }, cancellationToken);
    }

    public async Task<(int MaxEntries, int UsedEntries)> GetUdpForwarderInfoAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("udpForwarderInfo", [], cancellationToken).ConfigureAwait(false);
        return ((int)reply["maxEntries"]!, (int)reply["usedEntries"]!);
    }

    public Task AttachUdpProxyCoordinatorAsync(string password, CancellationToken cancellationToken = default) =>
        SendCommandAsync("udpProxyCoordinatorAttach", new JsonObject { ["password"] = password }, cancellationToken);

    public Task AttachUdpProxyServerAsync(string publicIp = "", int? maxForwardEntries = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["publicIp"] = publicIp };
        if (maxForwardEntries is { } max)
            fields["maxForwardEntries"] = max;
        return SendCommandAsync("udpProxyServerAttach", fields, cancellationToken);
    }

    public async Task<bool> LoginToCoordinatorAsync(string password, IPEndPoint? coordinator = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["password"] = password };
        if (coordinator is not null)
            fields["coordinator"] = coordinator.ToString();
        return (bool)(await SendCommandAsync("udpProxyServerLogin", fields, cancellationToken).ConfigureAwait(false))["result"]!;
    }

    public Task AttachUdpProxyClientAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("udpProxyClientAttach", [], cancellationToken);

    public Task<bool> RequestForwardingAsync(IPEndPoint coordinator, ulong target, TimeSpan timeout, IPEndPoint? source = null,
                                             CancellationToken cancellationToken = default) =>
        RequestForwardingAsync(coordinator, timeout, source, fields => fields["target"] = FormatGuid(target), cancellationToken);

    public Task<bool> RequestForwardingToAddressAsync(IPEndPoint coordinator, IPEndPoint targetAddress, TimeSpan timeout, IPEndPoint? source = null,
                                                      CancellationToken cancellationToken = default) =>
        RequestForwardingAsync(coordinator, timeout, source, fields => fields["targetAddress"] = targetAddress.ToString(), cancellationToken);

    public async Task<IReadOnlyList<UdpProxyCallback>> GetUdpProxyCallbacksAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("udpProxyCallbacks", [], cancellationToken).ConfigureAwait(false);
        static IPEndPoint? Address(JsonNode? node) => IPEndPoint.TryParse((string?)node ?? "", out var address) ? address : null;
        return [.. reply["callbacks"]!.AsArray().Select(c => new UdpProxyCallback(
            (string)c!["name"]!,
            (string?)c["proxyIp"],
            (int?)c["proxyPort"] ?? 0,
            Address(c["coordinator"]),
            Address(c["source"]),
            Address(c["target"]),
            c["targetGuid"] is { } guid ? ParseGuid(guid) : 0,
            (string?)c["password"]))];
    }

    public Task AttachRouter2Async(int? maxForwardingRequests = null, CancellationToken cancellationToken = default) =>
        SendCommandAsync("router2Attach", maxForwardingRequests is { } max ? new JsonObject { ["maxForwardingRequests"] = max } : [], cancellationToken);

    public Task EstablishRoutingAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("router2EstablishRouting", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public Task AttachRelayAsync(bool acceptAddParticipantRequests, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayAttach", new JsonObject { ["acceptAddParticipantRequests"] = acceptAddParticipantRequests }, cancellationToken);

    public async Task<string> AddParticipantOnServerAsync(string key, ulong target, CancellationToken cancellationToken = default) =>
        (string)(await SendCommandAsync("relayAddParticipantOnServer", new JsonObject { ["key"] = key, ["target"] = FormatGuid(target) }, cancellationToken)
            .ConfigureAwait(false))["result"]!;

    public Task RemoveParticipantOnServerAsync(ulong target, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayRemoveParticipantOnServer", new JsonObject { ["target"] = FormatGuid(target) }, cancellationToken);

    public Task AddParticipantRequestAsync(string key, ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayAddParticipantRequest", new JsonObject { ["key"] = key, ["server"] = FormatGuid(server) }, cancellationToken);

    public Task RemoveParticipantRequestAsync(ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayRemoveParticipantRequest", new JsonObject { ["server"] = FormatGuid(server) }, cancellationToken);

    public Task SendToParticipantAsync(ulong server, string key, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                                       int channel = 0, CancellationToken cancellationToken = default)
    {
        var fields = RelayData(server, data, bits, priority, reliability, channel);
        fields["key"] = key;
        return SendCommandAsync("relaySendToParticipant", fields, cancellationToken);
    }

    public Task SendGroupMessageAsync(ulong server, byte[] data, int bits, Priority priority = Priority.High, Reliability reliability = Reliability.ReliableOrdered,
                                      int channel = 0, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relaySendGroupMessage", RelayData(server, data, bits, priority, reliability, channel), cancellationToken);

    public Task JoinGroupAsync(ulong server, string group, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayJoinGroup", new JsonObject { ["server"] = FormatGuid(server), ["group"] = group }, cancellationToken);

    public Task LeaveGroupAsync(ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayLeaveGroup", new JsonObject { ["server"] = FormatGuid(server) }, cancellationToken);

    public Task GetGroupListAsync(ulong server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("relayGetGroupList", new JsonObject { ["server"] = FormatGuid(server) }, cancellationToken);

    private async Task<bool> RequestForwardingAsync(IPEndPoint coordinator, TimeSpan timeout, IPEndPoint? source, Action<JsonObject> target,
                                                    CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        var fields = new JsonObject { ["coordinator"] = coordinator.ToString(), ["timeoutMs"] = (long)timeout.TotalMilliseconds };
        if (source is not null)
            fields["source"] = source.ToString();
        target(fields);
        return (bool)(await SendCommandAsync("udpProxyClientRequest", fields, cancellationToken).ConfigureAwait(false))["result"]!;
    }

    private static JsonObject RelayData(ulong server, byte[] data, int bits, Priority priority, Reliability reliability, int channel) => new()
    {
        ["server"] = FormatGuid(server),
        ["data"] = Convert.ToHexStringLower(data),
        ["bits"] = bits,
        ["priority"] = ToProtocolName(priority),
        ["reliability"] = ToProtocolName(reliability),
        ["channel"] = channel,
    };
}
