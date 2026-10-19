using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Split messages, from Samples/BigPacketTest/BigPacketTest.cpp (one huge RELIABLE_ORDERED_WITH_ACK_RECEIPT message with
/// ID_DOWNLOAD_PROGRESS notifications), Samples/MessageSizeTest/MessageSizeTest.cpp (every message size from 1 byte up,
/// checking count and content) and the split progress check of Samples/Tests/PacketAndLowLevelTestsTest.cpp.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class SplitMessageScenarios
{
    // BigPacketTest sends 83296256 bytes; a few megabytes exercise the same path in a fraction of the time.
    private const int BigMessageSize = 4 * 1024 * 1024;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The largest message sent whole: ReliabilityLayer::Send splits anything longer than
    /// GetMaxDatagramSizeExcludingMessageHeaderBytes minus the largest message header (ReliabilityLayer.cpp:1604-1606).
    /// Split parts are the same size (SplitPacket, ReliabilityLayer.cpp:2928-2950).
    /// </summary>
    public static int LargestUnsplitSize => DatagramLimits.MaxPayloadBits(ScenarioFlows.LoopbackMtu) / 8 - (EncapsulatedMessage.MaxHeaderLengthBits + 7) / 8;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_message_of_several_megabytes_is_split_and_reassembled_intact_and_its_receipt_acknowledged(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (_, clientGuid) = await session.ConnectAsync(Token);

        // BigPacketTest.cpp:126-137: the server sends the big message to the client that connected, ID 255 and then
        // 255 - (i & 255), at LOW_PRIORITY with an ack receipt.
        var data = new byte[BigMessageSize];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(255 - (i & 255));
        }
        var receipt = await session.Server.SendAsync(
            new SendOptions(clientGuid, data, Reliability.ReliableOrderedWithAckReceipt, Priority.Low), Token);

        // Matched by length: a native receiver may put another part first (see IsDeliveredAsSent).
        var received = await session.ClientInbox.WaitForPacketAsync(p => p.Data.Length == BigMessageSize, $"the {BigMessageSize}-byte message", TimeSpan.FromSeconds(60), Token);
        Assert.True(IsDeliveredAsSent(client, data, received.Data), "The reassembled message differs from the one sent.");
        await ScenarioFlows.WaitForReceiptAsync(session.ServerInbox, MessageId.SndReceiptAcked, receipt, Token);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Split_message_progress_is_reported_every_interval_parts_with_the_first_part(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (_, clientGuid) = await session.ConnectAsync(Token);
        const int interval = 10;
        await session.Client.SetSplitMessageProgressIntervalAsync(interval, Token);

        var data = new byte[100_000];
        new Random(30).NextBytes(data);
        data[0] = ScenarioFlows.UserPacket;
        await session.Server.SendAsync(new SendOptions(clientGuid, data, Reliability.ReliableOrdered), Token);

        var received = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == ScenarioFlows.UserPacket, "the split message", InteropSession.DefaultTimeout, Token);
        Assert.Equal(data, received.Data.ToArray());
        // Progress is reported as parts arrive, so every notification came before the message itself.
        var progress = session.ClientInbox.Unconsumed.Where(p => p.MessageId == (byte)MessageId.DownloadProgress).ToList();
        AssertProgress(data, interval, progress);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Messages_of_every_size_up_to_the_largest_unsplit_size_arrive_intact(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        int[] strides = [1, 2, 3, 7, 64, 255, 256, 1000, LargestUnsplitSize - 1, LargestUnsplitSize];
        await SendStridesAsync(session, strides);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Messages_just_past_the_largest_unsplit_size_and_at_the_part_boundaries_are_split_and_arrive_intact(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        var part = LargestUnsplitSize;
        int[] strides = [part + 1, 2 * part, 2 * part + 1, 3 * part];
        await SendStridesAsync(session, strides);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Split_parts_that_arrive_out_of_order_are_joined_in_index_order_except_by_native_receivers(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        var size = 2 * LargestUnsplitSize;
        var data = new byte[size];
        new Random(31).NextBytes(data);
        data[0] = ScenarioFlows.UserPacket;
        // Hold back every datagram carrying the first part, including resends after the receiver's NAK, until the
        // second part has arrived.
        session.Proxy.Rule = (direction, payload) =>
            direction == ProxyDirection.AToB && CarriesSplitPart(payload, index: 0)
                ? new FaultDecision(false, false, TimeSpan.FromMilliseconds(200))
                : null;
        await session.Client.SendAsync(new SendOptions(serverGuid, data, Reliability.ReliableOrdered), Token);

        var received = await session.ServerInbox.WaitForPacketAsync(p => p.Data.Length == size, $"the {size}-byte message", InteropSession.DefaultTimeout, Token);
        Assert.Equal(Convert.ToHexStringLower(ExpectedAfterReordering(server, data)), Convert.ToHexStringLower(received.Data.Span));
    }

    /// <summary>
    /// What a receiver of <paramref name="kind"/> delivers when a two-part message's second part arrives first. Native
    /// RakNet 4.081 joins parts in arrival order, so it swaps the halves (docs/plan.md, "Wire format notes"). That is a
    /// receive-side defect, not wire format, and the pass criterion is that the receiver observes the message sent, so
    /// RakDotNet joins parts by split index.
    /// </summary>
    private static byte[] ExpectedAfterReordering(PeerKind kind, byte[] data) => kind switch
    {
        PeerKind.Native => [.. data.AsSpan(LargestUnsplitSize), .. data.AsSpan(0, LargestUnsplitSize)],
        PeerKind.RakDotNet => data,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>
    /// Whether a receiver of <paramref name="kind"/> delivered <paramref name="data"/> as it should. A burst of thousands
    /// of parts over loopback can lose a datagram, and its resend then arrives after later parts. Native RakNet 4.081
    /// joins parts in arrival order (docs/plan.md, "Wire format notes"), so a native receiver delivers the parts joined
    /// in some order; RakDotNet joins them by split index, so it delivers the message sent.
    /// </summary>
    private static bool IsDeliveredAsSent(PeerKind kind, byte[] data, ReadOnlyMemory<byte> delivered) => kind switch
    {
        PeerKind.Native => SplitParts.IsJoinedInSomeOrder(delivered, data, LargestUnsplitSize),
        PeerKind.RakDotNet => data.AsSpan().SequenceEqual(delivered.Span),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>MessageSizeTest.cpp:37-74: for each stride, enough messages of that size to cover 4000 bytes.</summary>
    private static async Task SendStridesAsync(InteropSession session, IEnumerable<int> strides)
    {
        var (serverGuid, _) = await session.ConnectAsync(Token);
        var pattern = new byte[strides.Max()];
        pattern[0] = ScenarioFlows.UserPacket;
        for (var i = 1; i < pattern.Length; i++)
        {
            pattern[i] = (byte)(i % 256);
        }

        foreach (var stride in strides)
        {
            var data = pattern.AsMemory(0, stride);
            var count = 0;
            for (var sum = 0; sum < 4000; sum += stride)
            {
                await session.Client.SendAsync(new SendOptions(serverGuid, data, Reliability.ReliableOrdered), Token);
                count++;
            }

            var received = await session.ServerInbox.WaitForPacketsAsync(
                p => p.MessageId == ScenarioFlows.UserPacket, count, $"{count} messages of {stride} bytes", InteropSession.DefaultTimeout, Token);
            Assert.All(received, p => Assert.Equal(Convert.ToHexStringLower(data.Span), Convert.ToHexStringLower(p.Data.Span)));
        }
    }

    // ReliabilityLayer.cpp:3154-3171: ID, parts arrived, part count and the first part's length, each a host-order
    // (little-endian) u32 copied with memcpy, then the first part.
    private static void AssertProgress(byte[] data, int interval, List<ReceivedPacket> progress)
    {
        Assert.NotEmpty(progress);
        var partLength = BinaryPrimitives.ReadUInt32LittleEndian(progress[0].Data.Span[9..]);
        var partCount = (data.Length + (int)partLength - 1) / (int)partLength;
        var arrived = progress.Select(p => (int)BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[1..])).ToList();
        Assert.Equal(Enumerable.Range(1, (partCount - 1) / interval).Select(i => i * interval), arrived);
        Assert.All(progress, p =>
        {
            Assert.Equal((uint)partCount, BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[5..]));
            Assert.Equal(partLength, BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[9..]));
            Assert.Equal(Convert.ToHexStringLower(data.AsSpan(0, (int)partLength)), Convert.ToHexStringLower(p.Data.Span[13..]));
        });
    }

    private static bool CarriesSplitPart(ReadOnlyMemory<byte> datagram, int index)
    {
        var reader = new BitStreamReader(datagram.Span);
        if ((datagram.Span[0] & 0x80) == 0 || !DatagramHeader.TryRead(ref reader, out var header) || header.Kind != DatagramKind.Data)
        {
            return false;
        }
        var messages = new List<EncapsulatedMessage>();
        return EncapsulatedMessageReader.TryReadAll(datagram[(reader.BitPosition >> 3)..], messages)
            && messages.Exists(m => m.Split is { } split && split.Index == index);
    }
}
