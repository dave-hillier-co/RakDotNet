using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>Each scenario runs over the (client, server) matrix with a <see cref="UdpProxy"/> in between.</summary>
public sealed class ProxiedScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Unconnected_ping_returns_a_pong_carrying_the_offline_ping_response(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await ScenarioFlows.UnconnectedPingAsync(session, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Connect_send_each_reliability_on_two_channels_then_disconnect(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await ScenarioFlows.ConnectSendDisconnectAsync(session, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_message_larger_than_the_mtu_is_split_and_reassembled_intact(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await ScenarioFlows.SplitPacketAsync(session, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Reliable_ordered_messages_arrive_complete_and_in_order_per_channel_under_loss_and_reordering(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var faults = new FaultOptions(Seed: 20261018, LossProbability: 0.2, Delay: TimeSpan.Zero, Jitter: TimeSpan.FromMilliseconds(30));

        await ScenarioFlows.ReliableOrderedUnderLossAsync(session, faults, Token);
    }
}
