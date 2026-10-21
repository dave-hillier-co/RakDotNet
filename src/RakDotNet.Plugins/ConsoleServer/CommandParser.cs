using System.Globalization;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins;

/// <summary>A command a <see cref="CommandParser"/> understands: RakNet's RegisteredCommand.</summary>
/// <param name="Command">The command's name, matched without regard to case.</param>
/// <param name="CommandHelp">What <c>help &lt;command&gt;</c> shows after the name.</param>
/// <param name="ParameterCount">
/// The parameters the command takes, or <see cref="CommandParser.VariableNumberOfParameters"/> for any number.
/// </param>
public readonly record struct RegisteredCommand(string Command, string CommandHelp, byte ParameterCount);

/// <summary>
/// RakNet's CommandParserInterface: a set of commands a <see cref="ConsoleServer"/> passes remote input to.
/// <see cref="LogCommandParser"/> and <see cref="RakNetCommandParser"/> derive from it.
/// </summary>
/// <remarks>
/// <para>
/// Ported from CommandParserInterface.cpp. Commands are kept sorted by name, compared as _stricmp compares them, in a
/// DataStructures::OrderedList, so <see cref="SendCommandList"/> lists them in that order and a second registration of
/// a name is ignored. The ReturnResult overloads write RakNet's lines, so derived parsers answer as RakNet's do.
/// </para>
/// <para>
/// <see cref="OnCommand"/> gets its parameters as a list where RakNet passes a char** and a count; RakNet's parsers
/// read entries past the count, which are a null pointer and then stale pointers, so a derived parser asking for an
/// index past the end gets null from <see cref="Parameter"/>.
/// </para>
/// </remarks>
public abstract class CommandParser
{
    /// <summary>VARIABLE_NUMBER_OF_PARAMETERS: a command that takes any number of parameters.</summary>
    public const byte VariableNumberOfParameters = 255;

    private readonly List<RegisteredCommand> _commands = [];

    /// <summary>GetName: the name the console shows and commands are directed with, matched without regard to case.</summary>
    public abstract string Name { get; }

    /// <summary>The registered commands in the order RakNet keeps them.</summary>
    public IReadOnlyList<RegisteredCommand> RegisteredCommands => _commands;

    /// <summary>A remote console connected.</summary>
    public virtual void OnNewIncomingConnection(SystemAddress address, IConsoleTransport transport)
    {
    }

    /// <summary>A remote console disconnected.</summary>
    public virtual void OnConnectionLost(SystemAddress address, IConsoleTransport transport)
    {
    }

    /// <summary>Sends what <c>help &lt;parser name&gt;</c> shows before the command list.</summary>
    public abstract void SendHelp(IConsoleTransport transport, SystemAddress address);

    /// <summary>
    /// Runs <paramref name="command"/>, the registered name, with its <paramref name="parameters"/>; the console has
    /// already checked their count. <paramref name="originalString"/> is the line as it was typed.
    /// </summary>
    public abstract bool OnCommand(string command, IReadOnlyList<string> parameters, IConsoleTransport transport, SystemAddress address,
                                   string originalString);

    /// <summary>The console is now using <paramref name="transport"/>.</summary>
    public virtual void OnTransportChange(IConsoleTransport transport)
    {
    }

    /// <summary>GetRegisteredCommand: finds <paramref name="command"/> without regard to case.</summary>
    public virtual bool TryGetRegisteredCommand(string command, out RegisteredCommand registered)
    {
        ArgumentNullException.ThrowIfNull(command);
        var index = FindIndex(command, out var exists);
        registered = exists ? _commands[index] : default;
        return exists;
    }

    /// <summary>
    /// RegisterCommand: adds a command, kept in name order; a name already registered keeps its first registration, as
    /// OrderedList::Insert leaves it (DS_OrderedList.h:174-199).
    /// </summary>
    public virtual void RegisterCommand(byte parameterCount, string command, string commandHelp)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(commandHelp);
        var index = FindIndex(command, out var exists);
        if (!exists)
            _commands.Insert(index, new RegisteredCommand(command, commandHelp, parameterCount));
    }

    /// <summary>
    /// SendCommandList (CommandParserInterface.cpp:94-109): the names separated by <c>", "</c>, then <c>\r\n</c>, each
    /// part a send of its own; or <c>No registered commands\r\n</c>.
    /// </summary>
    public virtual void SendCommandList(IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (_commands.Count == 0)
        {
            transport.Send(address, "No registered commands\r\n");
            return;
        }
        for (var i = 0; i < _commands.Count; i++)
        {
            transport.Send(address, _commands[i].Command);
            if (i < _commands.Count - 1)
                transport.Send(address, ", ");
        }
        transport.Send(address, "\r\n");
    }

    /// <summary>
    /// ParseConsoleString (CommandParserInterface.cpp:48-93): splits <paramref name="text"/> at
    /// <paramref name="delimiter"/>, CR and LF, with <paramref name="delimiterToggle"/> switching the delimiter off and
    /// on (so quotes keep spaces); empty parameters are skipped, and at most <paramref name="maxParameters"/> are kept.
    /// The text ends at its first NUL.
    /// </summary>
    public static IReadOnlyList<string> ParseConsoleString(string text, char delimiter, char delimiterToggle, int maxParameters)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParameters, 1);
        var str = ConsoleText.CString(text).ToCharArray();
        var replaceDelimiter = true;
        for (var i = 0; i < str.Length; i++)
        {
            if (str[i] == delimiter && replaceDelimiter)
                str[i] = '\0';
            if (str[i] is '\n' or '\r')
                str[i] = '\0';
            if (str[i] == delimiterToggle)
            {
                str[i] = '\0';
                replaceDelimiter = !replaceDelimiter;
            }
        }

        var parameters = new List<string>();
        for (var i = 0; i < str.Length;)
        {
            if (str[i] == '\0')
            {
                i++;
                continue;
            }
            var start = i;
            while (i < str.Length && str[i] != '\0')
                i++;
            parameters.Add(new string(str, start, i - start));
            if (parameters.Count >= maxParameters)
                break;
        }
        return parameters;
    }

    /// <summary>The parameter at <paramref name="index"/>, or null past the end, where RakNet reads a null or stale pointer.</summary>
    protected static string? Parameter(IReadOnlyList<string> parameters, int index)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return index < parameters.Count ? parameters[index] : null;
    }

    /// <summary>ReturnResult(bool): <c>{command} returned true.\r\n</c> or <c>false</c>.</summary>
    public virtual void ReturnResult(bool result, string command, IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, result ? $"{command} returned true.\r\n" : $"{command} returned false.\r\n");
    }

    /// <summary>ReturnResult(int): <c>{command} returned {result}.\r\n</c>.</summary>
    public virtual void ReturnResult(int result, string command, IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, string.Create(CultureInfo.InvariantCulture, $"{command} returned {result}.\r\n"));
    }

    /// <summary>
    /// ReturnResult(char*): <c>{command} returned {result}.\r\n</c>, with <c>(null)</c> for null as the C library
    /// prints it.
    /// </summary>
    public virtual void ReturnResult(string? result, string command, IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, $"{command} returned {result ?? "(null)"}.\r\n");
    }

    /// <summary>
    /// ReturnResult(SystemAddress) (CommandParserInterface.cpp:160-167): <c>{command} returned {a} {b}:{port}\r\n</c>,
    /// where a is the address of the system asking, b the result's address and port the result's, as RakNet writes it.
    /// </summary>
    public virtual void ReturnResult(SystemAddress result, string command, IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, string.Create(CultureInfo.InvariantCulture,
            $"{command} returned {address.ToString(writePort: false)} {result.ToString(writePort: false)}:{result.Port}\r\n"));
    }

    /// <summary>ReturnResult with no result: <c>Successfully called {command}.\r\n</c>.</summary>
    public virtual void ReturnResult(string command, IConsoleTransport transport, SystemAddress address)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Send(address, $"Successfully called {command}.\r\n");
    }

    // OrderedList::GetIndexFromKey with RegisteredCommandComp, _stricmp of the key and the command (DS_OrderedList.h:119-171).
    private int FindIndex(string command, out bool exists)
    {
        int low = 0, high = _commands.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var comparison = ConsoleText.CompareIgnoreCase(command, _commands[middle].Command);
            if (comparison == 0)
            {
                exists = true;
                return middle;
            }
            if (comparison < 0)
                high = middle - 1;
            else
                low = middle + 1;
        }
        exists = false;
        return low;
    }
}
