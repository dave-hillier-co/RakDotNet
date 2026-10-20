using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : INatDriver
{
    public Task SetNatGatewayAsync(IPEndPoint? gateway, CancellationToken cancellationToken = default) =>
        SendCommandAsync("natGateway", gateway is null
            ? new JsonObject { ["port"] = 0 }
            : new JsonObject { ["host"] = gateway.Address.ToString(), ["port"] = gateway.Port }, cancellationToken);

    public Task AttachNatTypeDetectionClientAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("natTypeDetectionClientAttach", [], cancellationToken);

    public Task DetectNatTypeAsync(IPEndPoint server, CancellationToken cancellationToken = default) =>
        SendCommandAsync("natTypeDetectionClientDetect", Address(server), cancellationToken);

    public Task AttachNatTypeDetectionServerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("natTypeDetectionServerAttach", [], cancellationToken);

    public async Task<IReadOnlyList<IPEndPoint>> StartNatTypeDetectionServerAsync(string ip2, string ip3, string ip4, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("natTypeDetectionServerStartup", new JsonObject { ["ip2"] = ip2, ["ip3"] = ip3, ["ip4"] = ip4 }, cancellationToken).ConfigureAwait(false);
        return [.. reply["addresses"]!.AsArray().Select(a => IPEndPoint.Parse((string)a!))];
    }

    public Task AttachNatPunchthroughServerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("natPunchthroughServerAttach", [], cancellationToken);

    public Task AttachNatPunchthroughClientAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("natPunchthroughClientAttach", [], cancellationToken);

    public Task ConfigureNatPunchthroughClientAsync(PunchthroughSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var fields = new JsonObject();
        void Set(string name, int? value)
        {
            if (value is { } v)
                fields[name] = v;
        }
        Set("timeBetweenPunchAttemptsInternal", settings.TimeBetweenPunchAttemptsInternal);
        Set("timeBetweenPunchAttemptsExternal", settings.TimeBetweenPunchAttemptsExternal);
        Set("udpSendsPerPortInternal", settings.UdpSendsPerPortInternal);
        Set("udpSendsPerPortExternal", settings.UdpSendsPerPortExternal);
        Set("internalIpWaitAfterAttempts", settings.InternalIpWaitAfterAttempts);
        Set("maxPredictivePortRange", settings.MaxPredictivePortRange);
        Set("externalIpWaitBetweenPorts", settings.ExternalIpWaitBetweenPorts);
        Set("externalIpWaitAfterFirstTtl", settings.ExternalIpWaitAfterFirstTtl);
        Set("externalIpWaitAfterAllAttempts", settings.ExternalIpWaitAfterAllAttempts);
        Set("maximumNumberOfInternalIdsToCheck", settings.MaximumNumberOfInternalIdsToCheck);
        if (settings.RetryOnFailure is { } retry)
            fields["retryOnFailure"] = retry;
        return SendCommandAsync("natPunchthroughClientConfigure", fields, cancellationToken);
    }

    public async Task<bool> OpenNatAsync(ulong target, IPEndPoint facilitator, CancellationToken cancellationToken = default)
    {
        var fields = Address(facilitator);
        fields["target"] = FormatGuid(target);
        return (bool)(await SendCommandAsync("natPunchthroughOpen", fields, cancellationToken).ConfigureAwait(false))["result"]!;
    }

    public Task FindRouterPortStrideAsync(IPEndPoint facilitator, CancellationToken cancellationToken = default) =>
        SendCommandAsync("natPunchthroughFindPortStride", Address(facilitator), cancellationToken);

    private static JsonObject Address(IPEndPoint endPoint) => new() { ["host"] = endPoint.Address.ToString(), ["port"] = endPoint.Port };
}
