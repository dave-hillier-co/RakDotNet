using RakDotNet.Plugins;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// The console* commands on a <see cref="ConsoleServer"/>, with interop-peer's results and errors
/// (tools/interop-peer/src/ConsoleCommands.cpp). As there, the console is updated from a plugin's update hook, on the
/// peer's dispatch step, and a telnet transport is stopped when the peer shuts down.
/// </summary>
public sealed partial class RakDotNetPeerDriver : IConsoleDriver
{
    private ConsoleServer? _console;
    private LogCommandParser? _logParser;
    private PacketConsoleLogger? _packetConsoleLogger;

    public async Task<int> StartConsoleAsync(ConsoleStartOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_console is not null)
            throw new PeerCommandException("consoleStart", "a console is already started");
        var peer = Started("consoleStart");
        if (options.Port is < 0 or > ushort.MaxValue)
            throw new PeerCommandException("consoleStart", "field 'port' is out of range");
        if (options.Transport is not ("raknet" or "telnet"))
            throw new PeerCommandException("consoleStart", "field 'transport' must be raknet or telnet");

        var console = new ConsoleServer();
        RakNetCommandParser? rakNetParser = null;
        if (options.RakNetParser)
        {
            rakNetParser = new RakNetCommandParser();
            console.AddCommandParser(rakNetParser);
        }
        if (options.LogParser)
        {
            _logParser = new LogCommandParser();
            console.AddCommandParser(_logParser);
        }

        IConsoleTransport transport;
        TelnetTransport? telnet = null;
        if (options.Transport == "raknet")
        {
            var rakNetTransport = new RakNetTransport2();
            await AttachAsync(peer, rakNetTransport, cancellationToken).ConfigureAwait(false);
            transport = rakNetTransport;
        }
        else
        {
            transport = telnet = new TelnetTransport();
        }
        console.SetTransportProvider(transport, (ushort)options.Port);
        console.Prompt = options.Prompt;
        rakNetParser?.Peer = peer;
        foreach (var channel in options.Channels ?? [])
            _logParser?.AddChannel(channel);

        _console = console;
        await AttachAsync(peer, new ConsoleUpdater(console, telnet), cancellationToken).ConfigureAwait(false);
        return telnet?.ListenEndPoint?.Port ?? 0;
    }

    public Task WriteConsoleLogAsync(string channel, string text, CancellationToken cancellationToken = default)
    {
        RequireLogParser("consoleLog").WriteLog(channel, text);
        return Task.CompletedTask;
    }

    public async Task AttachPacketConsoleLoggerAsync(CancellationToken cancellationToken = default)
    {
        var logParser = RequireLogParser("packetConsoleLoggerAttach");
        if (_packetConsoleLogger is not null)
            return;
        _packetConsoleLogger = new PacketConsoleLogger { LogCommandParser = logParser };
        await AttachAsync(Started("packetConsoleLoggerAttach"), _packetConsoleLogger, cancellationToken).ConfigureAwait(false);
    }

    private LogCommandParser RequireLogParser(string command) =>
        _logParser ?? throw new PeerCommandException(command, "no LogCommandParser; send consoleStart with logParser first");

    /// <summary>interop-peer's ConsoleUpdater: a sample's main loop calling ConsoleServer::Update with the peer's updates.</summary>
    private sealed class ConsoleUpdater(ConsoleServer console, TelnetTransport? telnet) : IRakPlugin
    {
        public void OnUpdate() => console.Update();

        // interop-peer's telnet transport ends with its process.
        public void OnShutdown() => telnet?.Stop();
    }
}
