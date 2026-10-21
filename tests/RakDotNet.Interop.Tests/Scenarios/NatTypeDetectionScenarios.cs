using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// NAT type detection, from Samples/NATCompleteClient's NatTypeDetectionFramework and NATCompleteServer's
/// NatTypeDetectionServerFramework: the client connects to the server, attaches NatTypeDetectionClient and calls
/// DetectNATType; the server runs NatTypeDetectionServer started on three more addresses. The sample needs a server with
/// four IP addresses and a real router; here the server's extra sockets share 127.0.0.1, S1P2 is declared S1P1's host
/// and the others hosts of their own, and the client is behind a NatEmulator router of each kind, or none.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class NatTypeDetectionScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => NatSession.DefaultTimeout;

    public static TheoryData<PeerKind, PeerKind, string> ClientServerAndRouter
    {
        get
        {
            var data = new TheoryData<PeerKind, PeerKind, string>();
            foreach (var client in Enum.GetValues<PeerKind>())
            foreach (var server in Enum.GetValues<PeerKind>())
            {
                foreach (var router in Enum.GetNames<NatType>().Prepend("None"))
                    data.Add(client, server, router);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClientServerAndRouter))]
    public async Task A_client_is_told_the_kind_of_router_it_is_behind(PeerKind client, PeerKind server, string router)
    {
        NatType? routerType = router == "None" ? null : Enum.Parse<NatType>(router);
        await using var session = await StartAsync(server);
        var clientPeer = await session.AddClientAsync(client, routerType, Token);
        await clientPeer.Nat.AttachNatTypeDetectionClientAsync(Token);
        var (serverGuid, _) = await session.ConnectAsync(clientPeer, Token);

        await clientPeer.Nat.DetectNatTypeAsync(session.ServerAddress, Token);
        var result = await clientPeer.Inbox.WaitForPacketAsync(p => p.MessageId == (byte)MessageId.NatTypeDetectionResult, "ID_NAT_TYPE_DETECTION_RESULT", Timeout, Token);

        // NatTypeDetectionClient.cpp:72-80: the result, pushed from the server.
        var expected = routerType switch
        {
            null => 0, // NAT_TYPE_NONE
            NatType.FullCone => 1,
            NatType.AddressRestricted => 2,
            NatType.PortRestricted => 3,
            _ => 4, // NAT_TYPE_SYMMETRIC
        };
        Assert.Equal(expected, result.Data.Span[1]);
        // AllocatePacketUnified(sizeof(MessageID)+2): three bytes, the last uninitialised in RakNet.
        Assert.Equal(3, result.Data.Length);
        Assert.Equal(serverGuid, result.PeerGuid);
        Assert.Equal(session.ServerAddress, result.Address);
        // Every test message is consumed by the plugins.
        Assert.DoesNotContain(clientPeer.Inbox.Unconsumed, p => p.MessageId is (byte)MessageId.OutOfBandInternal or (byte)MessageId.NatTypeDetectionRequest);
        Assert.DoesNotContain(session.Server.Inbox.Unconsumed, p => p.MessageId == (byte)MessageId.NatTypeDetectionRequest);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_client_that_never_runs_the_test_is_asked_twice_then_told_symmetric(PeerKind client, PeerKind server)
    {
        await using var session = await StartAsync(server);
        // A client without the plugin behind a router that drops every test: it sends the request itself.
        var clientPeer = await session.AddClientAsync(client, NatType.Symmetric, Token);
        var (serverGuid, _) = await session.ConnectAsync(clientPeer, Token);
        // NatTypeDetectionClient.cpp:66-70: the ID, true as one bit, C2's port (here 4000), unaligned.
        await clientPeer.Driver.SendAsync(new SendOptions(serverGuid, new byte[] { (byte)MessageId.NatTypeDetectionRequest, 0x80 | (4000 >> 9), (4000 >> 1) & 0xFF, (4000 & 1) << 7 }, Reliability.Reliable), Token);

        var messages = await clientPeer.Inbox.WaitForPacketsAsync(
            p => p.MessageId is (byte)MessageId.NatTypeDetectionRequest or (byte)MessageId.NatTypeDetectionResult, 3,
            "two requests to test S3P4 and a result", Timeout, Token);

        // NatTypeDetectionServer.cpp:300-326: the request names S3P4 by the address string given to Startup and its port.
        var s3p4 = session.ServerDetectionSockets[2];
        byte[] request = [(byte)MessageId.NatTypeDetectionRequest, 0, 9, .. "127.0.0.1"u8, (byte)(s3p4.Port >> 8), (byte)s3p4.Port];
        Assert.Equal(request, messages[0].Data.ToArray());
        Assert.Equal(request, messages[1].Data.ToArray());
        Assert.Equal([(byte)MessageId.NatTypeDetectionResult, 4], messages[2].Data.ToArray());
    }

    private static async Task<NatSession> StartAsync(PeerKind server)
    {
        var session = await NatSession.StartAsync(server, Token);
        try
        {
            await session.Server.Nat.AttachNatTypeDetectionServerAsync(Token);
            // NATCompleteServer.cpp:98: Startup(GetLocalIP(1), GetLocalIP(2), GetLocalIP(3)), here all loopback.
            var sockets = await session.Server.Nat.StartNatTypeDetectionServerAsync("127.0.0.1", "127.0.0.1", "127.0.0.1", Token);
            Assert.Equal(4, sockets.Count);
            // S1P2 is bound on S1P1's address; the other three are the server's other addresses.
            session.Emulator.AddHost(session.ServerAddress, sockets[0]);
            session.ServerDetectionSockets = sockets;
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}
