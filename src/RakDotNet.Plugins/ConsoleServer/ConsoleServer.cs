using System.Globalization;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's ConsoleServer: a remote command console. It reads lines from one <see cref="IConsoleTransport"/> and passes
/// them to its <see cref="CommandParser"/>s, answering with RakNet's text.
/// </summary>
/// <remarks>
/// <para>
/// Ported from ConsoleServer.cpp. A line is split at spaces, with double quotes keeping spaces, into at most 20
/// parameters. <c>help</c>, <c>help &lt;parser&gt;</c>, <c>help &lt;command&gt;</c> and <c>quit</c> are the console's
/// own; any other line is a command, directed to one parser when its first word is a parser's name or its number in
/// the list (from 1), and otherwise offered to every parser that has the command, each of which runs it. Names are
/// matched without regard to case. Every line is answered, then the prompt is sent.
/// </para>
/// <para>
/// Not thread-safe: call <see cref="Update"/> and the other members from one thread, as RakNet's samples call them from
/// their main loop.
/// </para>
/// </remarks>
public sealed class ConsoleServer
{
    // ConsoleServer.cpp:20-21 and the 20 parameters Update allows (ConsoleServer.cpp:105, 139).
    private const char CommandDelimiter = ' ';
    private const char CommandDelimiterToggle = '"';
    private const int MaxParameters = 20;

    private readonly List<CommandParser> _parsers = [];
    private string? _prompt;

    /// <summary>The transport given to <see cref="SetTransportProvider"/>, or null.</summary>
    public IConsoleTransport? Transport { get; private set; }

    /// <summary>The installed parsers, in the order the console numbers them.</summary>
    public IReadOnlyList<CommandParser> CommandParsers => _parsers;

    /// <summary>
    /// SetPrompt: sent after the greeting and after every line of input; null or empty for none, the default. RakNet
    /// passes it to the transport as a format, so <c>%%</c> sends one <c>%</c>.
    /// </summary>
    public string? Prompt
    {
        get => _prompt;
        set => _prompt = string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// SetTransportProvider (ConsoleServer.cpp:40-60): stops the current transport and removes its parser, starts
    /// <paramref name="transport"/> on <paramref name="port"/>, tells every parser, and installs the transport's own
    /// parser if it has one.
    /// </summary>
    public void SetTransportProvider(IConsoleTransport transport, ushort port)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (Transport is { } current)
        {
            RemoveCommandParser(current.CommandParser);
            current.Stop();
        }
        Transport = transport;
        transport.Start(port, serverMode: true);
        foreach (var parser in _parsers.ToArray())
            parser.OnTransportChange(transport);
        AddCommandParser(transport.CommandParser);
    }

    /// <summary>
    /// AddCommandParser (ConsoleServer.cpp:61-84): appends <paramref name="parser"/> unless it is null, already installed,
    /// or named as an installed parser is (which RakNet asserts on and otherwise ignores).
    /// </summary>
    public void AddCommandParser(CommandParser? parser)
    {
        if (parser is null)
            return;
        foreach (var installed in _parsers)
        {
            if (ReferenceEquals(installed, parser) || ConsoleText.EqualsIgnoreCase(installed.Name, parser.Name))
                return;
        }
        _parsers.Add(parser);
        if (Transport is { } transport)
            parser.OnTransportChange(transport);
    }

    /// <summary>RemoveCommandParser (ConsoleServer.cpp:85-101): the last parser takes the removed one's place.</summary>
    public void RemoveCommandParser(CommandParser? parser)
    {
        if (parser is null)
            return;
        var index = _parsers.IndexOf(parser);
        if (index < 0)
            return;
        _parsers[index] = _parsers[^1];
        _parsers.RemoveAt(_parsers.Count - 1);
    }

    /// <summary>
    /// Update (ConsoleServer.cpp:102-292): takes one line of input, then greets a new connection and tells the parsers of
    /// a lost one, then handles that line and every other line waiting.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transport has been set.</exception>
    public void Update()
    {
        var transport = Transport ?? throw new InvalidOperationException("Call SetTransportProvider before Update.");
        var packet = transport.Receive();

        var address = transport.HasNewIncomingConnection();
        if (address != SystemAddress.Unassigned)
        {
            foreach (var parser in _parsers.ToArray())
                parser.OnNewIncomingConnection(address, transport);
            transport.Send(address, "Connected to remote command console.\r\nType 'help' for help.\r\n");
            ListParsers(transport, address);
            ShowPrompt(transport, address);
        }

        address = transport.HasLostConnection();
        if (address != SystemAddress.Unassigned)
        {
            foreach (var parser in _parsers.ToArray())
                parser.OnConnectionLost(address, transport);
        }

        for (; packet is not null; packet = transport.Receive())
        {
            // The copy RakNet passes as originalString holds REMOTE_MAX_TEXT_INPUT bytes (ConsoleServer.cpp:136-138).
            var line = ConsoleText.Truncate(ConsoleText.FromBytes(packet.Data.Span), ConsoleText.MaxTextInput);
            var parameters = CommandParser.ParseConsoleString(line, CommandDelimiter, CommandDelimiterToggle, MaxParameters);
            if (parameters.Count == 0)
                continue;
            HandleLine(transport, packet.Address, line, parameters);
            ShowPrompt(transport, packet.Address);
        }
    }

    private void HandleLine(IConsoleTransport transport, SystemAddress from, string line, IReadOnlyList<string> parameters)
    {
        if (ConsoleText.EqualsIgnoreCase(parameters[0], "help") && parameters.Count <= 2)
        {
            if (parameters.Count == 1)
                SendInstructions(transport, from);
            else
                SendHelpTopic(transport, from, parameters[1]);
            return;
        }
        if (ConsoleText.EqualsIgnoreCase(parameters[0], "quit") && parameters.Count == 1)
        {
            transport.Send(from, "Goodbye!\r\n");
            transport.CloseConnection(from);
            return;
        }
        RunCommand(transport, from, line, parameters);
    }

    private void SendInstructions(IConsoleTransport transport, SystemAddress to)
    {
        transport.Send(to, "\r\nINSTRUCTIONS:\r\n");
        transport.Send(to, "Enter commands on your keyboard, using spaces to delineate parameters.\r\n");
        transport.Send(to, "You can use quotation marks to toggle space delineation.\r\n");
        transport.Send(to, "You can connect multiple times from the same computer.\r\n");
        transport.Send(to, "You can direct commands to a parser by prefixing the parser name or number.\r\n");
        transport.Send(to, "COMMANDS:\r\n");
        transport.Send(to, "help                                        Show this display.\r\n");
        transport.Send(to, "help <ParserName>                           Show help on a particular parser.\r\n");
        transport.Send(to, "help <CommandName>                          Show help on a particular command.\r\n");
        transport.Send(to, "quit                                        Disconnects from the server.\r\n");
        transport.Send(to, "[<ParserName>]   <Command> [<Parameters>]   Execute a command\r\n");
        transport.Send(to, "[<ParserNumber>] <Command> [<Parameters>]   Execute a command\r\n");
        ListParsers(transport, to);
    }

    // ConsoleServer.cpp:166-204: a parser's help, else the first parser's command of that name, else unknown.
    private void SendHelpTopic(IConsoleTransport transport, SystemAddress to, string topic)
    {
        foreach (var parser in _parsers)
        {
            if (ConsoleText.EqualsIgnoreCase(topic, parser.Name))
            {
                parser.SendHelp(transport, to);
                transport.Send(to, "COMMAND LIST:\r\n");
                parser.SendCommandList(transport, to);
                transport.Send(to, "\r\n");
                return;
            }
        }
        foreach (var parser in _parsers)
        {
            if (parser.TryGetRegisteredCommand(topic, out var command))
            {
                transport.Send(to, Usage(command));
                return;
            }
        }
        transport.Send(to, $"Unknown help topic: {topic}.\r\n");
    }

    // ConsoleServer.cpp:212-284.
    private void RunCommand(IConsoleTransport transport, SystemAddress from, string line, IReadOnlyList<string> parameters)
    {
        var commandParsed = false;
        var tryAllParsers = true;
        var failed = false;

        if (parameters.Count >= 2)
        {
            var parserIndex = -1;
            if (char.IsAsciiDigit(parameters[0][0]))
            {
                // Numbered from 1; atoi of 0, or of a number too large, is an invalid index.
                parserIndex = ConsoleText.Atoi(parameters[0]) - 1;
                if ((uint)parserIndex >= (uint)_parsers.Count)
                {
                    transport.Send(from, "Invalid index.\r\n");
                    failed = true;
                }
            }
            else
            {
                parserIndex = _parsers.FindIndex(p => ConsoleText.EqualsIgnoreCase(parameters[0], p.Name));
            }

            if (!failed && parserIndex >= 0)
            {
                tryAllParsers = false;
                var parser = _parsers[parserIndex];
                if (parser.TryGetRegisteredCommand(parameters[1], out var command))
                {
                    commandParsed = true;
                    if (command.ParameterCount == CommandParser.VariableNumberOfParameters || command.ParameterCount == parameters.Count - 2)
                        parser.OnCommand(command.Command, Slice(parameters, 2), transport, from, line);
                    else
                        transport.Send(from, "Invalid parameter count.\r\n" + Usage(command));
                }
            }
        }

        if (!failed && tryAllParsers)
        {
            foreach (var parser in _parsers.ToArray())
            {
                if (!parser.TryGetRegisteredCommand(parameters[0], out var command))
                    continue;
                commandParsed = true;
                if (command.ParameterCount == CommandParser.VariableNumberOfParameters || command.ParameterCount == parameters.Count - 1)
                    parser.OnCommand(command.Command, Slice(parameters, 1), transport, from, line);
                else
                    transport.Send(from, "Invalid parameter count.\r\n" + Usage(command));
            }
        }

        if (!commandParsed && _parsers.Count > 0)
            transport.Send(from, "Unknown command:  Type 'help' for help.\r\n");
    }

    // ListParsers (ConsoleServer.cpp:294-302).
    private void ListParsers(IConsoleTransport transport, SystemAddress to)
    {
        transport.Send(to, "INSTALLED PARSERS:\r\n");
        for (var i = 0; i < _parsers.Count; i++)
            transport.Send(to, string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {_parsers[i].Name}\r\n"));
    }

    // ShowPrompt (ConsoleServer.cpp:303-306): the prompt is the format, and a null one sends nothing.
    private void ShowPrompt(IConsoleTransport transport, SystemAddress to)
    {
        if (_prompt is { } prompt)
            transport.Send(to, ConsoleText.FormatWithoutArguments(prompt));
    }

    private static string Usage(RegisteredCommand command) =>
        command.ParameterCount == CommandParser.VariableNumberOfParameters
            ? $"(Variable parms): {command.Command} {command.CommandHelp}\r\n"
            : string.Create(CultureInfo.InvariantCulture, $"({command.ParameterCount} parms): {command.Command} {command.CommandHelp}\r\n");

    private static string[] Slice(IReadOnlyList<string> parameters, int start)
    {
        var slice = new string[parameters.Count - start];
        for (var i = 0; i < slice.Length; i++)
            slice[i] = parameters[start + i];
        return slice;
    }
}
