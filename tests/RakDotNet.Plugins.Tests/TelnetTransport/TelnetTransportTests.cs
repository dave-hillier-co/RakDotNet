using System.Text;
using RakDotNet.Plugins.Tests.TcpInterface;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>
/// TelnetTransport serving a TCP client over loopback: negotiation, echo, line assembly and RakNet's quirks
/// (TelnetTransport.cpp).
/// </summary>
public sealed class TelnetTransportTests : IDisposable
{
    private static readonly byte[] WillEcho = [255, 251, 1];

    private readonly Plugins.TelnetTransport _telnet = new();
    private readonly Plugins.TcpInterface _client = new();
    private SystemAddress _server;

    public TelnetTransportTests()
    {
        Assert.True(_telnet.Start(0, serverMode: true));
        Assert.True(_client.Start(0, 0));
    }

    [Fact]
    public void A_new_client_is_told_the_server_will_echo()
    {
        var atServer = Connect();

        Assert.Equal(WillEcho, Poll.Bytes(_client, _server, 3));
        Assert.Single(_client.GetConnectionList());
        Assert.NotEqual(SystemAddress.Unassigned, atServer);
    }

    [Fact]
    public void A_line_is_echoed_and_returned_without_its_line_end()
    {
        var atServer = ConnectAndDrain();

        Type("help\r\n");
        var line = ReceiveLine();

        Assert.Equal("help", Encoding.ASCII.GetString(line.Data.Span));
        Assert.Equal(atServer, line.Address);
        Assert.Equal("help\r\n", Text(Poll.Bytes(_client, _server, 6)));
    }

    [Fact]
    public void A_backspace_removes_a_character_and_is_echoed_with_a_space_and_a_backspace()
    {
        ConnectAndDrain();

        Type("hx\bi\n");

        Assert.Equal("hi", Encoding.ASCII.GetString(ReceiveLine().Data.Span));
        Assert.Equal("hx\bi\n \b", Text(Poll.Bytes(_client, _server, 7)));
    }

    [Fact]
    public void Only_the_first_line_of_a_packet_is_returned()
    {
        ConnectAndDrain();

        Type("first\nsecond\n");

        Assert.Equal("first", Encoding.ASCII.GetString(ReceiveLine().Data.Span));
        Poll.Never(() => _telnet.Receive() is not null, "a second line");
    }

    [Fact]
    public void A_packet_starting_with_escape_tab_or_a_telnet_command_is_dropped_unechoed()
    {
        ConnectAndDrain();

        Type("\u001b[B");
        Poll.Never(() => _telnet.Receive() is not null, "a line from a cursor key", 150);
        _client.Send([255, 253, 1], _server, broadcast: false);
        Poll.Never(() => _telnet.Receive() is not null, "a line from IAC DO ECHO", 150);
        Type("\tx\n");
        Poll.Never(() => _telnet.Receive() is not null, "a line starting with tab", 150);

        Type("ok\n");
        Assert.Equal("ok", Encoding.ASCII.GetString(ReceiveLine().Data.Span));
        Assert.Equal("ok\n", Text(Poll.Bytes(_client, _server, 3)));
    }

    [Fact]
    public void The_up_arrow_replaces_the_line_with_the_last_one_using_the_whole_line_buffer()
    {
        ConnectAndDrain();
        Type("help\n");
        ReceiveLine();
        Poll.Bytes(_client, _server, 5);

        Type("ab");
        ServerReturnsNoLine();
        Poll.Bytes(_client, _server, 2);
        Type("\u001b[A");
        ServerReturnsNoLine();

        // The buffer holds "ab" over the tail of "help", so four backspaces go before the recalled line.
        Assert.Equal("\b\b\b\bhelp", Text(Poll.Bytes(_client, _server, 8)));
        Type("\n");
        Assert.Equal("help", Encoding.ASCII.GetString(ReceiveLine().Data.Span));
    }

    [Fact]
    public void Data_from_a_client_not_yet_reported_as_connected_is_dropped()
    {
        _server = _client.Connect("127.0.0.1", (ushort)_telnet.ListenEndPoint!.Port, block: true);
        Type("early\n");

        Poll.Never(() => _telnet.Receive() is not null, "a line before the connection was reported");
        Poll.Address(_telnet.HasNewIncomingConnection, "the new connection");

        Assert.Equal(WillEcho, Poll.Bytes(_client, _server, 3));
        Poll.Never(() => _telnet.Receive() is not null || _client.Receive() is not null, "a line or an echo");
    }

    [Fact]
    public void Sends_carry_the_prefix_and_suffix()
    {
        var atServer = ConnectAndDrain();
        _telnet.SendPrefix = "[";
        _telnet.SendSuffix = "]";

        _telnet.Send(atServer, "text");
        _telnet.Send(atServer, "");

        Assert.Equal("[text]", Text(Poll.Bytes(_client, _server, 6)));
    }

    [Fact]
    public void A_send_is_cut_to_2047_characters_with_the_suffix_cut_first()
    {
        var atServer = ConnectAndDrain();
        _telnet.SendSuffix = "!!";

        _telnet.Send(atServer, new string('a', 2046));

        Assert.Equal(new string('a', 2046) + "!", Text(Poll.Bytes(_client, _server, 2047)));
    }

    [Fact]
    public void A_lost_client_is_reported_and_forgotten()
    {
        var atServer = ConnectAndDrain();

        _client.CloseConnection(_server);

        Assert.Equal(atServer, Poll.Address(_telnet.HasLostConnection, "the lost connection"));
    }

    [Fact]
    public void A_stopped_transport_does_nothing()
    {
        _telnet.Stop();

        Assert.Null(_telnet.ListenEndPoint);
        Assert.Null(_telnet.Receive());
        Assert.Equal(SystemAddress.Unassigned, _telnet.HasNewIncomingConnection());
        _telnet.Send(SystemAddress.Unassigned, "nothing");
    }

    public void Dispose()
    {
        _client.Dispose();
        _telnet.Dispose();
    }

    private SystemAddress Connect()
    {
        _server = _client.Connect("127.0.0.1", (ushort)_telnet.ListenEndPoint!.Port, block: true);
        Assert.NotEqual(SystemAddress.Unassigned, _server);
        return Poll.Address(_telnet.HasNewIncomingConnection, "the new connection");
    }

    private SystemAddress ConnectAndDrain()
    {
        var atServer = Connect();
        Poll.Bytes(_client, _server, 3);
        return atServer;
    }

    private void Type(string text) => _client.Send(Encoding.ASCII.GetBytes(text), _server, broadcast: false);

    // The server handles input only when polled.
    private void ServerReturnsNoLine() => Poll.Never(() => _telnet.Receive() is not null, "a line", 100);

    private TransportPacket ReceiveLine() => Poll.Until(_telnet.Receive, "a line");

    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);
}
