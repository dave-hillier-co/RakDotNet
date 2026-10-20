using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.RemoteConsole;

/// <summary>RakNetTransport2 on a peer, with plain peers as console clients (RakNetTransport2.cpp).</summary>
public sealed class RakNetTransport2Tests : IDisposable
{
    private const byte TransportString = (byte)MessageId.TransportString;

    private readonly PluginMesh _mesh = new();
    private readonly Plugins.RakNetTransport2 _transport = new();
    private readonly RakPeer _server;
    private readonly RakPeer _client;

    public RakNetTransport2Tests()
    {
        _server = _mesh.Add(4, _transport);
        _client = _mesh.Add(0);
    }

    [Fact]
    public void A_connection_is_a_new_console_connection()
    {
        var clientAtServer = Connect();

        Assert.Equal(SystemAddress.Unassigned, _transport.HasNewIncomingConnection());
        Assert.NotEqual(SystemAddress.Unassigned, clientAtServer);
    }

    [Fact]
    public void A_transport_string_is_consumed_and_returned_without_its_ID()
    {
        var clientAtServer = Connect();
        var serverAtClient = _client.Connections[0];

        serverAtClient.Send(new OutgoingMessage([TransportString, .. "help"u8, 0]), Reliability.ReliableOrdered);
        var packet = Receive();

        Assert.Equal(clientAtServer, packet.Address);
        Assert.Equal(_client.RakNetGuid, packet.RakNetGuid);
        Assert.Equal("help\0"u8.ToArray(), packet.Data.ToArray());
        Assert.Equal(40, packet.BitLength);
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == TransportString);
    }

    [Fact]
    public void A_transport_string_with_no_text_is_consumed_and_dropped()
    {
        Connect();

        _client.Connections[0].Send(new OutgoingMessage([TransportString]), Reliability.ReliableOrdered);
        _mesh.Settle();

        Assert.Null(_transport.Receive());
        Assert.DoesNotContain(_mesh.Seen(_server), e => e.Id == TransportString);
    }

    [Fact]
    public void Send_writes_the_ID_the_text_and_a_zero_byte()
    {
        var clientAtServer = Connect();

        _transport.Send(clientAtServer, "Goodbye!\r\n");
        _transport.Send(clientAtServer, "");

        var received = WaitForStrings(_client, 1);
        Assert.Equal([TransportString, .. "Goodbye!\r\n"u8, 0], received[0]);
        _mesh.Settle();
        Assert.Single(_mesh.Seen(_client), e => e.Id == TransportString);
    }

    [Fact]
    public void Sending_to_the_unassigned_address_sends_to_everyone()
    {
        Connect();
        var second = _mesh.Add(0);
        _mesh.Connect(second, _server);

        _transport.Send(SystemAddress.Unassigned, "all");

        Assert.Equal([TransportString, .. "all"u8, 0], WaitForStrings(_client, 1)[0]);
        Assert.Equal([TransportString, .. "all"u8, 0], WaitForStrings(second, 1)[0]);
    }

    [Fact]
    public void Closing_a_connection_disconnects_it_and_reports_it_lost()
    {
        var clientAtServer = Connect();

        _transport.CloseConnection(clientAtServer);

        _mesh.RunUntil(() => _mesh.Seen(_client).Any(e => e.Kind == PeerEventKind.Disconnected), "the client disconnected");
        _mesh.RunUntil(() => _transport.HasLostConnection() == clientAtServer, "the lost connection");
    }

    [Fact]
    public void A_client_that_disconnects_is_reported_lost()
    {
        var clientAtServer = Connect();

        _client.Close(_client.Connections[0]);

        _mesh.RunUntil(() => _transport.HasLostConnection() == clientAtServer, "the lost connection");
    }

    [Fact]
    public void Stop_forgets_what_was_queued_and_sends_after_shutdown_do_nothing()
    {
        var clientAtServer = Connect();
        _client.Connections[0].Send(new OutgoingMessage([TransportString, .. "x"u8, 0]), Reliability.ReliableOrdered);
        _mesh.Settle();

        _transport.Stop();
        _server.Dispose();

        Assert.Null(_transport.Receive());
        _transport.Send(clientAtServer, "after");
        _transport.CloseConnection(clientAtServer);
    }

    public void Dispose() => _mesh.Dispose();

    private SystemAddress Connect()
    {
        _mesh.Connect(_client, _server);
        SystemAddress address = default;
        _mesh.RunUntil(() => (address = _transport.HasNewIncomingConnection()) != SystemAddress.Unassigned, "the new connection");
        return address;
    }

    private TransportPacket Receive()
    {
        TransportPacket? packet = null;
        _mesh.RunUntil(() => (packet = _transport.Receive()) is not null, "a line");
        return packet!;
    }

    private List<byte[]> WaitForStrings(RakPeer peer, int count)
    {
        _mesh.RunUntil(() => _mesh.Seen(peer).Count(e => e.Id == TransportString) >= count, $"{count} transport strings");
        return [.. _mesh.Seen(peer).Where(e => e.Id == TransportString).Select(e => e.Data)];
    }
}
