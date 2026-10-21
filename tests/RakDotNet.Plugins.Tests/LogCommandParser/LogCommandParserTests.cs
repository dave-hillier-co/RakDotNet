using RakDotNet.Plugins.Tests.RemoteConsole;
using static RakDotNet.Plugins.Tests.RemoteConsole.RecordingTransport;

namespace RakDotNet.Plugins.Tests.LogCommandParser;

/// <summary>LogCommandParser through a console: channels, subscriptions and log lines (LogCommandParser.cpp).</summary>
public sealed class LogCommandParserTests
{
    private readonly RecordingTransport _transport = new();
    private readonly ConsoleServer _console = new();
    private readonly Plugins.LogCommandParser _logger = new();

    public LogCommandParserTests()
    {
        _console.AddCommandParser(_logger);
        _console.SetTransportProvider(_transport, 0);
        _logger.AddChannel("TestChannel");
        _logger.AddChannel("Other");
    }

    [Fact]
    public void Subscribing_to_a_channel_sends_its_lines_and_not_the_others()
    {
        Type(Alice, "Logger subscribe testchannel");

        _logger.WriteLog("TestChannel", "Test of logger");
        _logger.WriteLog("Other", "not for alice");

        Assert.Equal(["You are now subscribed to channel TestChannel.\r\n", "Test of logger\r\n"], _transport.SentTo(Alice));
    }

    [Fact]
    public void Subscribing_to_all_channels_and_unsubscribing_from_one()
    {
        Type(Alice, "Subscribe");
        Type(Alice, "Unsubscribe Other");

        _logger.WriteLog("TestChannel", "one");
        _logger.WriteLog("Other", "two");

        Assert.Equal(
            ["Subscribed to all channels.\r\n", "You are now unsubscribed from channel Other.\r\n", "one\r\n"],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void Unsubscribing_from_all_channels_forgets_the_system()
    {
        Type(Alice, "Subscribe");
        Type(Alice, "Unsubscribe");
        // Unsubscribing from one channel when not subscribed is "cannot find" (LogCommandParser.cpp:204-231).
        Type(Alice, "Unsubscribe TestChannel");

        _logger.WriteLog("TestChannel", "gone");

        Assert.Equal(
            ["Subscribed to all channels.\r\n", "Unsubscribed from all channels.\r\n", "Cannot find channel TestChannel.\r\n", "CHANNELS:\r\n", "1. TestChannel\r\n", "2. Other\r\n"],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void An_unknown_channel_or_too_many_parameters_is_refused()
    {
        Type(Alice, "Subscribe nothing");
        Type(Alice, "Subscribe a b");
        Type(Alice, "Unsubscribe a b");

        Assert.Equal(
            [
                "Cannot find channel nothing.\r\n", "CHANNELS:\r\n", "1. TestChannel\r\n", "2. Other\r\n",
                "Subscribe takes either 0 or 1 parameters.\r\n",
                "Unsubscribe takes either 0 or 1 parameters.\r\n",
            ],
            _transport.SentTo(Alice));
    }

    [Fact]
    public void Help_lists_the_channels_or_none()
    {
        var empty = new Plugins.LogCommandParser();

        _logger.SendHelp(_transport, Alice);
        empty.SendHelp(_transport, Bob);

        Assert.Equal(
            [
                "The logger will accept user log data via the Log(...) function.\r\n",
                "Each log is associated with a named channel.\r\n",
                "You can subscribe to or unsubscribe from named channels.\r\n",
                "CHANNELS:\r\n", "1. TestChannel\r\n", "2. Other\r\n",
            ],
            _transport.SentTo(Alice));
        Assert.Equal("None.\r\n", _transport.SentTo(Bob)[^1]);
    }

    [Fact]
    public void A_line_ending_in_a_newline_ends_in_crlf_and_an_empty_line_is_not_sent()
    {
        Type(Alice, "Subscribe");

        _logger.WriteLog("TestChannel", "with newline\n");
        _logger.WriteLog("TestChannel", "with crlf\r\n");
        _logger.WriteLog("TestChannel", "");
        _logger.WriteLog("TestChannel", "50%% done");

        Assert.Equal(["with newline\r\n", "with crlf\r\r\n", "50% done\r\n"], _transport.SentTo(Alice).Skip(1));
    }

    [Fact]
    public void A_long_line_loses_its_last_three_characters_to_the_line_end()
    {
        Type(Alice, "Subscribe");

        _logger.WriteLog("TestChannel", new string('x', 3000));
        _logger.WriteLog("TestChannel", new string('y', 2043));

        var sent = _transport.SentTo(Alice);
        Assert.Equal(new string('x', 2044) + "\r\n", sent[1]);
        Assert.Equal(new string('y', 2043) + "\r\n", sent[2]);
    }

    [Fact]
    public void The_first_line_to_a_new_channel_adds_it_and_reaches_no_one_who_subscribed_to_all()
    {
        Type(Alice, "Subscribe");

        _logger.WriteLog("Fresh", "first");
        _logger.WriteLog("Fresh", "second");

        Assert.Equal(["TestChannel", "Other", "Fresh"], _logger.Channels);
        Assert.Equal(["Subscribed to all channels.\r\n", "second\r\n"], _transport.SentTo(Alice));
    }

    [Fact]
    public void Subscribing_to_all_covers_only_the_first_sixteen_channels()
    {
        for (var i = 2; i < 17; i++)
            _logger.AddChannel($"C{i}");
        Type(Alice, "Subscribe");
        Type(Bob, "Subscribe C16");

        _logger.WriteLog("C15", "sixteenth");
        _logger.WriteLog("C16", "seventeenth");

        Assert.Equal("sixteenth\r\n", _transport.SentTo(Alice)[^1]);
        Assert.Equal(["You are now subscribed to channel C16.\r\n", "seventeenth\r\n"], _transport.SentTo(Bob));
    }

    [Fact]
    public void A_lost_connection_is_unsubscribed()
    {
        Type(Alice, "Subscribe");
        _transport.LostConnections.Enqueue(Alice);
        _console.Update();
        _transport.Clear();

        _logger.WriteLog("TestChannel", "after");

        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public void Nothing_is_sent_without_a_channel_name_or_text()
    {
        Type(Alice, "Subscribe");
        _transport.Clear();

        _logger.WriteLog(null, "text");
        _logger.WriteLog("TestChannel", null);

        Assert.Empty(_transport.Sent);
    }

    private void Type(RakDotNet.Protocol.Wire.SystemAddress from, string line)
    {
        _transport.Type(from, line);
        _console.Update();
    }
}
