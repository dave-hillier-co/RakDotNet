using System.Text.Json.Nodes;

namespace RakDotNet.Interop.Tests.Harness;

public sealed partial class NativePeerDriver : IConsoleDriver
{
    public async Task<int> StartConsoleAsync(ConsoleStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var reply = await SendCommandAsync("consoleStart", new JsonObject
        {
            ["transport"] = options.Transport,
            ["port"] = options.Port,
            ["prompt"] = options.Prompt,
            ["channels"] = new JsonArray([.. (options.Channels ?? []).Select(c => (JsonNode)c)]),
            ["rakNetParser"] = options.RakNetParser,
            ["logParser"] = options.LogParser,
        }, cancellationToken).ConfigureAwait(false);
        return (int)reply["port"]!;
    }

    public Task WriteConsoleLogAsync(string channel, string text, CancellationToken cancellationToken = default) =>
        SendCommandAsync("consoleLog", new JsonObject { ["channel"] = channel, ["text"] = text }, cancellationToken);

    public Task AttachPacketConsoleLoggerAsync(CancellationToken cancellationToken = default) =>
        SendCommandAsync("packetConsoleLoggerAttach", new JsonObject(), cancellationToken);
}
