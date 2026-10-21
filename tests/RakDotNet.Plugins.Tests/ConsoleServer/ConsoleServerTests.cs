using static RakDotNet.Plugins.Tests.RemoteConsole.RecordingTransport;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>ConsoleServer's own commands and its routing of lines to parsers, with RakNet's text (ConsoleServer.cpp).</summary>
public sealed class ConsoleServerTests
{
    private const string Instructions =
        "\r\nINSTRUCTIONS:\r\n" +
        "Enter commands on your keyboard, using spaces to delineate parameters.\r\n" +
        "You can use quotation marks to toggle space delineation.\r\n" +
        "You can connect multiple times from the same computer.\r\n" +
        "You can direct commands to a parser by prefixing the parser name or number.\r\n" +
        "COMMANDS:\r\n" +
        "help                                        Show this display.\r\n" +
        "help <ParserName>                           Show help on a particular parser.\r\n" +
        "help <CommandName>                          Show help on a particular command.\r\n" +
        "quit                                        Disconnects from the server.\r\n" +
        "[<ParserName>]   <Command> [<Parameters>]   Execute a command\r\n" +
        "[<ParserNumber>] <Command> [<Parameters>]   Execute a command\r\n";

    private readonly RecordingTransport _transport = new();
    private readonly ConsoleServer _console = new() { Prompt = "> " };
    private readonly EchoParser _echo = new EchoParser().WithCommand(2, "Pair").WithCommand(CommandParser.VariableNumberOfParameters, "Any");
    private readonly Plugins.LogCommandParser _logger = new();

    public ConsoleServerTests()
    {
        _console.AddCommandParser(_echo);
        _console.AddCommandParser(_logger);
        _console.SetTransportProvider(_transport, 23);
    }

    [Fact]
    public void A_new_connection_is_greeted_with_the_parsers_and_the_prompt()
    {
        _transport.NewConnections.Enqueue(Alice);

        _console.Update();

        Assert.Equal(
            ["Connected to remote command console.\r\nType 'help' for help.\r\n", "INSTALLED PARSERS:\r\n", "1. Echo\r\n", "2. Logger\r\n", "> "],
            _transport.SentTo(Alice));
        Assert.Equal([Alice], _echo.Connected);
    }

    [Fact]
    public void Help_shows_the_instructions_and_the_parsers_then_the_prompt()
    {
        _transport.Type(Alice, "HELP");

        _console.Update();

        Assert.Equal(Instructions + "INSTALLED PARSERS:\r\n1. Echo\r\n2. Logger\r\n> ", _transport.TextSentTo(Alice));
    }

    [Fact]
    public void Help_with_a_parser_name_shows_its_help_and_its_commands_in_name_order()
    {
        _transport.Type(Alice, "help echo");

        _console.Update();

        Assert.Equal(["Echo help\r\n", "COMMAND LIST:\r\n", "Any", ", ", "Pair", "\r\n", "\r\n", "> "], _transport.SentTo(Alice));
    }

    [Fact]
    public void Help_with_a_command_shows_its_parameter_count_and_help()
    {
        _transport.Type(Alice, "help pair");
        _transport.Type(Alice, "help SUBSCRIBE");
        _transport.Type(Alice, "help nothing");

        _console.Update();

        Assert.Equal(
            [
                "(2 parms): Pair help text\r\n", "> ",
                "(Variable parms): Subscribe [<ChannelName>] - Subscribes to a named channel, or all channels\r\n", "> ",
                "Unknown help topic: nothing.\r\n", "> ",
            ],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void A_command_named_by_one_parser_runs_there_with_its_parameters_and_the_line_as_typed()
    {
        _transport.Type(Alice, "Pair one \"two three\"");

        _console.Update();

        var ran = Assert.Single(_echo.Ran);
        Assert.Equal("Pair", ran.Command);
        Assert.Equal(["one", "two three"], ran.Parameters);
        Assert.Equal(Alice, ran.From);
        Assert.Equal("Pair one \"two three\"", ran.Original);
        Assert.Equal(["Echo ran Pair\r\n", "> "], _transport.SentTo(Alice));
    }

    [Fact]
    public void A_command_directed_by_parser_name_or_number_runs_only_there()
    {
        var other = new EchoParser("Other").WithCommand(0, "Shared");
        _echo.WithCommand(0, "Shared");
        _console.AddCommandParser(other);

        _transport.Type(Alice, "Shared");
        _transport.Type(Alice, "other shared");
        _transport.Type(Alice, "1 Shared");

        _console.Update();

        Assert.Equal(
            ["Echo ran Shared\r\n", "Other ran Shared\r\n", "> ", "Other ran Shared\r\n", "> ", "Echo ran Shared\r\n", "> "],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void A_parser_number_out_of_range_is_an_invalid_index()
    {
        _transport.Type(Alice, "0 Pair a b");
        _transport.Type(Alice, "3 Pair a b");

        _console.Update();

        // Nothing was parsed, so the unknown command line follows (ConsoleServer.cpp:280-283).
        string[] answer = ["Invalid index.\r\n", "Unknown command:  Type 'help' for help.\r\n", "> "];
        Assert.Equal([.. answer, .. answer], _transport.SentTo(Alice));
        Assert.Empty(_echo.Ran);
    }

    [Fact]
    public void A_wrong_parameter_count_shows_the_usage()
    {
        _transport.Type(Alice, "Pair a");
        _transport.Type(Alice, "Echo Pair a b c");

        _console.Update();

        Assert.Equal(
            ["Invalid parameter count.\r\n(2 parms): Pair help text\r\n", "> ", "Invalid parameter count.\r\n(2 parms): Pair help text\r\n", "> "],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void An_unknown_command_is_reported_even_when_directed_to_a_parser()
    {
        _transport.Type(Alice, "nothing");
        _transport.Type(Alice, "Echo nothing");
        _transport.Type(Alice, "help a b");

        _console.Update();

        Assert.Equal(Enumerable.Repeat<string[]>(["Unknown command:  Type 'help' for help.\r\n", "> "], 3).SelectMany(x => x), _transport.SentTo(Alice));
    }

    [Fact]
    public void A_variable_command_takes_any_number_of_parameters()
    {
        _transport.Type(Alice, "any");
        _transport.Type(Alice, "Any 1 2 3");

        _console.Update();

        Assert.Equal([[], ["1", "2", "3"]], _echo.Ran.Select(r => r.Parameters));
    }

    [Fact]
    public void A_line_with_no_parameters_gets_no_answer()
    {
        _transport.Type(Alice, "  \r\n");
        _transport.Type(Alice, "\"\"");

        _console.Update();

        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public void A_line_holds_at_most_twenty_parameters()
    {
        _transport.Type(Alice, "Any " + string.Join(' ', Enumerable.Range(1, 25)));

        _console.Update();

        Assert.Equal(19, Assert.Single(_echo.Ran).Parameters.Length);
    }

    [Fact]
    public void Quit_says_goodbye_and_closes_the_connection_then_sends_the_prompt()
    {
        _transport.Type(Alice, "Quit");

        _console.Update();

        Assert.Equal(["Goodbye!\r\n", "> "], _transport.SentTo(Alice));
        Assert.Equal([Alice], _transport.Closed);
    }

    [Fact]
    public void Without_parsers_an_unknown_command_gets_only_the_prompt()
    {
        var console = new ConsoleServer { Prompt = "% %%" };
        var transport = new RecordingTransport();
        console.SetTransportProvider(transport, 0);
        transport.Type(Alice, "anything");

        console.Update();

        // The prompt is a format: %% prints as %.
        Assert.Equal(["% %"], transport.SentTo(Alice));
    }

    [Fact]
    public void Without_a_prompt_nothing_follows_the_answer()
    {
        _console.Prompt = "";
        _transport.Type(Alice, "Pair a b");

        _console.Update();

        Assert.Null(_console.Prompt);
        Assert.Equal(["Echo ran Pair\r\n"], _transport.SentTo(Alice));
    }

    [Fact]
    public void A_lost_connection_is_passed_to_every_parser()
    {
        _transport.Type(Bob, "subscribe");
        _console.Update();
        _transport.LostConnections.Enqueue(Bob);

        _console.Update();

        Assert.Equal([Bob], _echo.Lost);
    }

    [Fact]
    public void The_first_line_is_taken_before_the_greeting_and_answered_after_it()
    {
        _transport.Type(Alice, "Pair a b");
        _transport.NewConnections.Enqueue(Alice);

        _console.Update();

        Assert.Equal("Echo ran Pair\r\n", _transport.SentTo(Alice)[^2]);
        Assert.StartsWith("Connected to remote command console.", _transport.SentTo(Alice)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_the_transport_stops_the_old_one_and_moves_the_transport_parsers()
    {
        var oldParser = new EchoParser("OldTransport");
        var newParser = new EchoParser("NewTransport");
        var parser = new EchoParser();
        var console = new ConsoleServer();
        var first = new RecordingTransport { CommandParser = oldParser };
        var second = new RecordingTransport { CommandParser = newParser };
        console.AddCommandParser(parser);

        console.SetTransportProvider(first, 23);
        console.SetTransportProvider(second, 24);

        Assert.Equal([23], first.Starts);
        Assert.Equal(1, first.Stops);
        Assert.Equal([24], second.Starts);
        Assert.Equal([parser, newParser], console.CommandParsers);
        Assert.Equal([first, second], parser.TransportChanges);
        Assert.Equal([second], newParser.TransportChanges);
    }

    [Fact]
    public void A_parser_is_installed_once_and_a_name_in_use_is_refused()
    {
        _console.AddCommandParser(_echo);
        _console.AddCommandParser(new EchoParser("LOGGER"));
        _console.AddCommandParser(null);

        Assert.Equal([_echo, _logger], _console.CommandParsers);
    }

    [Fact]
    public void Removing_a_parser_moves_the_last_into_its_place()
    {
        var third = new EchoParser("Third");
        _console.AddCommandParser(third);

        _console.RemoveCommandParser(_echo);

        Assert.Equal([third, _logger], _console.CommandParsers);
    }

    [Fact]
    public void Update_without_a_transport_is_a_programming_error()
    {
        Assert.Throws<InvalidOperationException>(() => new ConsoleServer().Update());
    }
}
