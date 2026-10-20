using System.Globalization;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>
/// RakNet's LogCommandParser, named <c>Logger</c> on the console: remote consoles subscribe to named channels and
/// receive what the application writes to them with <see cref="WriteLog"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ported from LogCommandParser.cpp. There are at most 32 channels, each a bit in a subscriber's mask. RakNet's quirks
/// are kept: subscribing to all channels sets only the first 16 bits (0xFFFF), unsubscribing from one channel also clears
/// bits 16 to 31, and the first <see cref="WriteLog"/> to a channel that does not exist yet adds it but tests the bit
/// for index -1, which the processor reads as bit 31 (LogCommandParser.cpp:136-141, 172), so it reaches only
/// subscribers to the channel with index 31.
/// </para>
/// <para>
/// Thread-safe: <see cref="WriteLog"/> may run on any thread, as <see cref="PacketConsoleLogger"/> calls it from a
/// peer's I/O cycle, while the console's thread runs the commands.
/// </para>
/// </remarks>
public class LogCommandParser : CommandParser
{
    private const int MaxChannels = 32;
    private const uint AllChannels = 0xFFFF;

    private readonly Lock _gate = new();
    private readonly string?[] _channelNames = new string?[MaxChannels];
    private readonly List<(SystemAddress Address, uint Channels)> _remoteUsers = [];
    private IConsoleTransport? _transport;

    public LogCommandParser()
    {
        RegisterCommand(VariableNumberOfParameters, "Subscribe", "[<ChannelName>] - Subscribes to a named channel, or all channels");
        RegisterCommand(VariableNumberOfParameters, "Unsubscribe", "[<ChannelName>] - Unsubscribes from a named channel, or all channels");
    }

    public override string Name => "Logger";

    /// <summary>The channel names in index order; a channel's number on the console is its index plus 1.</summary>
    public IReadOnlyList<string> Channels
    {
        get
        {
            lock (_gate)
                return [.. _channelNames.TakeWhile(n => n is not null).Select(n => n!)];
        }
    }

    /// <summary>
    /// AddChannel (LogCommandParser.cpp:110-130): takes the first free index. RakNet asserts that the name is new and that
    /// a channel is free, and otherwise adds a second channel of the same name, or nothing when all 32 are taken.
    /// </summary>
    public void AddChannel(string channelName)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        lock (_gate)
            AddChannelLocked(channelName);
    }

    /// <summary>
    /// WriteLog (LogCommandParser.cpp:131-177): sends <paramref name="text"/> to every subscriber of
    /// <paramref name="channelName"/>, adding the channel if it is new. The text is cut to 2047 characters and must not
    /// be empty; a final <c>\n</c> becomes <c>\r\n</c> and any other ending gets <c>\r\n</c>, except that text of 2044
    /// characters or more loses its last three to make room. It is then sent as RakNet passes it to the transport, as a
    /// format with no arguments.
    /// </summary>
    public void WriteLog(string? channelName, string? text)
    {
        if (channelName is null || text is null)
            return;
        lock (_gate)
        {
            var channelIndex = GetChannelIndexFromName(channelName);
            if (channelIndex == -1)
                AddChannelLocked(channelName);

            var line = ConsoleText.Truncate(ConsoleText.CString(text), ConsoleText.MaxTextInput);
            var length = line.Length;
            if (length == 0)
                return;
            if (line[^1] == '\n')
                line = line[..^1];
            line = length < ConsoleText.MaxTextInput - 4 ? line + "\r\n" : line[..(length - 3)] + "\r\n";

            // 1 << -1 for a channel just added: the shift count is taken modulo 32, as the processor does in RakNet.
            var bit = 1u << channelIndex;
            if (_transport is not { } transport)
                return;
            var formatted = ConsoleText.FormatWithoutArguments(line);
            foreach (var (address, channels) in _remoteUsers)
            {
                if ((channels & bit) != 0)
                    transport.Send(address, formatted);
            }
        }
    }

    public override bool OnCommand(string command, IReadOnlyList<string> parameters, IConsoleTransport transport, SystemAddress address,
                                   string originalString)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(transport);
        lock (_gate)
        {
            if (command == "Subscribe")
            {
                if (parameters.Count == 0)
                {
                    Subscribe(address, null);
                    transport.Send(address, "Subscribed to all channels.\r\n");
                }
                else if (parameters.Count == 1)
                {
                    var channelIndex = Subscribe(address, parameters[0]);
                    if (channelIndex != -1)
                    {
                        transport.Send(address, $"You are now subscribed to channel {_channelNames[channelIndex]}.\r\n");
                    }
                    else
                    {
                        transport.Send(address, $"Cannot find channel {parameters[0]}.\r\n");
                        PrintChannels(address, transport);
                    }
                }
                else
                {
                    transport.Send(address, "Subscribe takes either 0 or 1 parameters.\r\n");
                }
            }
            else if (command == "Unsubscribe")
            {
                if (parameters.Count == 0)
                {
                    Unsubscribe(address, null);
                    transport.Send(address, "Unsubscribed from all channels.\r\n");
                }
                else if (parameters.Count == 1)
                {
                    var channelIndex = Unsubscribe(address, parameters[0]);
                    if (channelIndex != -1)
                    {
                        transport.Send(address, $"You are now unsubscribed from channel {_channelNames[channelIndex]}.\r\n");
                    }
                    else
                    {
                        transport.Send(address, $"Cannot find channel {parameters[0]}.\r\n");
                        PrintChannels(address, transport);
                    }
                }
                else
                {
                    transport.Send(address, "Unsubscribe takes either 0 or 1 parameters.\r\n");
                }
            }
        }
        return true;
    }

    public override void SendHelp(IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, "The logger will accept user log data via the Log(...) function.\r\n");
        transport.Send(address, "Each log is associated with a named channel.\r\n");
        transport.Send(address, "You can subscribe to or unsubscribe from named channels.\r\n");
        lock (_gate)
            PrintChannels(address, transport);
    }

    /// <summary>OnConnectionLost: unsubscribes the system from everything.</summary>
    public override void OnConnectionLost(SystemAddress address, IConsoleTransport transport)
    {
        lock (_gate)
            Unsubscribe(address, null);
    }

    /// <summary>OnTransportChange: <see cref="WriteLog"/> sends through <paramref name="transport"/> from now on.</summary>
    public override void OnTransportChange(IConsoleTransport transport)
    {
        lock (_gate)
            _transport = transport;
    }

    private void AddChannelLocked(string channelName)
    {
        for (var i = 0; i < MaxChannels; i++)
        {
            if (_channelNames[i] is null)
            {
                _channelNames[i] = channelName;
                return;
            }
        }
    }

    // PrintChannels (LogCommandParser.cpp:178-193).
    private void PrintChannels(SystemAddress address, IConsoleTransport transport)
    {
        var anyChannels = false;
        transport.Send(address, "CHANNELS:\r\n");
        for (var i = 0; i < MaxChannels; i++)
        {
            if (_channelNames[i] is { } name)
            {
                transport.Send(address, string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {name}\r\n"));
                anyChannels = true;
            }
        }
        if (!anyChannels)
            transport.Send(address, "None.\r\n");
    }

    // Unsubscribe (LogCommandParser.cpp:204-231): from everything, which forgets the system, or from one channel; returns
    // the channel's index, 0 for everything, or -1 for a system that never subscribed or an unknown channel.
    private int Unsubscribe(SystemAddress address, string? channelName)
    {
        for (var i = 0; i < _remoteUsers.Count; i++)
        {
            if (_remoteUsers[i].Address != address)
                continue;
            if (channelName is null)
            {
                _remoteUsers[i] = _remoteUsers[^1];
                _remoteUsers.RemoveAt(_remoteUsers.Count - 1);
                return 0;
            }
            var channelIndex = GetChannelIndexFromName(channelName);
            if (channelIndex != -1)
                _remoteUsers[i] = (address, _remoteUsers[i].Channels & (AllChannels ^ (1u << channelIndex)));
            return channelIndex;
        }
        return -1;
    }

    // Subscribe (LogCommandParser.cpp:232-264): to one channel, or to all (0xFFFF), adding the system if it is new;
    // returns the channel's index, or -1 for an unknown channel and for all.
    private int Subscribe(SystemAddress address, string? channelName)
    {
        var channelIndex = -1;
        if (channelName is not null)
        {
            channelIndex = GetChannelIndexFromName(channelName);
            if (channelIndex == -1)
                return channelIndex;
        }

        var channels = channelName is null ? AllChannels : 1u << channelIndex;
        for (var i = 0; i < _remoteUsers.Count; i++)
        {
            if (_remoteUsers[i].Address == address)
            {
                _remoteUsers[i] = (address, channelName is null ? AllChannels : _remoteUsers[i].Channels | channels);
                return channelIndex;
            }
        }
        _remoteUsers.Add((address, channels));
        return channelIndex;
    }

    // GetChannelIndexFromName (LogCommandParser.cpp:265-277): stops at the first free index.
    private int GetChannelIndexFromName(string channelName)
    {
        for (var i = 0; i < MaxChannels; i++)
        {
            if (_channelNames[i] is not { } name)
                return -1;
            if (ConsoleText.EqualsIgnoreCase(name, channelName))
                return i;
        }
        return -1;
    }
}
