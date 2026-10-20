using System.Net;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IServicesDriver
{
    public async Task<string?> SendEmailAsync(EmailRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new JsonObject
        {
            ["host"] = request.Host,
            ["port"] = request.Port,
            ["body"] = request.Body,
            ["attachments"] = new JsonArray([.. (request.Attachments ?? []).Select(a => (JsonNode)new JsonObject
            {
                ["name"] = a.Name,
                ["data"] = Convert.ToHexStringLower(a.Data),
            })]),
        };
        AddOptional(fields, "sender", request.Sender);
        AddOptional(fields, "recipient", request.Recipient);
        AddOptional(fields, "senderName", request.SenderName);
        AddOptional(fields, "recipientName", request.RecipientName);
        AddOptional(fields, "subject", request.Subject);
        AddOptional(fields, "password", request.Password);
        var reply = await SendCommandAsync("emailSend", fields, cancellationToken).ConfigureAwait(false);
        return (string?)reply["result"];
    }

    public Task StartDynDnsAsync(DynDnsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fields = new JsonObject
        {
            ["dnsHost"] = request.DnsHost,
            ["usernameAndPassword"] = request.UsernameAndPassword,
            ["redirects"] = Redirects(request.Redirects),
        };
        AddOptional(fields, "newIp", request.NewIp);
        return SendCommandAsync("dynDnsStart", fields, cancellationToken);
    }

    public async Task<DynDnsStatus> UpdateDynDnsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("dynDnsUpdate", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return new DynDnsStatus(
            (bool)reply["running"]!,
            (bool)reply["completed"]!,
            (int?)reply["resultCode"],
            (string?)reply["description"],
            (bool?)reply["successful"],
            (string)reply["myPublicIp"]!);
    }

    public async Task<IPEndPoint?> AuthenticateRackspaceAsync(RackspaceAuthentication request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reply = await SendCommandAsync("rackspaceAuthenticate", new JsonObject
        {
            ["url"] = request.Url,
            ["user"] = request.User,
            ["key"] = request.Key,
            ["maxConnections"] = request.MaxConnections,
            ["redirects"] = Redirects(request.Redirects),
        }, cancellationToken).ConfigureAwait(false);
        return ParseTcpAddress(reply["address"]);
    }

    public Task RackspaceOperationAsync(string operation, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return SendCommandAsync("rackspaceOperation", new JsonObject
        {
            ["op"] = operation,
            ["args"] = new JsonArray([.. arguments.Select(a => (JsonNode)a)]),
        }, cancellationToken);
    }

    public async Task<RackspacePoll> PollRackspaceAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("rackspacePoll", new JsonObject(), cancellationToken).ConfigureAwait(false);
        return new RackspacePoll(
            [.. reply["results"]!.AsArray().Select(r => new RackspaceResult(
                (string)r!["callback"]!, (int)r["eventType"]!, (string)r["eventName"]!, Convert.FromHexString((string)r["html"]!)))],
            [.. reply["failures"]!.AsArray().Select(f => new RackspaceFailure((int)f!["operationType"]!, (string)f["url"]!))]);
    }

    private static void AddOptional(JsonObject fields, string name, string? value)
    {
        if (value is not null)
            fields[name] = value;
    }

    private static JsonArray Redirects(IReadOnlyList<ServiceRedirect> redirects) =>
        new([.. redirects.Select(r => (JsonNode)new JsonObject
        {
            ["host"] = r.Host,
            ["toHost"] = r.To.Address.ToString(),
            ["toPort"] = r.To.Port,
        })]);
}
