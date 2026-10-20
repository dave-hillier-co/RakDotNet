using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>
/// A console transport standing in for the network: lines and connection events are queued by the test, and every send
/// is recorded.
/// </summary>
internal sealed class RecordingTransport : IConsoleTransport
{
    public static readonly SystemAddress Alice = new(System.Net.IPAddress.Parse("10.0.0.1"), 1001);
    public static readonly SystemAddress Bob = new(System.Net.IPAddress.Parse("10.0.0.2"), 1002);

    public Queue<TransportPacket> Incoming { get; } = new();

    public Queue<SystemAddress> NewConnections { get; } = new();

    public Queue<SystemAddress> LostConnections { get; } = new();

    public List<(SystemAddress To, string Text)> Sent { get; } = [];

    public List<SystemAddress> Closed { get; } = [];

    public List<ushort> Starts { get; } = [];

    public int Stops { get; private set; }

    public CommandParser? CommandParser { get; init; }

    /// <summary>A line as RakNetTransport2 delivers it: the text and a zero byte.</summary>
    public void Type(SystemAddress from, string line) =>
        Incoming.Enqueue(new TransportPacket(from, Encoding.Latin1.GetBytes(line + "\0")));

    public string[] SentTo(SystemAddress address) => [.. Sent.Where(s => s.To == address).Select(s => s.Text)];

    public string TextSentTo(SystemAddress address) => string.Concat(SentTo(address));

    public void Clear() => Sent.Clear();

    public bool Start(ushort port, bool serverMode)
    {
        Assert.True(serverMode);
        Starts.Add(port);
        return true;
    }

    public void Stop() => Stops++;

    public void Send(SystemAddress address, string text)
    {
        if (text.Length > 0)
            Sent.Add((address, text));
    }

    public void CloseConnection(SystemAddress address) => Closed.Add(address);

    public TransportPacket? Receive() => Incoming.TryDequeue(out var packet) ? packet : null;

    public SystemAddress HasNewIncomingConnection() => NewConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;

    public SystemAddress HasLostConnection() => LostConnections.TryDequeue(out var address) ? address : SystemAddress.Unassigned;
}

/// <summary>A parser with commands of fixed and variable counts that records what it ran.</summary>
internal sealed class EchoParser(string name = "Echo") : CommandParser
{
    public List<(string Command, string[] Parameters, SystemAddress From, string Original)> Ran { get; } = [];

    public List<IConsoleTransport> TransportChanges { get; } = [];

    public List<SystemAddress> Connected { get; } = [];

    public List<SystemAddress> Lost { get; } = [];

    public override string Name => name;

    public EchoParser WithCommand(byte parameterCount, string command, string help = "help text")
    {
        RegisterCommand(parameterCount, command, help);
        return this;
    }

    public override void SendHelp(IConsoleTransport transport, SystemAddress address) => transport.Send(address, $"{name} help\r\n");

    public override bool OnCommand(string command, IReadOnlyList<string> parameters, IConsoleTransport transport, SystemAddress address, string originalString)
    {
        Ran.Add((command, [.. parameters], address, originalString));
        transport.Send(address, $"{name} ran {command}\r\n");
        return true;
    }

    public override void OnTransportChange(IConsoleTransport transport) => TransportChanges.Add(transport);

    public override void OnNewIncomingConnection(SystemAddress address, IConsoleTransport transport) => Connected.Add(address);

    public override void OnConnectionLost(SystemAddress address, IConsoleTransport transport) => Lost.Add(address);
}
