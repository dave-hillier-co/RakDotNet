using System.Buffers.Binary;
using System.Net;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// PacketizedTCP between two peers, as Samples/FileListTransfer runs over TCP (USE_TCP): every message sent arrives as
/// one packet, whatever TCP did to the stream, which shows the 32-bit big-endian length framing (PacketizedTCP.cpp:44-62)
/// agrees in both directions.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class PacketizedTcpScenarios
{
    // ID_DOWNLOAD_PROGRESS, the part index, part count and part length, then 65536 bytes (PacketizedTCP.cpp:219-246).
    private const int ProgressLength = 1 + 12 + 65536;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Messages_sent_back_to_back_arrive_whole_and_in_order_both_ways(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        var clientAtServer = await ConnectAsync(serverPeer, clientPeer);
        byte[][] messages = [[(byte)MessageId.UserPacketEnum, 1], [], [.. Enumerable.Range(0, 1000).Select(i => (byte)i)], Random(70_000, 5), [42]];

        foreach (var message in messages)
            await clientPeer.Tcp.TcpSendAsync(serverPeer.EndPoint, message, cancellationToken: Token);
        byte[][] replies = [.. Enumerable.Reverse(messages)];
        foreach (var reply in replies)
            await serverPeer.Tcp.TcpSendAsync(clientAtServer, reply, cancellationToken: Token);

        Assert.Equal(messages, await ReceiveMessagesAsync(serverPeer, clientAtServer, messages.Length));
        Assert.Equal(replies, await ReceiveMessagesAsync(clientPeer, serverPeer.EndPoint, replies.Length));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_send_list_arrives_as_one_message_and_an_empty_one_is_refused(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        var clientAtServer = await ConnectAsync(serverPeer, clientPeer);

        Assert.True(await clientPeer.Tcp.TcpSendListAsync(serverPeer.EndPoint, [new byte[] { 1, 2 }, Array.Empty<byte>(), new byte[] { 3 }], cancellationToken: Token));
        Assert.False(await clientPeer.Tcp.TcpSendListAsync(serverPeer.EndPoint, [Array.Empty<byte>()], cancellationToken: Token));
        await clientPeer.Tcp.TcpSendAsync(serverPeer.EndPoint, new byte[] { 4 }, cancellationToken: Token);

        Assert.Equal([[1, 2, 3], [4]], await ReceiveMessagesAsync(serverPeer, clientAtServer, 2));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_large_message_arrives_whole_after_any_download_progress(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        var clientAtServer = await ConnectAsync(serverPeer, clientPeer);
        var message = Random(500_000, 9);

        await clientPeer.Tcp.TcpSendAsync(serverPeer.EndPoint, message, cancellationToken: Token);

        // How the stream is cut into receives decides whether progress is reported; what is reported must agree with
        // the message.
        var packets = await ReceivePacketsAsync(serverPeer, clientAtServer, 1);
        Assert.Equal(message, packets[^1]);
        var lastIndex = 0u;
        foreach (var progress in packets[..^1])
        {
            Assert.Equal(ProgressLength, progress.Length);
            Assert.Equal((byte)MessageId.DownloadProgress, progress[0]);
            var index = BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(1));
            Assert.True(index > lastIndex);
            lastIndex = index;
            Assert.Equal((uint)message.Length / 65536, BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(5)));
            Assert.Equal(65536u, BinaryPrimitives.ReadUInt32LittleEndian(progress.AsSpan(9)));
            // At least 65532 bytes of the message were buffered; RakNet leaves the rest of its copy unwritten.
            Assert.Equal(message.AsSpan(0, 65532).ToArray(), progress.AsSpan(13, 65532).ToArray());
        }
    }

    private static async Task<IPEndPoint> ConnectAsync(TcpPeer serverPeer, TcpPeer clientPeer)
    {
        await serverPeer.ListenAsync(packetized: true, cancellationToken: Token);
        await clientPeer.StartClientAsync(packetized: true, cancellationToken: Token);
        await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: false, Token);
        Assert.Equal(serverPeer.EndPoint, (await clientPeer.WaitForKindAsync("completedConnectionAttempt", Timeout, Token)).Address);
        return (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;
    }

    // The messages from one sender, skipping download progress.
    private static async Task<byte[][]> ReceiveMessagesAsync(TcpPeer peer, IPEndPoint from, int count) =>
        [.. (await ReceivePacketsAsync(peer, from, count)).Where(p => !IsProgress(p))];

    // Every packet from one sender until count messages, not counting download progress, have arrived.
    private static async Task<byte[][]> ReceivePacketsAsync(TcpPeer peer, IPEndPoint from, int count)
    {
        var packets = new List<byte[]>();
        while (packets.Count(p => !IsProgress(p)) < count)
        {
            var packet = await peer.WaitForAsync(e => e.Kind == "packet" && Equals(e.Address, from), $"message {packets.Count + 1} from {from}", Timeout, Token);
            packets.Add(packet.Data);
        }
        return [.. packets];
    }

    private static bool IsProgress(byte[] packet) => packet.Length == ProgressLength && packet[0] == (byte)MessageId.DownloadProgress;

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        data[0] = (byte)MessageId.UserPacketEnum;
        return data;
    }
}
