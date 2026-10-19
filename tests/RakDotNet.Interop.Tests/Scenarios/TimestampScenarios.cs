using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Connected pings and ID_TIMESTAMP, from Samples/Tests/PingTestsTest.cpp (repeated connected pings, then the last,
/// lowest and average ping) and Samples/Timestamping/Timestamping.cpp (each side sends ID_TIMESTAMP with its
/// RakNet::GetTime, and the receiver sees it in its own clock).
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class TimestampScenarios
{
    // The clock differential comes from a ping and pong (RakPeer.cpp:4393-4415), so it is right to within a loopback
    // round trip; the sender's time is also read a command before the send.
    private const long ToleranceMs = 100;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Connected_pings_measure_the_round_trip_through_the_proxy(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        // PingTestsTest.cpp:73-96: a connected ping every 30 ms, then the statistics.
        for (var i = 0; i < 20; i++)
        {
            await session.Client.PingConnectedAsync(serverGuid, Token);
            await Task.Delay(TimeSpan.FromMilliseconds(30), Token);
        }

        var connection = Assert.Single((await session.Client.GetInfoAsync(Token)).Connections);
        TestContext.Current.TestOutputHelper?.WriteLine($"Ping last {connection.LastPing} ms, lowest {connection.LowestPing} ms, average {connection.AveragePing} ms.");
        // PingTestsTest.cpp:98-120 and :167-178: on loopback the last ping is within 100 ms, the lowest within 10 ms,
        // the last no lower than the lowest, and the average not negative.
        Assert.InRange(connection.LastPing, 0, 100);
        Assert.InRange(connection.LowestPing, 0, 10);
        Assert.True(connection.LastPing >= connection.LowestPing, $"Last ping {connection.LastPing} is below the lowest {connection.LowestPing}.");
        Assert.InRange(connection.AveragePing, 0, 100);
        // RakPeer::Ping(SystemAddress) is answered inside the server's RakPeer; neither application sees the ping or pong.
        Assert.DoesNotContain(session.ClientInbox.Unconsumed, p => p.MessageId is (byte)MessageId.ConnectedPing or (byte)MessageId.ConnectedPong);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_timestamped_message_arrives_with_the_senders_time_in_the_receivers_clock(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        // RakNet::GetTime counts from process start, so a client started this much later than the server has a clock
        // that far behind, and only a rewritten timestamp lands near the receiver's own time.
        await Task.Delay(ClockOffset, Token);
        var late = await session.AddClientAsync(client, Token);
        // Timestamping.cpp:26-27: occasional pings keep the clock differential current.
        await late.Driver.SetOccasionalPingAsync(true, Token);
        await session.Server.SetOccasionalPingAsync(true, Token);
        var (serverGuid, clientGuid) = await session.ConnectAsync(late, Token);

        await SendTimestampedAsync(late, serverGuid, session.ServerPeer);
        await SendTimestampedAsync(session.ServerPeer, clientGuid, late);
    }

    private static TimeSpan ClockOffset => TimeSpan.FromMilliseconds(4 * ToleranceMs);

    // Timestamping.cpp:89-92: ID_TIMESTAMP, then RakNet::GetTime as a BitStream (big-endian) u64, sent RELIABLE; a user
    // message ID follows so the receiver can tell what was stamped. RakPeer.cpp:1545-1553 and :3773-3790 rewrite the
    // time on arrival by subtracting the sender's clock differential, so the receiver reads it in its own clock.
    private static async Task SendTimestampedAsync(InteropPeer sender, ulong receiverGuid, InteropPeer receiver)
    {
        var sentTime = await sender.Driver.GetTimeAsync(Token);
        var data = new byte[1 + sizeof(ulong) + 2];
        data[0] = (byte)MessageId.Timestamp;
        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(1), sentTime);
        data[9] = ScenarioFlows.UserPacket;
        data[10] = 0x5a;
        await sender.Driver.SendAsync(new SendOptions(receiverGuid, data, Reliability.Reliable), Token);

        var received = await receiver.Inbox.WaitForPacketAsync(
            p => p.MessageId == ScenarioFlows.UserPacket && p.Timestamp is not null, $"a timestamped message from the {sender.Label}", InteropSession.DefaultTimeout, Token);
        Assert.Equal(new byte[] { ScenarioFlows.UserPacket, 0x5a }, received.Data.Span[9..].ToArray());
        var timestamp = (long)received.Timestamp!.Value;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{sender.Label} sent at {sentTime} in its clock; the {receiver.Label} read {timestamp} and received it at {received.TimeMs} in its own.");
        Assert.InRange(received.TimeMs - timestamp, -ToleranceMs, ToleranceMs);
        Assert.True(
            Math.Abs(received.TimeMs - (long)sentTime) >= ClockOffset.TotalMilliseconds - ToleranceMs,
            "The peers' clocks are too close together to show that the timestamp was rewritten.");
    }
}
