using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>What the relaying scenarios do with the peers of a <see cref="NatSession"/>.</summary>
public static class RelayPeers
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>The peer's <see cref="IRelayDriver"/>.</summary>
    public static IRelayDriver Relay(this NatPeer peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        return (IRelayDriver)peer.Driver;
    }

    /// <summary>Connects <paramref name="client"/> to <paramref name="server"/>, which is on the network directly, and waits for both sides.</summary>
    /// <returns>The address <paramref name="server"/> sees <paramref name="client"/> at.</returns>
    public static async Task<IPEndPoint> ConnectToAsync(this NatPeer client, NatPeer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await client.Driver.ConnectAsync(new ConnectOptions(server.Started.BoundAddress), cancellationToken).ConfigureAwait(false));
        await client.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == server.Started.PeerGuid,
            $"ID_CONNECTION_REQUEST_ACCEPTED from {server.Label}", NatSession.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var incoming = await server.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == client.Started.PeerGuid,
            $"ID_NEW_INCOMING_CONNECTION from {client.Label}", NatSession.DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return incoming.Address;
    }

    /// <summary>Waits until every connection of <paramref name="peer"/> has an average ping, which Router2 needs.</summary>
    public static async Task WaitForPingsAsync(this NatPeer peer, int connections, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var deadline = DateTime.UtcNow + NatSession.DefaultTimeout;
        while (true)
        {
            var info = await peer.Driver.GetInfoAsync(cancellationToken).ConfigureAwait(false);
            if (info.Connections.Count >= connections && info.Connections.All(c => c.AveragePing >= 0))
                return;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{peer.Label}: no average ping on all of {connections} connections: {string.Join(", ", info.Connections.Select(c => $"{c.Address} {c.AveragePing}"))}");
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for a UDPProxy result handler call that <paramref name="predicate"/> accepts.</summary>
    public static async Task<UdpProxyCallback> WaitForUdpProxyCallbackAsync(this NatPeer peer, Func<UdpProxyCallback, bool> predicate, string expectation,
                                                                           TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(predicate);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var calls = await peer.Relay().GetUdpProxyCallbacksAsync(cancellationToken).ConfigureAwait(false);
            if (calls.FirstOrDefault(predicate) is { } call)
                return call;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"{peer.Label}: no {expectation} within {timeout}; calls: {string.Join(", ", calls.Select(c => c.Name))}");
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
