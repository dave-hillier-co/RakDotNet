using System.Net;
using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// A ConsoleServer with RakNetCommandParser and LogCommandParser, as Samples/CommandConsoleServer runs it, reached by
/// Samples/CommandConsoleClient over RakNetTransport2 (a plain peer sending ID_TRANSPORT_STRING) and by a telnet client
/// over TelnetTransport on loopback; and Samples/PacketConsoleLogger's telnet console watching a peer's traffic. The
/// samples are interactive, so each scenario types a fixed script and checks every reply, byte for byte, against RakNet's
/// text: each RakNetTransport2 send is a packet of its own, and telnet is checked as one byte stream.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class ConsoleServerScenarios
{
    private const string Prompt = "> ";

    private const string Greeting = "Connected to remote command console.\r\nType 'help' for help.\r\n";

    private static readonly string[] SampleParsers = ["INSTALLED PARSERS:\r\n", "1. RakNet\r\n", "2. Logger\r\n"];

    private static readonly string[] Instructions =
    [
        "\r\nINSTRUCTIONS:\r\n",
        "Enter commands on your keyboard, using spaces to delineate parameters.\r\n",
        "You can use quotation marks to toggle space delineation.\r\n",
        "You can connect multiple times from the same computer.\r\n",
        "You can direct commands to a parser by prefixing the parser name or number.\r\n",
        "COMMANDS:\r\n",
        "help                                        Show this display.\r\n",
        "help <ParserName>                           Show help on a particular parser.\r\n",
        "help <CommandName>                          Show help on a particular command.\r\n",
        "quit                                        Disconnects from the server.\r\n",
        "[<ParserName>]   <Command> [<Parameters>]   Execute a command\r\n",
        "[<ParserNumber>] <Command> [<Parameters>]   Execute a command\r\n",
    ];

    private static readonly string[] LoggerHelp =
    [
        "The logger will accept user log data via the Log(...) function.\r\n",
        "Each log is associated with a named channel.\r\n",
        "You can subscribe to or unsubscribe from named channels.\r\n",
        "CHANNELS:\r\n",
        "1. TestChannel\r\n",
        "COMMAND LIST:\r\n",
        "Subscribe", ", ", "Unsubscribe", "\r\n",
        "\r\n",
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_RakNet_console_client_is_greeted_and_gets_help_from_the_console_and_its_parsers(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var console = await RakNetConsole.StartAsync(session);

        await console.ExpectAsync([Greeting, .. SampleParsers, Prompt]);

        await console.TypeAsync("help");
        await console.ExpectAsync([.. Instructions, .. SampleParsers, Prompt]);
        await console.TypeAsync("help logger");
        await console.ExpectAsync([.. LoggerHelp, Prompt]);
        await console.TypeAsync("help Subscribe");
        await console.ExpectAsync(["(Variable parms): Subscribe [<ChannelName>] - Subscribes to a named channel, or all channels\r\n", Prompt]);
        await console.TypeAsync("help isbanned");
        await console.ExpectAsync(["(1 parms): IsBanned ( const char *IP );\r\n", Prompt]);
        await console.TypeAsync("help nothing");
        await console.ExpectAsync(["Unknown help topic: nothing.\r\n", Prompt]);
        await console.TypeAsync("  ");
        await console.TypeAsync("nothing at all");
        await console.ExpectAsync(["Unknown command:  Type 'help' for help.\r\n", Prompt]);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_RakNet_console_client_calls_the_server_peer_through_the_RakNet_parser(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var console = await RakNetConsole.StartAsync(session);
        await console.ExpectAsync([Greeting, .. SampleParsers, Prompt]);
        var clientAtServer = session.AddressAtServer(session.ClientPeer);

        string[][] script =
        [
            ["RakNet SetMaximumIncomingConnections 5", "Successfully called SetMaximumIncomingConnections.\r\n"],
            ["GetMaximumIncomingConnections", "GetMaximumIncomingConnections returned 5.\r\n"],
            ["1 IsActive", "IsActive returned true.\r\n"],
            ["GetConnectionList", "GetConnectionList() returned:\r\n", $"0 {clientAtServer.Address}|{clientAtServer.Port}\r\n"],
            ["AddToBanList 10.1.2.3 0", "Successfully called AddToBanList.\r\n"],
            ["IsBanned 10.1.2.3", "IsBanned returned true.\r\n"],
            ["RakNet IsBanned 10.1.2.4", "IsBanned returned false.\r\n"],
            ["IsBanned", "Invalid parameter count.\r\n(1 parms): IsBanned ( const char *IP );\r\n"],
            ["GetMTUSize", "GetMTUSize returned 576.\r\n"],
            ["SetIncomingPassword \"pass word\" 4", "Successfully called SetIncomingPassword.\r\n"],
            ["InitializeSecurity", "InitializeSecurity returned false.\r\n"],
            ["Startup 8 0 127.0.0.1 unused", "Startup returned 1.\r\n"],
            ["IsConnected a b"],
            ["GetConnectionState x", "Unknown command:  Type 'help' for help.\r\n"],
            ["9 IsActive", "Invalid index.\r\n", "Unknown command:  Type 'help' for help.\r\n"],
        ];
        foreach (var step in script)
        {
            await console.TypeAsync(step[0]);
            await console.ExpectAsync([.. step[1..], Prompt]);
        }
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_RakNet_console_client_follows_a_log_channel_and_quits(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var console = await RakNetConsole.StartAsync(session);
        var log = (IConsoleDriver)session.Server;
        await console.ExpectAsync([Greeting, .. SampleParsers, Prompt]);

        await console.TypeAsync("Logger Subscribe testchannel");
        await console.ExpectAsync(["You are now subscribed to channel TestChannel.\r\n", Prompt]);
        await log.WriteConsoleLogAsync("TestChannel", "Test of logger", Token);
        await console.ExpectAsync(["Test of logger\r\n"]);

        await console.TypeAsync("Subscribe");
        await console.ExpectAsync(["Subscribed to all channels.\r\n", Prompt]);
        // The first line to a new channel adds it but tests bit 31, which subscribing to all leaves clear
        // (LogCommandParser.cpp:136-141, 172); the second reaches the subscriber.
        await log.WriteConsoleLogAsync("Fresh", "first line", Token);
        await log.WriteConsoleLogAsync("Fresh", "second line\n", Token);
        await console.ExpectAsync(["second line\r\n"]);

        await console.TypeAsync("Unsubscribe TestChannel");
        await console.ExpectAsync(["You are now unsubscribed from channel TestChannel.\r\n", Prompt]);
        await log.WriteConsoleLogAsync("TestChannel", "not sent", Token);
        await log.WriteConsoleLogAsync("Fresh", "still sent", Token);
        await console.ExpectAsync(["still sent\r\n"]);

        await console.TypeAsync("quit");
        await console.ExpectAsync(["Goodbye!\r\n"]);
        await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_telnet_client_is_echoed_and_answered_line_by_line(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await InteropPeer.StartAsync(server, "console server", new StartupOptions(), Token);
        var port = await ((IConsoleDriver)serverPeer.Driver).StartConsoleAsync(new ConsoleStartOptions("telnet", Prompt: Prompt, Channels: ["TestChannel"]), Token);
        await using var telnet = await TelnetConsole.ConnectAsync(client, port);

        await telnet.ExpectAsync("ÿû\u0001" + Greeting + string.Concat(SampleParsers) + Prompt);

        await telnet.TypeAsync("help logger\r\n");
        await telnet.ExpectAsync("help logger\r\n" + string.Concat(LoggerHelp) + Prompt);

        // A backspace is echoed, then answered with a space and a backspace, and removes the character before it.
        await telnet.TypeAsync("Logger subscrx\bibe\r\n");
        await telnet.ExpectAsync("Logger subscrx\bibe\r\n \bSubscribed to all channels.\r\n" + Prompt);

        await ((IConsoleDriver)serverPeer.Driver).WriteConsoleLogAsync("TestChannel", "Test of logger", Token);
        await telnet.ExpectAsync("Test of logger\r\n");

        // The up arrow sends a backspace for every character of the line buffer, which still holds the tail of the last
        // line under "ab", then the last line, which return runs again.
        await telnet.TypeAsync("ab");
        await telnet.ExpectAsync("ab");
        await telnet.TypeAsync("\u001b[A");
        await telnet.ExpectAsync(new string('\b', "Logger subscribe".Length) + "Logger subscribe");
        await telnet.TypeAsync("\r\n");
        await telnet.ExpectAsync("\r\nSubscribed to all channels.\r\n" + Prompt);

        await telnet.TypeAsync("RakNet GetMTUSize\r\n");
        await telnet.ExpectAsync("RakNet GetMTUSize\r\nGetMTUSize returned 576.\r\n" + Prompt);

        // Closing drops what is still to be sent (TCPInterface.cpp:576-614), so only the lost connection is certain.
        await telnet.TypeAsync("quit\r\n");
        await telnet.Peer.WaitForKindAsync("lostConnection", Timeout, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_telnet_console_subscribed_to_the_packet_logger_sees_the_peers_traffic(PeerKind client, PeerKind server)
    {
        // Samples/PacketConsoleLogger: a Logger-only console over telnet, with no prompt, and the logger on the peer.
        await using var serverPeer = await InteropPeer.StartAsync(server, "logged peer", new StartupOptions(), Token);
        var console = (IConsoleDriver)serverPeer.Driver;
        var port = await console.StartConsoleAsync(new ConsoleStartOptions("telnet", RakNetParser: false), Token);
        await console.AttachPacketConsoleLoggerAsync(Token);
        await using var telnet = await TelnetConsole.ConnectAsync(client, port);
        await telnet.ExpectAsync("ÿû\u0001" + Greeting + "INSTALLED PARSERS:\r\n1. Logger\r\n");
        await telnet.TypeAsync("Logger subscribe\r\n");
        await telnet.ExpectAsync("Logger subscribe\r\nSubscribed to all channels.\r\n");

        await using var rakNetClient = await InteropPeer.StartAsync(client, "logged peer's client", new StartupOptions(), Token);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await rakNetClient.Driver.ConnectAsync(new Harness.ConnectOptions(serverPeer.Started.BoundAddress), Token));
        var accepted = await rakNetClient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", Timeout, Token);
        await rakNetClient.Driver.SendAsync(new SendOptions(accepted.PeerGuid, new byte[] { 134, 1, 2, 3 }), Token);

        // PacketLogger's line for the message the peer received: no clock, Rcv, Nrm, then the ID and 32 bits.
        var lines = await telnet.WaitForLineAsync(l => l.StartsWith(",Rcv,Nrm,", StringComparison.Ordinal) && l.Contains(",134,32,", StringComparison.Ordinal), "the logged user message");
        Assert.All(lines, l => Assert.EndsWith("\r\n", l, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(",ID_CONNECTION_REQUEST,", StringComparison.Ordinal));
    }

    /// <summary>Samples/CommandConsoleClient: a plain peer that sends each line as ID_TRANSPORT_STRING and its NUL.</summary>
    private sealed class RakNetConsole
    {
        private readonly InteropSession _session;
        private readonly ulong _serverGuid;

        private RakNetConsole(InteropSession session, ulong serverGuid)
        {
            _session = session;
            _serverGuid = serverGuid;
        }

        /// <summary>Samples/CommandConsoleServer with RakNetTransport2, then the client's connection.</summary>
        public static async Task<RakNetConsole> StartAsync(InteropSession session)
        {
            Assert.Equal(0, await ((IConsoleDriver)session.Server).StartConsoleAsync(new ConsoleStartOptions("raknet", Prompt: Prompt, Channels: ["TestChannel"]), Token));
            var (serverGuid, _) = await session.ConnectAsync(Token);
            return new RakNetConsole(session, serverGuid);
        }

        // CommandConsoleClient/main.cpp:128-133.
        public async Task TypeAsync(string line)
        {
            byte[] data = [(byte)MessageId.TransportString, .. Encoding.ASCII.GetBytes(line), 0];
            await _session.Client.SendAsync(new SendOptions(_serverGuid, data, Priority: Priority.Medium), Token);
        }

        /// <summary>The next sends of the console, one packet each: the ID, the text and a zero byte.</summary>
        public async Task ExpectAsync(string[] texts)
        {
            var packets = await _session.ClientInbox.WaitForPacketsAsync(
                p => p.MessageId == (byte)MessageId.TransportString, texts.Length, $"{texts.Length} console sends", Timeout, Token);
            Assert.All(packets, p => Assert.Equal(_serverGuid, p.PeerGuid));
            Assert.All(packets, p => Assert.Equal(0, p.Data.Span[^1]));
            Assert.Equal(texts, packets.Select(p => Encoding.ASCII.GetString(p.Data.Span[1..^1])));
        }
    }

    /// <summary>A telnet client: a TCPInterface connected to the console, whose replies are read as one stream.</summary>
    private sealed class TelnetConsole : IAsyncDisposable
    {
        private readonly IPEndPoint _server;
        private readonly StringBuilder _unread = new();

        private TelnetConsole(TcpPeer peer, IPEndPoint server)
        {
            Peer = peer;
            _server = server;
        }

        public TcpPeer Peer { get; }

        public static async Task<TelnetConsole> ConnectAsync(PeerKind kind, int port)
        {
            var peer = await TcpPeer.LaunchAsync(kind, "telnet client", Token);
            try
            {
                await peer.StartClientAsync(cancellationToken: Token);
                var server = new IPEndPoint(IPAddress.Loopback, port);
                Assert.Equal(server, await peer.Tcp.TcpConnectAsync(server, block: true, Token));
                return new TelnetConsole(peer, server);
            }
            catch
            {
                await peer.DisposeAsync();
                throw;
            }
        }

        /// <summary>
        /// Sends <paramref name="keys"/> as one packet; TelnetTransport handles a packet at a time, so each line waits for
        /// the reply to the one before.
        /// </summary>
        public Task TypeAsync(string keys) => Peer.Tcp.TcpSendAsync(_server, Encoding.Latin1.GetBytes(keys), cancellationToken: Token);

        /// <summary>The next bytes of the stream are exactly <paramref name="text"/>.</summary>
        public async Task ExpectAsync(string text)
        {
            await ReadUntilAsync(() => _unread.Length >= text.Length, $"\"{Escape(text)}\"");
            Assert.Equal(Escape(text), Escape(_unread.ToString(0, text.Length)));
            _unread.Remove(0, text.Length);
        }

        /// <summary>Reads whole lines until one matches; returns every line read, the match last.</summary>
        public async Task<List<string>> WaitForLineAsync(Func<string, bool> match, string expectation)
        {
            await ReadUntilAsync(() => Lines(_unread.ToString()).Any(match), expectation);
            var lines = new List<string>();
            foreach (var line in Lines(_unread.ToString()))
            {
                lines.Add(line);
                if (match(line))
                    break;
            }
            _unread.Remove(0, lines.Sum(l => l.Length));
            return lines;
        }

        public async ValueTask DisposeAsync() => await Peer.DisposeAsync();

        private async Task ReadUntilAsync(Func<bool> done, string expectation)
        {
            if (done())
                return;
            // ReceiveBytesAsync passes everything it has collected so far on each check.
            var before = _unread.ToString();
            await Peer.ReceiveBytesAsync(_server, bytes =>
            {
                _unread.Clear().Append(before).Append(Encoding.Latin1.GetString(bytes));
                return done();
            }, expectation, Timeout, Token);
        }

        private static IEnumerable<string> Lines(string text)
        {
            for (int start = 0, end; (end = text.IndexOf("\r\n", start, StringComparison.Ordinal)) >= 0; start = end + 2)
                yield return text[start..(end + 2)];
        }

        private static string Escape(string text) =>
            text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\b", "\\b", StringComparison.Ordinal);
    }
}
