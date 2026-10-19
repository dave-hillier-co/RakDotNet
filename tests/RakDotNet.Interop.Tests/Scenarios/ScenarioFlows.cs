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
            await WaitForReceiptAsync(session.ClientInbox, MessageId.SndReceiptAcked, receipt, cancellationToken);
        }

        await session.Client.CloseAsync(serverGuid, notify: true, cancellationToken);
        var disconnected = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.DisconnectionNotification, "ID_DISCONNECTION_NOTIFICATION", Timeout, cancellationToken);
        Assert.Equal(clientGuid, disconnected.PeerGuid);
    }

    /// <summary>
    /// The MTU every combination negotiates on loopback: the first probe, MAXIMUM_MTU_SIZE (RakPeer.cpp:104, MTUSize.h:33),
    /// fits.
    /// </summary>
    public const int LoopbackMtu = Protocol.Wire.Datagrams.DatagramLimits.MaximumMtuSize;

    /// <summary>Decodes a packet the application received with the wire codec, failing the test if it does not parse.</summary>
    public static T Decode<T>(ReadOnlyMemory<byte> packet)
        where T : Protocol.Wire.Messages.IWireMessage<T>
    {
        var reader = new BitStreamReader(packet.Span);
        Assert.True(T.TryRead(ref reader, out var value), $"{typeof(T).Name} did not decode from {Convert.ToHexStringLower(packet.Span)}.");
        return value;
    }

    /// <summary>Polls GetConnectionState until the peer reports <paramref name="state"/> for <paramref name="target"/>.</summary>
    public static async Task WaitForStateAsync(IPeerDriver driver, ulong target, string state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(driver);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Timeout);
        var last = "";
        try
        {
            while ((last = await driver.GetConnectionStateAsync(target, cancellationToken)) != state)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{driver.Kind} peer did not reach {state} for {target} within {Timeout.TotalSeconds} s; it is {last}.");
        }
    }

    /// <summary>Waits for ID_SND_RECEIPT_ACKED or ID_SND_RECEIPT_LOSS carrying <paramref name="receipt"/>.</summary>
    public static Task<ReceivedPacket> WaitForReceiptAsync(PacketInbox inbox, MessageId id, uint receipt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbox);
        // ReliabilityLayer.cpp:2493-2494: the receipt serial is memcpy'd after the ID, so it is host order
        // (little-endian on every platform we run), not BitStream big-endian.
        return inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)id && p.Data.Length >= 5 && BinaryPrimitives.ReadUInt32LittleEndian(p.Data.Span[1..]) == receipt,
            $"{MessageNames.Of((byte)id)} for receipt {receipt}",
            Timeout,
            cancellationToken);
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

    public static Task ReliableOrderedUnderLossAsync(InteropSession session, FaultOptions faults, CancellationToken cancellationToken) =>
        ReliableOrderedUnderLossAsync(session, faults, count: 200, MessageSizeUnderLoss, cancellationToken);

    /// <summary>
    /// Connects, switches on <paramref name="faults"/> and sends <paramref name="count"/> RELIABLE_ORDERED messages of
    /// <paramref name="messageSize"/> bytes over four channels; every one must arrive, in order per channel.
    /// </summary>
    public static async Task ReliableOrderedUnderLossAsync(InteropSession session, FaultOptions faults, int count, int messageSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(faults);
        var (serverGuid, _) = await session.ConnectAsync(cancellationToken);
        // Faults start after the handshake: this scenario is about ordered delivery, not connection setup.
        session.Proxy.Faults = faults;

        const int channels = 4;
        for (var i = 0; i < count; i++)
        {
            var channel = (byte)(i % channels);
            var sequence = (ushort)(i / channels);
            // Padding spreads the messages over many datagrams; tiny messages would share a handful and see little loss.
            var data = new byte[messageSize];
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
