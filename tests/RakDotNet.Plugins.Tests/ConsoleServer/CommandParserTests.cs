using System.Net;
using RakDotNet.Protocol.Wire;
using static RakDotNet.Plugins.Tests.RemoteConsole.RecordingTransport;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>CommandParserInterface's parsing, command table and result lines (CommandParserInterface.cpp).</summary>
public sealed class CommandParserTests
{
    [Theory]
    [InlineData("a b  c", new[] { "a", "b", "c" })]
    [InlineData("  lead and trail  ", new[] { "lead", "and", "trail" })]
    [InlineData("say \"hello there\" now", new[] { "say", "hello there", "now" })]
    [InlineData("x\"y z\"w", new[] { "x", "y z", "w" })]
    [InlineData("line\r\nnext", new[] { "line", "next" })]
    [InlineData("open \"quote never closed", new[] { "open", "quote never closed" })]
    [InlineData("cut\0after", new[] { "cut" })]
    [InlineData("", new string[0])]
    public void ParseConsoleString_splits_at_the_delimiter_with_the_toggle_keeping_it(string text, string[] expected)
    {
        Assert.Equal(expected, CommandParser.ParseConsoleString(text, ' ', '"', 20));
    }

    [Fact]
    public void ParseConsoleString_keeps_at_most_the_number_asked_for()
    {
        Assert.Equal(["1", "2", "3"], CommandParser.ParseConsoleString("1 2 3 4 5", ' ', '"', 3));
    }

    [Fact]
    public void Commands_are_kept_in_case_insensitive_name_order_and_found_without_regard_to_case()
    {
        var parser = new EchoParser().WithCommand(1, "beta").WithCommand(2, "Alpha").WithCommand(3, "gamma");

        Assert.Equal(["Alpha", "beta", "gamma"], parser.RegisteredCommands.Select(c => c.Command));
        Assert.True(parser.TryGetRegisteredCommand("ALPHA", out var alpha));
        Assert.Equal(new RegisteredCommand("Alpha", "help text", 2), alpha);
        Assert.False(parser.TryGetRegisteredCommand("delta", out _));
    }

    [Fact]
    public void A_second_registration_of_a_name_is_ignored()
    {
        var parser = new EchoParser().WithCommand(1, "Name", "first").WithCommand(2, "NAME", "second");

        Assert.Equal([new RegisteredCommand("Name", "first", 1)], parser.RegisteredCommands);
    }

    [Fact]
    public void SendCommandList_sends_each_name_and_separator_separately()
    {
        var transport = new RecordingTransport();

        new EchoParser().WithCommand(0, "b").WithCommand(0, "a").SendCommandList(transport, Alice);
        new EchoParser().SendCommandList(transport, Bob);

        Assert.Equal(["a", ", ", "b", "\r\n"], transport.SentTo(Alice));
        Assert.Equal(["No registered commands\r\n"], transport.SentTo(Bob));
    }

    [Fact]
    public void ReturnResult_writes_RakNet_lines()
    {
        var transport = new RecordingTransport();
        var parser = new EchoParser();
        var result = new SystemAddress(IPAddress.Parse("192.168.1.2"), 61000);

        parser.ReturnResult(true, "IsActive", transport, Alice);
        parser.ReturnResult(false, "IsBanned", transport, Alice);
        parser.ReturnResult(-1, "GetIndexFromSystemAddress", transport, Alice);
        parser.ReturnResult("1.2.3.4", "GetLocalIP", transport, Alice);
        parser.ReturnResult((string?)null, "GetLocalIP", transport, Alice);
        parser.ReturnResult("Ping1", transport, Alice);
        parser.ReturnResult(result, "GetInternalID", transport, Alice);
        parser.ReturnResult(SystemAddress.Unassigned, "GetExternalID", transport, Alice);

        Assert.Equal(
            [
                "IsActive returned true.\r\n",
                "IsBanned returned false.\r\n",
                "GetIndexFromSystemAddress returned -1.\r\n",
                "GetLocalIP returned 1.2.3.4.\r\n",
                "GetLocalIP returned (null).\r\n",
                "Successfully called Ping1.\r\n",
                // The address of the system asking comes first (CommandParserInterface.cpp:160-167).
                "GetInternalID returned 10.0.0.1 192.168.1.2:61000\r\n",
                "GetExternalID returned 10.0.0.1 UNASSIGNED_SYSTEM_ADDRESS:0\r\n",
            ],
            transport.SentTo(Alice));
    }
}
