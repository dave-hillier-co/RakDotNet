using System.Buffers.Binary;
using System.Net;
using RakDotNet.Plugins.Tests.TcpInterface;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Plugins.Tests.PacketizedTcp;

/// <summary>
/// PacketizedTCP as Samples/FileListTransfer runs it over TCP: each message travels behind a 32-bit length, big-endian
/// on the wire (PacketizedTCP.cpp:44-62; the length is byte-swapped on little-endian hosts), and arrives whole.
/// </summary>
public sealed class PacketizedTcpTests : IDisposable
{
    private readonly Plugins.PacketizedTcp _server = new();
    private readonly Plugins.PacketizedTcp _client = new();
    private readonly Plugins.TcpInterface _raw = new();

    public PacketizedTcpTests()
    {
        Assert.True(_server.Start(0, maxIncomingConnections: 4, bindAddress: "127.0.0.1"));
        Assert.True(_client.Start(0, 0));
        Assert.True(_raw.Start(0, 0));
    }

    private ushort ServerPort => (ushort)_server.ListenEndPoint!.Port;

    public void Dispose()
    {
        _client.Dispose();
        _raw.Dispose();
        _server.Dispose();
    }

    [Fact]
    public void Messages_sent_back_to_back_arrive_as_the_same_messages()
    {
        var (clientAtServer, server) = Connect();
        byte[][] messages = [[1], [], [.. Enumerable.Range(0, 1000).Select(i => (byte)i)], [5, 6]];

        foreach (var message in messages)
            _client.Send(message, server, broadcast: false);

        var received = ReceiveMessages(_server, messages.Length);
        Assert.Equal(messages, received.Select(p => p.Data.ToArray()));
        Assert.All(received, p => Assert.Equal(clientAtServer, p.Address));
        Assert.All(received, p => Assert.Equal(p.Data.Length * 8, p.BitLength));
    }

    [Fact]
    public void A_send_list_is_one_message_of_the_parts_joined()
    {
        var (_, server) = Connect();

        Assert.True(_client.SendList([new byte[] { 1, 2 }, new byte[] { 3 }], server, broadcast: false));
        Assert.False(_client.SendList([Array.Empty<byte>()], server, broadcast: false));

        Assert.Equal([1, 2, 3], ReceiveMessages(_server, 1)[0].Data.ToArray());
    }

    [Fact]
    public void On_the_wire_each_message_is_its_length_big_endian_then_its_bytes()
    {
        var packetizedAtRaw = _raw.Connect("127.0.0.1", ServerPort, block: true);
        var rawAtServer = Poll.Address(_server.HasNewIncomingConnection, "the raw connection");

        _server.Send([0xAA, 0xBB, 0xCC], rawAtServer, broadcast: false);

        Assert.Equal([0, 0, 0, 3, 0xAA, 0xBB, 0xCC], Poll.Bytes(_raw, packetizedAtRaw, 7));
    }

    [Fact]
    public void A_message_split_across_receives_is_reassembled()
    {
        var packetizedAtRaw = _raw.Connect("127.0.0.1", ServerPort, block: true);
        Poll.Address(_server.HasNewIncomingConnection, "the raw connection");

        byte[][] pieces = [[0, 0], [0, 2, 7], [8, 0, 0, 0, 1, 9]];
        foreach (var piece in pieces)
        {
            _raw.Send(piece, packetizedAtRaw, broadcast: false);
            Thread.Sleep(50);
        }

        Assert.Equal([[7, 8], [9]], ReceiveMessages(_server, 2).Select(p => p.Data.ToArray()));
    }

    [Fact]
    public void A_message_that_arrives_in_parts_reports_download_progress_at_each_64_KiB()
    {
        // PacketizedTCP.cpp:211-249: while a message is incomplete, a receive that takes the buffered byte count past a
        // multiple of 65536 queues ID_DOWNLOAD_PROGRESS: the part index, the length / 65536, 65536 (each in host order)
        // and the first 65536 bytes of the message as far as they have arrived.
        var packetizedAtRaw = _raw.Connect("127.0.0.1", ServerPort, block: true);
        Poll.Address(_server.HasNewIncomingConnection, "the raw connection");
        var message = new byte[200_000];
        new Random(3).NextBytes(message);
        var framed = new byte[4 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed, (uint)message.Length);
        message.CopyTo(framed, 4);

        _raw.Send(framed.AsSpan(0, 70_000), packetizedAtRaw, broadcast: false);
        var progress = Poll.Until(() => _server.Receive(), "download progress");
        Thread.Sleep(50);
        _raw.Send(framed.AsSpan(70_000), packetizedAtRaw, broadcast: false);

        var data = progress.Data.Span;
        Assert.Equal((byte)MessageId.DownloadProgress, data[0]);
        Assert.Equal(1 + 12 + 65536, data.Length);
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data[1..]));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(data[5..]));
        Assert.Equal(65536u, BinaryPrimitives.ReadUInt32LittleEndian(data[9..]));
        // At least 65532 message bytes were buffered when the count passed 65536.
        Assert.Equal(message.AsSpan(0, 65532).ToArray(), data.Slice(13, 65532).ToArray());

        var rest = ReceiveMessages(_server, 1, skipProgress: true);
        Assert.Equal(message, rest[0].Data.ToArray());
    }

    [Fact]
    public void Connection_events_pass_through_and_a_close_is_lost_at_the_other_side()
    {
        var (clientAtServer, server) = Connect();

        _client.CloseConnection(server);

        Assert.Equal(clientAtServer, Poll.Address(_server.HasLostConnection, "the lost connection"));
    }

    [Fact]
    public void A_failed_attempt_is_reported()
    {
        var closedPort = TcpInterfaceTests.ClosedPort();

        _client.Connect("127.0.0.1", closedPort, block: false);

        Assert.Equal(new SystemAddress(IPAddress.Loopback, closedPort), Poll.Address(_client.HasFailedConnectionAttempt, "the failed attempt"));
    }

    private (SystemAddress ClientAtServer, SystemAddress Server) Connect()
    {
        _client.Connect("127.0.0.1", ServerPort, block: false);
        var server = Poll.Address(_client.HasCompletedConnectionAttempt, "the completed attempt");
        var clientAtServer = Poll.Address(_server.HasNewIncomingConnection, "the new connection");
        return (clientAtServer, server);
    }

    private static List<TcpPacket> ReceiveMessages(Plugins.PacketizedTcp tcp, int count, bool skipProgress = false)
    {
        var received = new List<TcpPacket>();
        Poll.True(() =>
        {
            while (tcp.Receive() is { } packet)
            {
                if (skipProgress && packet.Data.Length > 0 && packet.Data.Span[0] == (byte)MessageId.DownloadProgress && packet.Data.Length == 1 + 12 + 65536)
                    continue;
                received.Add(packet);
            }
            return received.Count >= count;
        }, $"{count} messages");
        return received;
    }
}
