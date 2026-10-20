namespace RakDotNet.Plugins;

/// <summary>
/// PacketConsoleLogger: a <see cref="PacketLogger"/> whose lines go to the <c>PacketConsoleLogger</c> channel of a
/// <see cref="LogCommandParser"/>, so remote consoles that subscribe to it see the peer's traffic.
/// </summary>
/// <remarks>
/// Ported from PacketConsoleLogger.cpp. RakNet passes each line to LogCommandParser::WriteLog as the format, so
/// <c>%%</c> in a line, which only a prefix or suffix can put there, is written as one <c>%</c>. Lines come from the
/// peer's I/O cycle as well as its dispatch step; <see cref="LogCommandParser.WriteLog"/> is thread-safe.
/// </remarks>
public class PacketConsoleLogger : PacketLogger
{
    /// <summary>The channel the lines are written to.</summary>
    public const string ChannelName = "PacketConsoleLogger";

    private volatile LogCommandParser? _logCommandParser;

    public PacketConsoleLogger()
        : base(TextWriter.Null)
    {
    }

    /// <summary>SetLogCommandParser: the parser to write to, which gets the channel when set; null writes nowhere.</summary>
    public LogCommandParser? LogCommandParser
    {
        get => _logCommandParser;
        set
        {
            _logCommandParser = value;
            value?.AddChannel(ChannelName);
        }
    }

    /// <summary>WriteLog: writes the line to the channel.</summary>
    public override void WriteLog(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        _logCommandParser?.WriteLog(ChannelName, ConsoleText.FormatWithoutArguments(line));
    }
}
