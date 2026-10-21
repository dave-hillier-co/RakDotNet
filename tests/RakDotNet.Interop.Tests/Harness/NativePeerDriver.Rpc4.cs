using System.Globalization;
using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IRpc4Driver
{
    public Task AttachRpc4Async(CancellationToken cancellationToken = default) => SendCommandAsync("rpc4Attach", [], cancellationToken);

    public async Task<int?> RegisterRpc4FunctionAsync(string name, CancellationToken cancellationToken = default) =>
        (int?)(await SendCommandAsync("rpc4RegisterFunction", new JsonObject { ["name"] = name }, cancellationToken).ConfigureAwait(false))["handler"];

    public async Task<int> RegisterRpc4SlotAsync(string name, int priority, bool interrupt = false, CancellationToken cancellationToken = default) =>
        (int)(await SendCommandAsync("rpc4RegisterSlot", new JsonObject { ["name"] = name, ["priority"] = priority, ["interrupt"] = interrupt }, cancellationToken).ConfigureAwait(false))["handler"]!;

    public async Task<int?> RegisterRpc4BlockingAsync(string name, Rpc4Bits? returnData, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["name"] = name };
        if (returnData is not null)
        {
            fields["returnData"] = Convert.ToHexStringLower(returnData.Data);
            fields["returnBits"] = returnData.Bits;
        }
        return (int?)(await SendCommandAsync("rpc4RegisterBlocking", fields, cancellationToken).ConfigureAwait(false))["handler"];
    }

    public Task RegisterRpc4LocalCallbackAsync(string name, byte messageId, CancellationToken cancellationToken = default) =>
        SendCommandAsync("rpc4RegisterLocalCallback", new JsonObject { ["name"] = name, ["messageId"] = messageId }, cancellationToken);

    public async Task<bool> UnregisterRpc4Async(Rpc4Registration kind, string name, byte messageId = 0, CancellationToken cancellationToken = default)
    {
        var command = kind switch
        {
            Rpc4Registration.Function => "rpc4UnregisterFunction",
            Rpc4Registration.Blocking => "rpc4UnregisterBlocking",
            Rpc4Registration.Slot => "rpc4UnregisterSlot",
            _ => "rpc4UnregisterLocalCallback",
        };
        return (bool)(await SendCommandAsync(command, new JsonObject { ["name"] = name, ["messageId"] = messageId }, cancellationToken).ConfigureAwait(false))["result"]!;
    }

    public Task Rpc4CallAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default) =>
        SendCommandAsync("rpc4Call", Rpc4Fields(name, parameters, send), cancellationToken);

    public Task Rpc4SignalAsync(string name, Rpc4Bits? parameters, Rpc4Send send, bool invokeLocal, CancellationToken cancellationToken = default)
    {
        var fields = Rpc4Fields(name, parameters, send);
        fields["invokeLocal"] = invokeLocal;
        return SendCommandAsync("rpc4Signal", fields, cancellationToken);
    }

    public Task Rpc4CallLoopbackAsync(string name, Rpc4Bits? parameters, CancellationToken cancellationToken = default) =>
        SendCommandAsync("rpc4CallLoopback", Rpc4Fields(name, parameters, send: null), cancellationToken);

    public async Task<(bool Returned, Rpc4Bits ReturnData)> Rpc4CallBlockingAsync(string name, Rpc4Bits? parameters, Rpc4Send send, CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("rpc4CallBlocking", Rpc4Fields(name, parameters, send), cancellationToken).ConfigureAwait(false);
        return ((bool)reply["result"]!, new Rpc4Bits(Convert.FromHexString((string)reply["returnData"]!), (int)reply["returnBits"]!));
    }

    public async Task<IReadOnlyList<Rpc4Invocation>> GetRpc4InvocationsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await SendCommandAsync("rpc4Info", [], cancellationToken).ConfigureAwait(false);
        return [.. reply["invocations"]!.AsArray().Select(i => new Rpc4Invocation(
            (int)i!["handler"]!,
            (string)i["name"]!,
            ulong.Parse((string)i["guid"]!, CultureInfo.InvariantCulture),
            (string)i["address"]!,
            (bool)i["local"]!,
            new Rpc4Bits(Convert.FromHexString((string)i["parameters"]!), (int)i["bits"]!)))];
    }

    private static JsonObject Rpc4Fields(string name, Rpc4Bits? parameters, Rpc4Send? send)
    {
        var fields = new JsonObject { ["name"] = name };
        if (parameters is not null)
        {
            fields["parameters"] = Convert.ToHexStringLower(parameters.Data);
            fields["bits"] = parameters.Bits;
        }
        if (send is not null)
        {
            if (send.Target is { } target)
                fields["target"] = FormatGuid(target);
            fields["broadcast"] = send.Broadcast;
            fields["reliability"] = ToProtocolName(send.Reliability);
            fields["priority"] = ToProtocolName(send.Priority);
            fields["channel"] = send.Channel;
        }
        return fields;
    }
}
