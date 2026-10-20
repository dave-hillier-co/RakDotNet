using System.Net;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// UDPForwarder, from Samples/UDPForwarder: a forwarder in a process of the server's kind forwards between a peer of
/// the client's kind and one of the server's, and the first connects to the second through the forwarder's port.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class UdpForwarderScenarios
{
    // RakNet sends at least every five seconds; the sample adds one for thread latency.
    private static readonly TimeSpan SampleTimeout = TimeSpan.FromMilliseconds(6000);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_peer_connects_to_another_through_the_forwarder(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var forwarder = session.Server;
        var peer0 = await session.AddClientAsync(client, null, Token);
        var peer1 = await session.AddClientAsync(server, null, Token);
        await forwarder.Relay().StartUdpForwarderAsync(cancellationToken: Token);

        var (result, port) = await forwarder.Relay().StartForwardingAsync(peer0.Started.BoundAddress, peer1.Started.BoundAddress, SampleTimeout, "127.0.0.1", Token);
        Assert.Equal("UDPFORWARDER_SUCCESS", result);
        var forwarded = new IPEndPoint(IPAddress.Loopback, port);

        // The sample connects peer 0 to the forwarder's port, which peer 1 receives.
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await peer0.Driver.ConnectAsync(new Harness.ConnectOptions(forwarded), Token));
        var accepted = await peer0.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", Timeout, Token);
        var incoming = await peer1.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NewIncomingConnection, "ID_NEW_INCOMING_CONNECTION", Timeout, Token);
        Assert.Equal(peer1.Started.PeerGuid, accepted.PeerGuid);
        Assert.Equal(forwarded, accepted.Address);
        Assert.Equal(peer0.Started.PeerGuid, incoming.PeerGuid);
        Assert.Equal(forwarded, incoming.Address);

        await peer0.Driver.SendAsync(new SendOptions(peer1.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum, 0x46 }), Token);
        await peer1.Driver.SendAsync(new SendOptions(peer0.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum, 0x57 }), Token);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 0x46], (await peer1.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum, "peer 0's message", Timeout, Token)).Data.ToArray());
        Assert.Equal([(byte)MessageId.UserPacketEnum, 0x57], (await peer0.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum, "peer 1's message", Timeout, Token)).Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Forwarding_requests_get_RakNets_results(PeerKind client, PeerKind server)
    {
        // The forwarder is of the client's kind here, so each kind's results are checked.
        await using var session = await NatSession.StartAsync(server, Token);
        var host = await session.AddClientAsync(client, null, Token);
        var forwarder = host.Relay();
        await forwarder.StartUdpForwarderAsync(maxEntries: 1, Token);
        var a = new IPEndPoint(IPAddress.Loopback, 1000);
        var b = new IPEndPoint(IPAddress.Loopback, 2000);

        // UDPForwarder.cpp:120-121.
        Assert.Equal(("UDPFORWARDER_INVALID_PARAMETERS", 0), await forwarder.StartForwardingAsync(a, b, TimeSpan.Zero, cancellationToken: Token));
        Assert.Equal(("UDPFORWARDER_INVALID_PARAMETERS", 0), await forwarder.StartForwardingAsync(a, b, TimeSpan.FromMilliseconds(600001), cancellationToken: Token));
        var (first, port) = await forwarder.StartForwardingAsync(a, b, SampleTimeout, cancellationToken: Token);
        Assert.Equal("UDPFORWARDER_SUCCESS", first);
        Assert.NotEqual(0, port);
        // Either direction exists already, and only success reports a port.
        Assert.Equal(("UDPFORWARDER_FORWARDING_ALREADY_EXISTS", 0), await forwarder.StartForwardingAsync(b, a, SampleTimeout, cancellationToken: Token));
        // One more than the maximum fits; then there are no sockets.
        var c = new IPEndPoint(IPAddress.Loopback, 3000);
        Assert.Equal("UDPFORWARDER_SUCCESS", (await forwarder.StartForwardingAsync(a, c, SampleTimeout, cancellationToken: Token)).Result);
        Assert.Equal(("UDPFORWARDER_NO_SOCKETS", 0), await forwarder.StartForwardingAsync(b, c, SampleTimeout, cancellationToken: Token));
        Assert.Equal((1, 2), await forwarder.GetUdpForwarderInfoAsync(Token));

        // StopForwarding is applied by the forwarder's next update, in either direction.
        await forwarder.StopForwardingAsync(c, a, Token);
        await WaitForUsedEntriesAsync(forwarder, 1);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task An_entry_that_forwards_nothing_for_its_timeout_is_removed(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var host = await session.AddClientAsync(client, null, Token);
        var forwarder = host.Relay();
        await forwarder.StartUdpForwarderAsync(cancellationToken: Token);
        var timeout = TimeSpan.FromMilliseconds(500);

        var started = DateTime.UtcNow;
        await forwarder.StartForwardingAsync(new IPEndPoint(IPAddress.Loopback, 1000), new IPEndPoint(IPAddress.Loopback, 2000), timeout, cancellationToken: Token);
        await WaitForUsedEntriesAsync(forwarder, 0);

        // UDPForwarder.cpp:563-566: more than the timeout after the entry was made.
        Assert.True(DateTime.UtcNow - started >= timeout, $"removed after {DateTime.UtcNow - started}");
    }

    private static async Task WaitForUsedEntriesAsync(IRelayDriver forwarder, int used)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while ((await forwarder.GetUdpForwarderInfoAsync(Token)).UsedEntries != used)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the forwarder still has more than {used} entries");
            await Task.Delay(10, Token);
        }
    }
}
