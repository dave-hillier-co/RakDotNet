using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Ordered and sequenced delivery on every ordering channel, from Samples/Reliable Ordered Test/ReliableOrderedTest.cpp
/// (random channels, RELIABLE_ORDERED and UNRELIABLE_SEQUENCED, padding up to 25000 bytes, a per-channel counter checked
/// on arrival) and Samples/Tests/ReliableOrderedConvertedTest.cpp (the same check as a pass/fail test).
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class OrderingScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Ordered_and_sequenced_messages_keep_their_order_on_all_32_channels_under_loss_and_reordering(PeerKind client, PeerKind server)
    {
        PeerMatrix.SkipUntilImplemented(client, server, PeerMatrix.Milestone4.Loss);
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var faults = new FaultOptions(Seed: 20261019, LossProbability: 0.1, DuplicationProbability: 0.05, Jitter: TimeSpan.FromMilliseconds(30));

        // Below the MTU. The samples pad past it, but native RakNet joins split parts in arrival order
        // (SplitMessageScenarios), so split messages cannot survive loss between native peers.
        await OrderingFlows.SendOnEveryChannelAsync(session, faults, messagesPerChannel: 12, maxSize: 1000, Token);

        Assert.True(session.Proxy.Datagrams.Any(d => d.Fault.Drop), "The proxy dropped nothing, so the scenario did not exercise loss.");
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Ordered_and_sequenced_messages_keep_their_order_on_all_32_channels_under_reordering_and_duplication(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var faults = new FaultOptions(Seed: 20261020, DuplicationProbability: 0.1, Jitter: TimeSpan.FromMilliseconds(30));

        // Below the MTU, so nothing is split and nothing is lost: only the order of arrival changes.
        await OrderingFlows.SendOnEveryChannelAsync(session, faults, messagesPerChannel: 12, maxSize: 1000, Token);

        Assert.True(session.Proxy.Datagrams.Any(d => d.Fault.Duplicate), "The proxy duplicated nothing.");
    }
}

/// <summary>The steps and assertions of the ordering scenarios, shared with the golden vector capture.</summary>
public static class OrderingFlows
{
    public const int ChannelCount = 32;

    private const byte MessageId = ScenarioFlows.UserPacket + 1;

    // Each channel cycles through these; the last message on every channel is RELIABLE_ORDERED.
    private static readonly Reliability[] Cycle = [Reliability.ReliableOrdered, Reliability.ReliableSequenced, Reliability.UnreliableSequenced];

    /// <summary>
    /// Connects, switches on <paramref name="faults"/>, sends <paramref name="messagesPerChannel"/> messages on each of
    /// the 32 channels, cycling through RELIABLE_ORDERED, RELIABLE_SEQUENCED and UNRELIABLE_SEQUENCED, and checks what
    /// the server delivers.
    /// </summary>
    /// <remarks>
    /// Each message is the ID, its channel, its reliability, its index on the channel (a big-endian u16, as the samples'
    /// packet counter) and seeded padding. Per channel the server must deliver every RELIABLE_ORDERED message and a
    /// subsequence of the rest, in the order sent: a sequenced message older than one already delivered on its channel
    /// is dropped (ReliabilityLayer.cpp:1290-1340), never delivered late. A duplicated UNRELIABLE_SEQUENCED message can
    /// be delivered twice in a row, as native RakNet does. Because the last message on each channel is
    /// ordered, nothing on that channel can be delivered after it, so the stream is complete once every ordered message
    /// has arrived.
    /// </remarks>
    public static async Task SendOnEveryChannelAsync(InteropSession session, FaultOptions faults, int messagesPerChannel, int maxSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(faults);
        var (serverGuid, _) = await session.ConnectAsync(cancellationToken);
        session.Proxy.Faults = faults;

        var random = new Random(faults.Seed);
        var sent = new Dictionary<(byte Channel, ushort Index), byte[]>();
        for (var index = 0; index < messagesPerChannel; index++)
        {
            for (byte channel = 0; channel < ChannelCount; channel++)
            {
                var reliability = index == messagesPerChannel - 1 ? Reliability.ReliableOrdered : Cycle[index % Cycle.Length];
                var data = Message(channel, reliability, (ushort)index, random.Next(5, maxSize + 1), random);
                sent[(channel, (ushort)index)] = data;
                await session.Client.SendAsync(new SendOptions(serverGuid, data, reliability, Channel: channel), cancellationToken);
            }
        }

        var orderedCount = sent.Values.Count(d => (Reliability)d[2] == Reliability.ReliableOrdered);
        var received = await session.ServerInbox.WaitForPacketsAsync(
            p => p.MessageId == MessageId,
            found => found.Count(p => (Reliability)p.Data.Span[2] == Reliability.ReliableOrdered) == orderedCount,
            $"{orderedCount} RELIABLE_ORDERED messages on {ChannelCount} channels",
            TimeSpan.FromSeconds(30),
            cancellationToken);

        foreach (var packet in received)
        {
            var span = packet.Data.Span;
            var key = (span[1], BinaryPrimitives.ReadUInt16BigEndian(span[3..]));
            Assert.True(sent.TryGetValue(key, out var expected), $"Unexpected message on channel {key.Item1} with index {key.Item2}.");
            Assert.Equal(expected, packet.Data.ToArray());
        }

        for (byte channel = 0; channel < ChannelCount; channel++)
        {
            var indexes = received
                .Where(p => p.Data.Span[1] == channel)
                .Select(p => (int)BinaryPrimitives.ReadUInt16BigEndian(p.Data.Span[3..]))
                .ToList();
            // A message may repeat only if it is UNRELIABLE_SEQUENCED: unreliable messages carry no message number to
            // filter duplicates by, and a duplicate that waits in the ordering heap is popped without the sequencing
            // check (ReliabilityLayer.cpp:1360-1419), so it is delivered again straight after the original.
            Assert.True(
                indexes.Zip(indexes.Skip(1)).All(pair =>
                    pair.First < pair.Second
                    || (pair.First == pair.Second && (Reliability)sent[(channel, (ushort)pair.First)][2] == Reliability.UnreliableSequenced)),
                $"Channel {channel} delivered out of order or twice: {string.Join(", ", indexes)}.");
            var ordered = sent.Where(s => s.Key.Channel == channel && (Reliability)s.Value[2] == Reliability.ReliableOrdered).Select(s => (int)s.Key.Index);
            Assert.Superset(ordered.ToHashSet(), indexes.ToHashSet());
        }

        var sequenced = received.Count(p => ((Reliability)p.Data.Span[2]).IsSequenced());
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Delivered {received.Count} of {sent.Count} messages, {sequenced} of them sequenced; the proxy dropped {session.Proxy.Datagrams.Count(d => d.Fault.Drop)} datagrams.");
        Assert.True(sequenced > 0, "No sequenced message was delivered, so the scenario did not exercise sequencing.");
    }

    private static byte[] Message(byte channel, Reliability reliability, ushort index, int size, Random random)
    {
        var data = new byte[size];
        random.NextBytes(data);
        data[0] = MessageId;
        data[1] = channel;
        data[2] = (byte)reliability;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(3), index);
        return data;
    }
}
