using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Datagrams;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Low-level packet cases, from Samples/Tests/PacketAndLowLevelTestsTest.cpp (SendList, test packets broadcast with
/// several reliabilities and IDs, split progress) and the receipt handling it relies on. PushBackPacket, AllocatePacket
/// and plugins act only inside one peer, so they have no interop counterpart.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class LowLevelPacketScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Send_list_delivers_its_parts_joined_as_one_message(PeerKind client, PeerKind server)
    {
        PeerMatrix.SkipUntilImplemented(client, PeerMatrix.Milestone5.SendList);
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        // PacketAndLowLevelTestsTest.cpp:71-101: five 5-byte strings "AAAA" to "AEEE" with their terminating zero, each
        // starting with ID_USER_PACKET_ENUM+1+i, arrive as one 25-byte message.
        var parts = Enumerable.Range(0, 5)
            .Select(i => (ReadOnlyMemory<byte>)new byte[] { (byte)(ScenarioFlows.UserPacket + 1 + i), (byte)('A' + i), (byte)('A' + i), (byte)('A' + i), 0 })
            .ToList();
        await session.Client.SendListAsync(new SendListOptions(serverGuid, parts), Token);

        var received = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == ScenarioFlows.UserPacket + 1, "the joined message", Timeout, Token);
        Assert.Equal(parts.SelectMany(p => p.ToArray()).ToArray(), received.Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Every_user_message_id_arrives_as_a_one_byte_message_in_order(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        // The smallest message is the ID alone; every ID from ID_USER_PACKET_ENUM to 255 is the application's.
        var ids = Enumerable.Range(ScenarioFlows.UserPacket, 256 - ScenarioFlows.UserPacket).Select(id => (byte)id).ToList();
        foreach (var id in ids)
        {
            await session.Client.SendAsync(new SendOptions(serverGuid, new[] { id }, Reliability.ReliableOrdered), Token);
        }

        var received = await session.ServerInbox.WaitForPacketsAsync(p => p.MessageId >= ScenarioFlows.UserPacket, ids.Count, $"{ids.Count} one-byte messages", Timeout, Token);
        Assert.Equal(ids, received.Select(p => p.Data.Span[0]));
        Assert.All(received, p => Assert.Equal(1, p.Data.Length));
        Assert.Equal(ids.Select(MessageNames.Of), received.Select(p => p.MessageName));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Each_reliability_with_an_ack_receipt_reports_its_receipt_acknowledged(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);

        Reliability[] reliabilities = [Reliability.UnreliableWithAckReceipt, Reliability.ReliableWithAckReceipt, Reliability.ReliableOrderedWithAckReceipt];
        var receipts = new List<uint>();
        foreach (var reliability in reliabilities)
        {
            receipts.Add(await session.Client.SendAsync(new SendOptions(serverGuid, new byte[] { ScenarioFlows.UserPacket, (byte)reliability }, reliability), Token));
        }

        // RakPeer.cpp:1279-1287 numbers receipts from 1 and never reuses 0; each send takes the next one.
        Assert.Equal(receipts.Order(), receipts);
        Assert.Equal(receipts.Count, receipts.Distinct().Count());
        Assert.DoesNotContain(0u, receipts);
        await session.ServerInbox.WaitForPacketsAsync(p => p.MessageId == ScenarioFlows.UserPacket, reliabilities.Length, "the messages", Timeout, Token);
        foreach (var receipt in receipts)
        {
            await ScenarioFlows.WaitForReceiptAsync(session.ClientInbox, MessageId.SndReceiptAcked, receipt, Token);
        }
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task An_unreliable_message_lost_on_the_way_reports_its_receipt_lost(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (serverGuid, _) = await session.ConnectAsync(Token);
        byte[] marker = [ScenarioFlows.UserPacket, 0x10, 0x55, 0x55];
        // Drop exactly the datagram carrying the marked message.
        session.Proxy.Rule = (direction, payload) =>
            direction == ProxyDirection.AToB && Carries(payload, marker) ? new FaultDecision(true, false, TimeSpan.Zero) : null;

        var receipt = await session.Client.SendAsync(new SendOptions(serverGuid, marker, Reliability.UnreliableWithAckReceipt), Token);

        // ReliabilityLayer.cpp:1860-1885: an unreliable message with a receipt whose datagram is not
        // acknowledged in time is reported as ID_SND_RECEIPT_LOSS.
        await ScenarioFlows.WaitForReceiptAsync(session.ClientInbox, MessageId.SndReceiptLoss, receipt, Token);
        Assert.DoesNotContain(session.ServerInbox.Unconsumed, p => p.MessageId == ScenarioFlows.UserPacket);
        Assert.Contains(session.Proxy.Datagrams, d => d.Fault.Drop);
    }

    private static bool Carries(ReadOnlyMemory<byte> datagram, byte[] payload)
    {
        var reader = new BitStreamReader(datagram.Span);
        if ((datagram.Span[0] & 0x80) == 0 || !DatagramHeader.TryRead(ref reader, out var header) || header.Kind != DatagramKind.Data)
        {
            return false;
        }
        var messages = new List<EncapsulatedMessage>();
        return EncapsulatedMessageReader.TryReadAll(datagram[(reader.BitPosition >> 3)..], messages)
            && messages.Exists(m => m.Payload.Span.SequenceEqual(payload));
    }
}
