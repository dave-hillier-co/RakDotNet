namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A ConsoleServer on the peer, set up as Samples/CommandConsoleServer sets one up: the console* and
/// packetConsoleLoggerAttach commands of docs/interop-protocol.md. Both drivers implement it. The console is updated each
/// time the peer updates its plugins, and lives as long as the peer.
/// </summary>
public interface IConsoleDriver
{
    /// <summary>
    /// The parsers, the transport on <see cref="ConsoleStartOptions.Port"/>, the prompt, the peer for the RakNet parser
    /// and the Logger's channels; returns the port the telnet transport listens on, 0 for RakNetTransport2.
    /// </summary>
    Task<int> StartConsoleAsync(ConsoleStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>LogCommandParser::WriteLog of <paramref name="text"/>, as "%s", to <paramref name="channel"/>.</summary>
    Task WriteConsoleLogAsync(string channel, string text, CancellationToken cancellationToken = default);

    /// <summary>AttachPlugin of a PacketConsoleLogger writing to the console's LogCommandParser.</summary>
    Task AttachPacketConsoleLoggerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The fields of consoleStart. <see cref="Transport"/> is <c>raknet</c> (RakNetTransport2 on the peer) or
/// <c>telnet</c> (TelnetTransport); an empty prompt is none.
/// </summary>
public sealed record ConsoleStartOptions(
    string Transport,
    int Port = 0,
    string Prompt = "",
    IReadOnlyList<string>? Channels = null,
    bool RakNetParser = true,
    bool LogParser = true);
