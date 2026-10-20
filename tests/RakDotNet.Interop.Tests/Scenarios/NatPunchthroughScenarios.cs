using System.Buffers.Binary;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// NAT punchthrough, from Samples/NATCompleteClient's NatPunchthoughClientFramework and NATCompleteServer's
/// NatPunchthroughServerFramework. Two clients, each behind its own NatEmulator router, connect to the facilitator,
/// which runs NatPunchthroughServer. The listening client calls FindRouterPortStride; the other calls OpenNAT with the
/// listener's GUID, and on ID_NAT_PUNCHTHROUGH_SUCCEEDED connects to the address it came from, as the sample's success
/// text promises. The sender is of the client's kind and the listener and facilitator of the server's, so each pair of
/// kinds punches and coordinates.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class NatPunchthroughScenarios
{
    private const byte Succeeded = (byte)MessageId.NatPunchthroughSucceeded;
    private const byte Failed = (byte)MessageId.NatPunchthroughFailed;

    // Failure takes the start delay and every external port: about four seconds with RakNet's configuration.
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(20);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    /// <summary>
    /// Router pairs and whether punchthrough gets through them: a symmetric router needs address restricted or less on
    /// the other side, a port restricted one port restricted or less (NatTypeDetectionCommon.cpp:21-37).
    /// </summary>
    public static TheoryData<PeerKind, PeerKind, NatType, NatType, bool> Routers
    {
        get
        {
            var data = new TheoryData<PeerKind, PeerKind, NatType, NatType, bool>();
            foreach (var client in Enum.GetValues<PeerKind>())
            foreach (var server in Enum.GetValues<PeerKind>())
            {
                data.Add(client, server, NatType.FullCone, NatType.FullCone, true);
                data.Add(client, server, NatType.PortRestricted, NatType.PortRestricted, true);
                data.Add(client, server, NatType.Symmetric, NatType.FullCone, true);
                data.Add(client, server, NatType.AddressRestricted, NatType.Symmetric, true);
                data.Add(client, server, NatType.Symmetric, NatType.PortRestricted, false);
                data.Add(client, server, NatType.Symmetric, NatType.Symmetric, false);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Routers))]
    public async Task Punchthrough_gets_through_the_routers_that_allow_it_and_the_sender_then_connects(
        PeerKind client, PeerKind server, NatType senderRouter, NatType recipientRouter, bool succeeds)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (sender, recipient) = await StartClientsAsync(session, client, server, senderRouter, recipientRouter);
        // The listener finds its stride in advance; a facilitator on one address cannot tell it.
        await recipient.Nat.FindRouterPortStrideAsync(session.ServerAddress, Token);

        Assert.True(await sender.Nat.OpenNatAsync(recipient.Started.PeerGuid, session.ServerAddress, Token));
        var (atSender, atRecipient) = await WaitForResultsAsync(sender, recipient);

        // PushSuccess and PushFailure (NatPunchthroughClient.cpp:335-348, 1090-1103): 1 for the sender, 0 for the
        // recipient, from the other system.
        var expected = succeeds ? Succeeded : Failed;
        Assert.Equal([expected, 1], atSender.Data.ToArray());
        Assert.Equal([expected, 0], atRecipient.Data.ToArray());
        Assert.Equal(recipient.Started.PeerGuid, atSender.PeerGuid);
        Assert.Equal(sender.Started.PeerGuid, atRecipient.PeerGuid);
        // The coordination and the punches are consumed.
        foreach (var inbox in new[] { sender.Inbox, recipient.Inbox })
        {
            Assert.DoesNotContain(inbox.Unconsumed, p => p.MessageId is (byte)MessageId.OutOfBandInternal or (byte)MessageId.NatGetMostRecentPort
                or (byte)MessageId.NatConnectAtTime or (byte)MessageId.NatTargetNotConnected or (byte)MessageId.NatTargetUnresponsive);
        }
        if (!succeeds)
        {
            // Each predicted port, the reported one and the next two, was opened with a TTL send that died past the
            // sender's own router (NatPunchthroughClient.cpp:193-206).
            Assert.Equal(3, TtlSends(sender));
            Assert.Equal(3, TtlSends(recipient));
            return;
        }

        // Each side heard the other from a mapping of the other's router.
        Assert.Contains(atSender.Address, recipient.Router!.Mappings);
        Assert.Contains(atRecipient.Address, sender.Router!.Mappings);

        // NATCompleteClient: "We can now communicate with the other system, including connecting."
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await sender.Driver.ConnectAsync(new Harness.ConnectOptions(atSender.Address), Token));
        await sender.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == recipient.Started.PeerGuid, "ID_CONNECTION_REQUEST_ACCEPTED from the recipient", Timeout, Token);
        await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == sender.Started.PeerGuid, "ID_NEW_INCOMING_CONNECTION from the sender", Timeout, Token);
        await sender.Driver.SendAsync(new SendOptions(recipient.Started.PeerGuid, new byte[] { (byte)MessageId.UserPacketEnum, 0x4E, 0x41, 0x54 }), Token);
        var message = await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.UserPacketEnum, "the sender's message", Timeout, Token);
        Assert.Equal([(byte)MessageId.UserPacketEnum, 0x4E, 0x41, 0x54], message.Data.ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_recipient_the_facilitator_does_not_know_is_reported_not_connected(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await session.Server.Nat.AttachNatPunchthroughServerAsync(Token);
        var sender = await session.AddClientAsync(client, NatType.FullCone, Token);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);
        var (serverGuid, _) = await session.ConnectAsync(sender, Token);

        Assert.True(await sender.Nat.OpenNatAsync(0x0102030405060708, session.ServerAddress, Token));

        // NatPunchthroughServer.cpp:396-407: the ID and the GUID asked for, which reach the application.
        var notConnected = await sender.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatTargetNotConnected, "ID_NAT_TARGET_NOT_CONNECTED", Timeout, Token);
        Assert.Equal([(byte)MessageId.NatTargetNotConnected, 1, 2, 3, 4, 5, 6, 7, 8], notConnected.Data.ToArray());
        Assert.Equal(serverGuid, notConnected.PeerGuid);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task OpenNat_is_refused_without_a_connection_to_the_facilitator(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var sender = await session.AddClientAsync(client, NatType.FullCone, Token);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);

        Assert.False(await sender.Nat.OpenNatAsync(session.Server.Started.PeerGuid, session.ServerAddress, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_recipient_without_the_plugin_is_reported_unresponsive_to_both(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await session.Server.Nat.AttachNatPunchthroughServerAsync(Token);
        var sender = await session.AddClientAsync(client, NatType.PortRestricted, Token);
        var recipient = await session.AddClientAsync(server, NatType.PortRestricted, Token);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);
        await session.ConnectAsync(sender, Token);
        await session.ConnectAsync(recipient, Token);

        await sender.Nat.OpenNatAsync(recipient.Started.PeerGuid, session.ServerAddress, Token);
        // The recipient never answers ID_NAT_GET_MOST_RECENT_PORT, which reaches its application.
        await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatGetMostRecentPort, "ID_NAT_GET_MOST_RECENT_PORT", Timeout, Token);
        var asked = DateTime.UtcNow;

        // NatPunchthroughServer.cpp:170-189: more than 10 s after the attempt started, both are told, with the other's
        // GUID and the session ID.
        var atSender = await sender.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatTargetUnresponsive, "ID_NAT_TARGET_UNRESPONSIVE", ResultTimeout, Token);
        Assert.InRange(DateTime.UtcNow - asked, TimeSpan.FromSeconds(9.5), TimeSpan.FromSeconds(15));
        Assert.Equal(11, atSender.Data.Length);
        Assert.Equal(recipient.Started.PeerGuid, BinaryPrimitives.ReadUInt64BigEndian(atSender.Data.Span[1..]));
        var atRecipient = await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatTargetUnresponsive, "ID_NAT_TARGET_UNRESPONSIVE", Timeout, Token);
        Assert.Equal(sender.Started.PeerGuid, BinaryPrimitives.ReadUInt64BigEndian(atRecipient.Data.Span[1..]));
        Assert.Equal(atSender.Data.Span[9..].ToArray(), atRecipient.Data.Span[9..].ToArray());
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_recipient_that_leaves_the_facilitator_is_reported_lost(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        await session.Server.Nat.AttachNatPunchthroughServerAsync(Token);
        var sender = await session.AddClientAsync(client, NatType.PortRestricted, Token);
        var recipient = await session.AddClientAsync(server, NatType.PortRestricted, Token);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);
        var (serverGuid, _) = await session.ConnectAsync(sender, Token);
        await session.ConnectAsync(recipient, Token);
        await sender.Nat.OpenNatAsync(recipient.Started.PeerGuid, session.ServerAddress, Token);
        await recipient.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatGetMostRecentPort, "ID_NAT_GET_MOST_RECENT_PORT", Timeout, Token);

        await recipient.Driver.CloseAsync(serverGuid, notify: true, Token);

        // NatPunchthroughServer.cpp:315-321: the lost system's GUID and the session ID.
        var lost = await sender.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatConnectionToTargetLost, "ID_NAT_CONNECTION_TO_TARGET_LOST", Timeout, Token);
        Assert.Equal(11, lost.Data.Length);
        Assert.Equal(recipient.Started.PeerGuid, BinaryPrimitives.ReadUInt64BigEndian(lost.Data.Span[1..]));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task With_retry_on_failure_a_failed_punch_is_tried_again_and_reported_once(PeerKind client, PeerKind server)
    {
        await using var session = await NatSession.StartAsync(server, Token);
        var (sender, recipient) = await StartClientsAsync(session, client, server, NatType.Symmetric, NatType.Symmetric);
        await sender.Nat.ConfigureNatPunchthroughClientAsync(new PunchthroughSettings(RetryOnFailure: true), Token);
        await recipient.Nat.ConfigureNatPunchthroughClientAsync(new PunchthroughSettings(RetryOnFailure: true), Token);

        await sender.Nat.OpenNatAsync(recipient.Started.PeerGuid, session.ServerAddress, Token);
        var (atSender, atRecipient) = await WaitForResultsAsync(sender, recipient, ResultTimeout * 2);

        // NatPunchthroughClient.cpp:416-427: the first failure is kept and the sender asks again; the second is reported.
        Assert.Equal([Failed, 1], atSender.Data.ToArray());
        Assert.Equal([Failed, 0], atRecipient.Data.ToArray());
        // Two attempts, each opening the three predicted ports with a TTL send.
        Assert.Equal(6, TtlSends(sender));
        Assert.Equal(6, TtlSends(recipient));
        await Task.Delay(TimeSpan.FromSeconds(1), Token);
        Assert.DoesNotContain(sender.Inbox.Unconsumed, p => p.MessageId is Failed or Succeeded);
        Assert.DoesNotContain(recipient.Inbox.Unconsumed, p => p.MessageId is Failed or Succeeded);
    }

    private static int TtlSends(NatPeer peer) => peer.Router!.Records.Count(r => r.Outcome == "ttl opened mapping");

    private static async Task<(NatPeer Sender, NatPeer Recipient)> StartClientsAsync(NatSession session, PeerKind client, PeerKind server, NatType senderRouter, NatType recipientRouter)
    {
        await session.Server.Nat.AttachNatPunchthroughServerAsync(Token);
        var sender = await session.AddClientAsync(client, senderRouter, Token);
        var recipient = await session.AddClientAsync(server, recipientRouter, Token);
        await sender.Nat.AttachNatPunchthroughClientAsync(Token);
        await recipient.Nat.AttachNatPunchthroughClientAsync(Token);
        await session.ConnectAsync(sender, Token);
        await session.ConnectAsync(recipient, Token);
        return (sender, recipient);
    }

    private static async Task<(ReceivedPacket AtSender, ReceivedPacket AtRecipient)> WaitForResultsAsync(NatPeer sender, NatPeer recipient, TimeSpan? timeout = null)
    {
        static bool IsResult(ReceivedPacket p) => p.MessageId is Succeeded or Failed;
        var atSender = sender.Inbox.WaitForPacketAsync(IsResult, "ID_NAT_PUNCHTHROUGH_SUCCEEDED or _FAILED at the sender", timeout ?? ResultTimeout, Token);
        var atRecipient = recipient.Inbox.WaitForPacketAsync(IsResult, "ID_NAT_PUNCHTHROUGH_SUCCEEDED or _FAILED at the recipient", timeout ?? ResultTimeout, Token);
        return (await atSender, await atRecipient);
    }
}
