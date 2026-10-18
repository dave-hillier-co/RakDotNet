using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// The steps and assertions of each scenario, shared by the scenario theories and the golden vector capture.
/// </summary>
public static class ScenarioFlows
{
    public const byte UserPacket = (byte)MessageId.UserPacketEnum;

    private const int MessageSizeUnderLoss = 400;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    public static async Task UnconnectedPingAsync(InteropSession session, CancellationToken cancellationToken)
    {
        var response = "hello"u8.ToArray();
        await session.Server.SetOfflinePingResponseAsync(response, cancellationToken);

        Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, cancellationToken: cancellationToken));

        var pong = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", Timeout, cancellationToken);
        Assert.Equal(session.ServerStarted.PeerGuid, pong.PeerGuid);
        Assert.Equal(session.ProxyEndPoint, pong.Address);
        // RakPeer.cpp:4650-4657 sends ID, time, GUID, magic, then the response; RakPeer.cpp:4693-4699 hands the
        // application ID, TimeMS and the response, so the packet data ends with the response bytes.
        Assert.True(pong.Data.Span.EndsWith(response), $"Pong data {Convert.ToHexStringLower(pong.Data.Span)} does not end with the response.");
    }

    public static async Task ConnectSendDisconnectAsync(InteropSession session, CancellationToken cancellationToken)
    {
        var (serverGuid, clientGuid) = await session.ConnectAsync(cancellationToken);

        Reliability[] reliabilities = [Reliability.ReliableOrdered, Reliability.Unreliable, Reliability.ReliableSequenced, Reliability.ReliableOrderedWithAckReceipt];
        var sent = new List<byte[]>();
        var receipts = new List<uint>();
        foreach (var channel in new byte[] { 0, 3 })
        {
            foreach (var reliability in reliabilities)
            {
                byte[] data = [UserPacket, channel, (byte)reliability, .. System.Text.Encoding.ASCII.GetBytes($"{reliability} on {channel}")];
                var receipt = await session.Client.SendAsync(new SendOptions(serverGuid, data, reliability, Channel: channel), cancellationToken);
                sent.Add(data);
                if (reliability == Reliability.ReliableOrderedWithAckReceipt)
                {
                    receipts.Add(receipt);
                }
            }
        }

        var received = await session.ServerInbox.WaitForPacketsAsync(
            p => p.MessageId == UserPacket, sent.Count, $"{sent.Count} user messages", Timeout, cancellationToken);
        Assert.All(received, p => Assert.Equal(clientGuid, p.PeerGuid));
        Assert.Equal(
            sent.Select(Convert.ToHexStringLower).Order(),
            received.Select(p => Convert.ToHexStringLower(p.Data.Span)).Order());
        // Unreliable messages may arrive in any order, but the ordered and sequenced messages of each channel arrive in the
        // order they were sent. Each message carries its channel at byte 1 and its reliability at byte 2.
        foreach (var channel in new byte[] { 0, 3 })
        {
            Assert.Equal(
                sent.Where(d => d[1] == channel && ((Reliability)d[2]).HasOrderingChannel()).Select(Convert.ToHexStringLower),
                received.Select(p => p.Data.ToArray()).Where(d => d[1] == channel && ((Reliability)d[2]).HasOrderingChannel()).Select(Convert.ToHexStringLower));
        }

        foreach (var receipt in receipts)
        {
            // ReliabilityLayer.cpp:2493-2494: the receipt serial is memcpy'd after the ID, so it is host order
            // (little-endian on every platform we run), not BitStream big-endian.
            await session.ClientInbox.WaitForPacketAsync(
                p => p.MessageId == (byte)MessageId.SndReceiptAcked && p.Data.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[1..]) == receipt,
                $"ID_SND_RECEIPT_ACKED for receipt {receipt}",
                Timeout,
                cancellationToken);
        }

        await session.Client.CloseAsync(serverGuid, notify: true, cancellationToken);
        var disconnected = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, cancellationToken);
        Assert.Equal(clientGuid, disconnected.PeerGuid);
    }

    public static async Task SplitPacketAsync(InteropSession session, CancellationToken cancellationToken)
    {
        var (serverGuid, _) = await session.ConnectAsync(cancellationToken);
        var data = new byte[4000];
        data[0] = UserPacket;
        for (var i = 1; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        await session.Client.SendAsync(new SendOptions(serverGuid, data, Reliability.ReliableOrdered), cancellationToken);

        var received = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == UserPacket, "the 4000-byte message", Timeout, cancellationToken);
        Assert.Equal(data, received.Data.ToArray());
    }

    public static async Task ReliableOrderedUnderLossAsync(InteropSession session, FaultOptions faults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(faults);
        var (serverGuid, _) = await session.ConnectAsync(cancellationToken);
        // Faults start after the handshake: this scenario is about ordered delivery, not connection setup.
        session.Proxy.Faults = faults;

        const int count = 200;
        const int channels = 4;
        for (var i = 0; i < count; i++)
        {
            var channel = (byte)(i % channels);
            var sequence = (ushort)(i / channels);
            // Padding spreads the messages over many datagrams; tiny messages would share a handful and see little loss.
            var data = new byte[MessageSizeUnderLoss];
            data[0] = UserPacket;
            data[1] = channel;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), sequence);
            await session.Client.SendAsync(new SendOptions(serverGuid, data, Reliability.ReliableOrdered, Channel: channel), cancellationToken);
        }

        var received = await session.ServerInbox.WaitForPacketsAsync(
            p => p.MessageId == UserPacket, count, $"{count} ordered messages under loss", TimeSpan.FromSeconds(30), cancellationToken);

        for (byte channel = 0; channel < channels; channel++)
        {
            var sequences = received
                .Where(p => p.Data.Span[1] == channel)
                .Select(p => BinaryPrimitives.ReadUInt16BigEndian(p.Data.Span[2..]))
                .ToList();
            Assert.Equal(Enumerable.Range(0, count / channels).Select(s => (ushort)s), sequences);
        }

        var dropped = session.Proxy.Datagrams.Count(d => d.Fault.Drop);
        TestContext.Current.TestOutputHelper?.WriteLine($"Proxy dropped {dropped} of {session.Proxy.Datagrams.Count} datagrams.");
        Assert.True(dropped > 0, "The proxy dropped nothing, so the scenario did not exercise loss.");
    }
}
