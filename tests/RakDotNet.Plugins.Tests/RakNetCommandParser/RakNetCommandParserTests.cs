using System.Net;
using RakDotNet.Protocol.Wire;
using static RakDotNet.Plugins.Tests.RemoteConsole.RecordingTransport;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>RakNetCommandParser calling a peer from a console, with RakNet's replies (RakNetCommandParser.cpp).</summary>
public sealed class RakNetCommandParserTests : IDisposable
{
    private readonly PluginMesh _mesh = new();
    private readonly RecordingTransport _transport = new();
    private readonly ConsoleServer _console = new();
    private readonly Plugins.RakNetCommandParser _parser = new();
    private readonly RakPeer _peer;

    public RakNetCommandParserTests()
    {
        _peer = _mesh.Add(4);
        _parser.Peer = _peer;
        _console.AddCommandParser(_parser);
        _console.SetTransportProvider(_transport, 0);
    }

    [Fact]
    public void Without_a_peer_the_parser_answers_nothing_and_its_help_says_so()
    {
        var parser = new Plugins.RakNetCommandParser();

        Assert.False(parser.OnCommand("IsActive", [], _transport, Alice, "IsActive"));
        parser.SendHelp(_transport, Alice);

        Assert.Equal(["Parser not active.  Call SetRakPeerInterface.\r\n"], _transport.SentTo(Alice));
    }

    [Fact]
    public void Help_lists_the_commands_in_name_order()
    {
        Assert.Equal(
            "The RakNet parser provides mirror functions to RakPeer\r\nSystemAddresss take two parameters: send <BinaryAddress> <Port>.\r\n" +
            "For bool, send 1 or 0.\r\nCOMMAND LIST:\r\n" +
            "AddToBanList, AddToSecurityExceptionList, AdvertiseSystem, AllowConnectionResponseIPMigration, ClearBanList, CloseConnection, " +
            "Connect, DisableSecurity, Disconnect, GetAveragePing, GetConnectionList, GetExternalID, GetIncomingPassword, GetIndexFromSystemAddress, " +
            "GetInternalID, GetLastPing, GetLocalIP, GetLowestPing, GetMaximumIncomingConnections, GetMTUSize, GetNumberOfAddresses, " +
            "GetSystemAddressFromIndex, InitializeSecurity, IsActive, IsBanned, IsConnected, IsInSecurityExceptionList, IsNetworkSimulatorActive, " +
            "Ping1, Ping2, RemoveFromBanList, RemoveFromSecurityExceptionList, SetIncomingPassword, SetMaximumIncomingConnections, " +
            "SetOccasionalPing, SetOfflinePingResponse, SetTimeoutTime, Startup\r\n\r\n",
            Run("help RakNet"));
    }

    [Fact]
    public void Incoming_connections_are_set_and_read()
    {
        Assert.Equal("Successfully called SetMaximumIncomingConnections.\r\n", Run("RakNet SetMaximumIncomingConnections 2"));
        _mesh.Step();

        Assert.Equal("GetMaximumIncomingConnections returned 2.\r\n", Run("GetMaximumIncomingConnections"));
        Assert.Equal(2, _peer.MaxIncomingConnections);
    }

    [Fact]
    public void Queries_answer_with_RakNet_values()
    {
        Assert.Equal("IsActive returned true.\r\n", Run("IsActive"));
        Assert.Equal("GetMTUSize returned 576.\r\n", Run("GetMTUSize"));
        Assert.Equal("GetConnectionList() returned no systems connected.\r\n", Run("GetConnectionList"));
        Assert.Equal("GetIndexFromSystemAddress returned -1.\r\n", Run("GetIndexFromSystemAddress 1.2.3.4|5"));
        Assert.Equal("GetSystemAddressFromIndex returned 10.0.0.1 UNASSIGNED_SYSTEM_ADDRESS:0\r\n", Run("GetSystemAddressFromIndex 0"));
        Assert.Equal("GetAveragePing returned -1.\r\n", Run("GetAveragePing 1.2.3.4|5"));
        Assert.Equal("GetIncomingPassword returned 0.\r\n", Run("GetIncomingPassword"));
        Assert.Equal("InitializeSecurity returned false.\r\n", Run("InitializeSecurity"));
        Assert.Equal("Startup returned 1.\r\n", Run("Startup 8 0 127.0.0.1 unused"));
    }

    [Fact]
    public void A_connected_system_is_listed_and_found_by_index_and_address()
    {
        var client = _mesh.Add(0);
        _mesh.Connect(client, _peer);
        var clientAddress = _peer.Connections[0].Address;
        var text = clientAddress.ToString(writePort: true);

        Assert.Equal($"GetConnectionList() returned:\r\n0 {text}\r\n", Run("GetConnectionList"));
        Assert.Equal($"GetIndexFromSystemAddress returned {_peer.Connections[0].SystemIndex}.\r\n", Run($"GetIndexFromSystemAddress {text}"));
        Assert.Equal(
            $"GetSystemAddressFromIndex returned 10.0.0.1 {clientAddress.ToString(writePort: false)}:{clientAddress.Port}\r\n",
            Run($"GetSystemAddressFromIndex {_peer.Connections[0].SystemIndex}"));
        Assert.Equal($"GetLastPing returned {(int)_peer.Connections[0].LastPing!.Value.TotalMilliseconds}.\r\n", Run($"GetLastPing {text}"));
        Assert.Equal("GetLastPing returned -1.\r\n", Run("GetLastPing 9.9.9.9|1"));
    }

    [Fact]
    public void The_ban_list_is_changed_and_queried()
    {
        Assert.Equal("Successfully called AddToBanList.\r\n", Run("AddToBanList 1.2.3.4 0"));
        Assert.Equal("IsBanned returned true.\r\n", Run("IsBanned 1.2.3.4"));
        Assert.Equal("Successfully called RemoveFromBanList.\r\n", Run("RemoveFromBanList 1.2.3.4"));
        Assert.Equal("IsBanned returned false.\r\n", Run("IsBanned 1.2.3.4"));
    }

    [Fact]
    public void The_password_is_set_from_the_text_and_its_length()
    {
        Assert.Equal("Successfully called SetIncomingPassword.\r\n", Run("SetIncomingPassword secret 3"));

        Assert.Equal("sec"u8.ToArray(), _peer.IncomingPassword.ToArray());
        Assert.Equal("GetIncomingPassword returned sec.\r\n", Run("GetIncomingPassword"));
    }

    [Fact]
    public void Commands_registered_without_an_answer_answer_nothing_and_GetConnectionState_is_unknown()
    {
        Assert.Equal("", Run("IsConnected a b"));
        Assert.Equal("", Run("IsNetworkSimulatorActive"));
        Assert.Equal("Unknown command:  Type 'help' for help.\r\n", Run("GetConnectionState 1.2.3.4|5"));
    }

    [Fact]
    public void The_security_exception_commands_read_a_missing_parameter()
    {
        _peer.SecurityExceptions.Add("1.2.3.4");

        Assert.Equal("Successfully called AddToSecurityExceptionList.\r\n", Run("AddToSecurityExceptionList 5.6.7.8"));
        Assert.Equal("IsInSecurityExceptionList returned false.\r\n", Run("IsInSecurityExceptionList 1.2.3.4"));
        Assert.Equal("Successfully called RemoveFromSecurityExceptionList.\r\n", Run("RemoveFromSecurityExceptionList 9.9.9.9"));

        Assert.Empty(_peer.SecurityExceptions);
    }

    [Fact]
    public void Disconnect_shuts_the_peer_down_and_Startup_starts_another()
    {
        Assert.Equal("Successfully called Disconnect.\r\n", Run("Disconnect 0 0"));
        Assert.False(_peer.IsActive);
        Assert.Equal("IsActive returned false.\r\n", Run("IsActive"));
        Assert.Equal("GetConnectionList() returned false.\r\n", Run("GetConnectionList"));
        Assert.Equal("Successfully called SetOccasionalPing.\r\n", Run("SetOccasionalPing 1"));
        Assert.Equal("Startup returned 3.\r\n", Run("Startup 0 0 127.0.0.1 x"));

        Assert.Equal("Startup returned 0.\r\n", Run("Startup 2 0 127.0.0.1 x"));

        using var started = _parser.Peer!;
        Assert.NotSame(_peer, started);
        Assert.True(started.IsActive);
        Assert.Equal(2, started.MaxConnections);
        Assert.Equal(IPAddress.Loopback, started.Sockets[0].LocalAddress.ToIPEndPoint().Address);
    }

    [Theory]
    [InlineData("CloseConnection x 1 0", "Successfully called CloseConnection.\r\n")]
    [InlineData("CloseConnection 1.2.3.4|5 1 99", "Successfully called CloseConnection.\r\n")]
    [InlineData("Connect 127.0.0.1 0 x 0", "Connect returned false.\r\n")]
    [InlineData("Connect 127.0.0.1 1 x 0", "Connect returned true.\r\n")]
    [InlineData("SetTimeoutTime -1 x", "Successfully called SetTimeoutTime.\r\n")]
    [InlineData("AddToBanList 1.2.3.4 -1", "Successfully called AddToBanList.\r\n")]
    [InlineData("SetOfflinePingResponse x 4000", "Successfully called SetOfflinePingResponse.\r\n")]
    public void Arguments_the_API_refuses_get_RakNet_replies(string line, string reply)
    {
        Assert.Equal(reply, Run(line));
    }

    [Fact]
    public void Advertised_data_RakNet_only_asserts_on_is_reported_as_called()
    {
        var data = new string('a', 1900);

        Assert.Equal("Successfully called AdvertiseSystem.\r\n", Run($"AdvertiseSystem 127.0.0.1 1 {data} 1900"));
    }

    [Fact]
    public void A_host_name_the_resolver_refuses_is_not_pinged_or_advertised_to()
    {
        var host = new string('a', 1900);

        Assert.Equal("Successfully called Ping2.\r\n", Run($"Ping2 {host} 1 0"));
        Assert.Equal("Successfully called AdvertiseSystem.\r\n", Run($"AdvertiseSystem {host} 1 x 1"));
        Assert.Equal("Connect returned false.\r\n", Run($"Connect {host} 1 x 1"));
    }

    [Fact]
    public void No_command_and_parameters_make_the_console_update_throw()
    {
        var long_ = new string('a', 1900);
        string[][] parameterSets =
        [
            ["x", "x", "x", "x"],
            ["-1", "-1", "-1", "-1"],
            ["4294967295", "4294967295", "4294967295", "4294967295"],
            ["127.0.0.1", "0", long_, "1900"],
            ["127.0.0.1|0", "65536", "", "-5"],
            [long_, "1", long_, "1900"],
        ];
        // Disconnect ends the peer, so it runs last.
        var commands = Commands().Where(c => c != "Disconnect").Append("Disconnect");

        foreach (var command in commands)
        {
            foreach (var parameters in parameterSets)
            {
                var line = $"{command} {string.Join(' ', parameters)}";
                var exception = Record.Exception(() => Run(line));
                Assert.True(exception is null, $"{line[..Math.Min(line.Length, 60)]}: {exception}");
            }
        }
    }

    private static IEnumerable<string> Commands()
    {
        var help = new RecordingTransport();
        new Plugins.RakNetCommandParser { Peer = null }.SendCommandList(help, Alice);
        return help.TextSentTo(Alice).Split(["COMMAND LIST:\r\n", ", ", "\r\n"], StringSplitOptions.RemoveEmptyEntries)
                   .Where(c => !c.Contains(' ', StringComparison.Ordinal));
    }

    public void Dispose() => _mesh.Dispose();

    private string Run(string line)
    {
        _transport.Clear();
        _transport.Type(Alice, line);
        _console.Update();
        return _transport.TextSentTo(Alice);
    }
}
