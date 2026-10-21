using System.Net;
using System.Text;
using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// TCPInterface between two peers, as Samples/CommandConsoleServer (through TelnetTransport) and
/// Samples/ComprehensivePCGame use it: a server listening, clients connecting without listening, the connection events
/// polled each frame, and bytes either way. TCP may split or join sends, so the byte stream is compared, not packets.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class TcpInterfaceScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_client_connects_exchanges_bytes_and_its_close_is_a_lost_connection_at_the_server(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(cancellationToken: Token);

        // A non-blocking Connect returns UNASSIGNED_SYSTEM_ADDRESS (TCPInterface.cpp:391-417).
        Assert.Null(await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: false, Token));
        var completed = await clientPeer.WaitForKindAsync("completedConnectionAttempt", Timeout, Token);
        Assert.Equal(serverPeer.EndPoint, completed.Address);
        var clientAtServer = (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;
        Assert.Equal(IPAddress.Loopback, clientAtServer.Address);
        Assert.Equal([clientAtServer], (await serverPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).Connections);
        Assert.Equal([serverPeer.EndPoint], (await clientPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).Connections);

        await clientPeer.Tcp.TcpSendAsync(serverPeer.EndPoint, "hello"u8.ToArray(), cancellationToken: Token);
        Assert.True(await clientPeer.Tcp.TcpSendListAsync(serverPeer.EndPoint, ["ab"u8.ToArray(), Array.Empty<byte>(), "c"u8.ToArray()], cancellationToken: Token));
        Assert.Equal("helloabc", await ReceiveTextAsync(serverPeer, clientAtServer, "helloabc"));

        await serverPeer.Tcp.TcpSendAsync(clientAtServer, "reply"u8.ToArray(), cancellationToken: Token);
        Assert.Equal("reply", await ReceiveTextAsync(clientPeer, serverPeer.EndPoint, "reply"));

        await clientPeer.Tcp.TcpCloseAsync(serverPeer.EndPoint, Token);
        Assert.Equal(clientAtServer, (await serverPeer.WaitForKindAsync("lostConnection", Timeout, Token)).Address);
        Assert.Empty((await serverPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).Connections);
        Assert.Empty((await clientPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).Connections);
        // CloseConnection queues nothing on the side that closes (TCPInterface.cpp:576-614).
        Assert.DoesNotContain(await clientPeer.SettleAsync(TimeSpan.FromMilliseconds(200), Token), e => e.Kind == "lostConnection");
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_server_that_closes_the_connection_is_a_lost_connection_at_the_client(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(cancellationToken: Token);

        // A blocking Connect returns the server and still queues the completed attempt (TCPInterface.cpp:356-390).
        Assert.Equal(serverPeer.EndPoint, await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: true, Token));
        await clientPeer.WaitForKindAsync("completedConnectionAttempt", Timeout, Token);
        var clientAtServer = (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;

        await serverPeer.Tcp.TcpSendAsync(clientAtServer, "bye"u8.ToArray(), cancellationToken: Token);
        await serverPeer.CloseWhenSentAsync(clientAtServer, Timeout, Token);

        Assert.Equal("bye", await ReceiveTextAsync(clientPeer, serverPeer.EndPoint, "bye"));
        Assert.Equal(serverPeer.EndPoint, (await clientPeer.WaitForKindAsync("lostConnection", Timeout, Token)).Address);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_connection_attempt_to_a_stopped_server_fails(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(cancellationToken: Token);
        // Stop closes the listening socket (TCPInterface.cpp:266-277).
        await serverPeer.Tcp.StopTcpAsync(Token);
        Assert.False((await serverPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).Started);

        Assert.Null(await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: false, Token));
        Assert.Equal(serverPeer.EndPoint, (await clientPeer.WaitForKindAsync("failedConnectionAttempt", Timeout, Token)).Address);
        Assert.Null(await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: true, Token));
        Assert.Equal(serverPeer.EndPoint, (await clientPeer.WaitForKindAsync("failedConnectionAttempt", Timeout, Token)).Address);
        Assert.Equal(0, (await clientPeer.Tcp.GetTcpInfoAsync(cancellationToken: Token)).ConnectionCount);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_broadcast_reaches_every_client_but_the_one_excluded(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var firstPeer = await TcpPeer.LaunchAsync(client, "first client", Token);
        // The second client is of the server's kind, so every pair of kinds talks.
        await using var secondPeer = await TcpPeer.LaunchAsync(server, "second client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await firstPeer.StartClientAsync(cancellationToken: Token);
        await secondPeer.StartClientAsync(cancellationToken: Token);
        await firstPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: true, Token);
        var first = (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;
        await secondPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: true, Token);
        var second = (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;

        await serverPeer.Tcp.TcpSendAsync(first, "all but first"u8.ToArray(), broadcast: true, Token);
        await serverPeer.Tcp.TcpSendAsync(null, "!"u8.ToArray(), broadcast: true, Token);

        Assert.Equal("all but first!", await ReceiveTextAsync(secondPeer, serverPeer.EndPoint, "all but first!"));
        Assert.Equal("!", await ReceiveTextAsync(firstPeer, serverPeer.EndPoint, "!"));
        Assert.NotEqual(first, second);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Megabytes_sent_either_way_arrive_intact(PeerKind client, PeerKind server)
    {
        await using var serverPeer = await TcpPeer.LaunchAsync(server, "server", Token);
        await using var clientPeer = await TcpPeer.LaunchAsync(client, "client", Token);
        await serverPeer.ListenAsync(cancellationToken: Token);
        await clientPeer.StartClientAsync(cancellationToken: Token);
        await clientPeer.Tcp.TcpConnectAsync(serverPeer.EndPoint, block: true, Token);
        var clientAtServer = (await serverPeer.WaitForKindAsync("newIncomingConnection", Timeout, Token)).Address!;
        var data = new byte[2_000_000];
        new Random(11).NextBytes(data);

        await clientPeer.Tcp.TcpSendAsync(serverPeer.EndPoint, data, cancellationToken: Token);
        await serverPeer.Tcp.TcpSendAsync(clientAtServer, data, cancellationToken: Token);

        Assert.Equal(data, await serverPeer.ReceiveBytesAsync(clientAtServer, r => r.Length >= data.Length, "2 MB from the client", Timeout, Token));
        Assert.Equal(data, await clientPeer.ReceiveBytesAsync(serverPeer.EndPoint, r => r.Length >= data.Length, "2 MB from the server", Timeout, Token));
        Assert.Equal(0, (await clientPeer.Tcp.GetTcpInfoAsync(serverPeer.EndPoint, Token)).OutgoingBytes);
    }

    private static async Task<string> ReceiveTextAsync(TcpPeer peer, IPEndPoint from, string expected) =>
        Encoding.ASCII.GetString(await peer.ReceiveBytesAsync(from, r => r.Length >= expected.Length, $"\"{expected}\" from {from}", Timeout, Token));
}
