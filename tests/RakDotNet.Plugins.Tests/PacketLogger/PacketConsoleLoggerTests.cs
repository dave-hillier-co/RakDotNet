using RakDotNet.Plugins.Tests.RemoteConsole;
using RakDotNet.Protocol.Wire;
using static RakDotNet.Plugins.Tests.RemoteConsole.RecordingTransport;

namespace RakDotNet.Plugins.Tests.PacketLogger;

/// <summary>PacketConsoleLogger writing a peer's traffic to the subscribers of its channel (PacketConsoleLogger.cpp).</summary>
public sealed class PacketConsoleLoggerTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly RecordingTransport _transport = new();
    private readonly ConsoleServer _console = new();
    private readonly Plugins.LogCommandParser _logCommandParser = new();
    private readonly PacketConsoleLogger _logger = new();

    public PacketConsoleLoggerTests()
    {
        _logger.LogCommandParser = _logCommandParser;
        _console.AddCommandParser(_logCommandParser);
        _console.SetTransportProvider(_transport, 0);
    }

    [Fact]
    public void Setting_the_parser_adds_the_channel()
    {
        Assert.Equal([PacketConsoleLogger.ChannelName], _logCommandParser.Channels);
    }

    [Fact]
    public void A_subscriber_receives_the_peers_traffic_a_line_at_a_time()
    {
        _transport.Type(Alice, "Logger Subscribe PacketConsoleLogger");
        _console.Update();
        _transport.Clear();
        var server = _mesh.Add(4, _logger);
        var client = _mesh.Add(0);

        _logger.LogHeader();
        _mesh.Connect(client, server);
        client.Connections[0].Send(new OutgoingMessage([134, 1, 2, 3]), Reliability.ReliableOrdered);
        _mesh.Settle();

        var lines = _transport.SentTo(Alice);
        Assert.Equal("Clock,S|R,Typ,Reliable#,Frm #,PktID,BitLn,Time     ,Local IP:Port   ,RemoteIP:Port,SPID,SPIN,SPCO,OI,Suffix,Miscellaneous\r\n", lines[0]);
        Assert.All(lines, line => Assert.EndsWith("\r\n", line, StringComparison.Ordinal));
        Assert.Contains(lines, line => line.StartsWith(",Rcv,Nrm,", StringComparison.Ordinal) && line.Contains(",134,32,", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains(",ID_CONNECTION_REQUEST,", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_is_written_without_a_parser_or_a_subscriber()
    {
        var unattached = new PacketConsoleLogger();

        unattached.WriteLog("line");
        _logger.WriteLog("line");

        Assert.Empty(_transport.Sent);
    }

    public void Dispose() => _mesh.Dispose();
}
