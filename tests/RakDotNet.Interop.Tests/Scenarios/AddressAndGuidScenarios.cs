using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Addresses and GUIDs, from Samples/Tests/SystemAddressAndGuidTest.cpp: after connecting, the connection list, the
/// GUID and address lookups and GetExternalID must all agree on who is where. Through the proxy the server sees the
/// client at the proxy's upstream address and the client sees the server at the proxy.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class AddressAndGuidScenarios
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_handshake_tells_each_side_the_others_guid_and_the_address_it_is_seen_at(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var clientAtServer = session.AddressAtServer(session.ClientPeer);

        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), Token));
        var accepted = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", InteropSession.DefaultTimeout, Token);
        var incoming = await session.ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection, "ID_NEW_INCOMING_CONNECTION", InteropSession.DefaultTimeout, Token);

        Assert.NotEqual(session.ClientStarted.PeerGuid, session.ServerStarted.PeerGuid);
        Assert.DoesNotContain(RakNetConstants.UnassignedGuid, new[] { session.ClientStarted.PeerGuid, session.ServerStarted.PeerGuid });
        Assert.Equal(session.ServerStarted.PeerGuid, accepted.PeerGuid);
        Assert.Equal(session.ProxyEndPoint, accepted.Address);
        Assert.Equal(session.ClientStarted.PeerGuid, incoming.PeerGuid);
        Assert.Equal(clientAtServer, incoming.Address);
        // RakPeer.cpp:3540-3554 tells the client its address as the server sees it, which GetExternalID returns
        // (SystemAddressAndGuidTest.cpp:161-167); RakPeer.cpp:6185-6200 tells the server its address as the client sees it.
        Assert.Equal(clientAtServer, ScenarioFlows.Decode<ConnectionRequestAccepted>(accepted.Data).ClientAddress);
        Assert.Equal(session.ProxyEndPoint, ScenarioFlows.Decode<NewIncomingConnection>(incoming.Data).ServerAddress);
        Assert.Equal("IS_CONNECTED", await session.Client.GetConnectionStateAsync(session.ServerStarted.PeerGuid, Token));
        Assert.Equal("IS_CONNECTED", await session.Server.GetConnectionStateAsync(session.ClientStarted.PeerGuid, Token));
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task The_connection_list_reports_each_remote_guid_address_mtu_and_our_external_address(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var clientAtServer = session.AddressAtServer(session.ClientPeer);
        await session.ConnectAsync(Token);

        var clientInfo = await session.Client.GetInfoAsync(Token);
        var serverInfo = await session.Server.GetInfoAsync(Token);

        // SystemAddressAndGuidTest.cpp:95-167: one connection each way, the same GUID and address from every lookup, and
        // GetExternalID giving the address the other side sees.
        Assert.Equal(session.ClientStarted.PeerGuid, clientInfo.PeerGuid);
        Assert.Equal(session.ClientStarted.BoundAddress, clientInfo.BoundAddress);
        Assert.Equal(
            new ConnectionInfo(session.ServerStarted.PeerGuid, session.ProxyEndPoint, ScenarioFlows.LoopbackMtu, 0, 0, 0, clientAtServer),
            Assert.Single(clientInfo.Connections) with { AveragePing = 0, LastPing = 0, LowestPing = 0 });
        Assert.Equal(
            new ConnectionInfo(session.ClientStarted.PeerGuid, clientAtServer, ScenarioFlows.LoopbackMtu, 0, 0, 0, session.ProxyEndPoint),
            Assert.Single(serverInfo.Connections) with { AveragePing = 0, LastPing = 0, LowestPing = 0 });
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_banned_address_is_refused_with_connection_banned(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);

        await AddressFlows.ConnectWhileBannedAsync(session, Token);
    }
}

/// <summary>The steps and assertions of the address scenarios, shared with the golden vector capture.</summary>
public static class AddressFlows
{
    /// <summary>
    /// The server bans 127.0.0.1, where the proxy's upstream addresses are, and the client's attempt fails with
    /// ID_CONNECTION_BANNED (RakPeer.cpp:4540-4565 answers any offline message from a banned address with it).
    /// </summary>
    public static async Task ConnectWhileBannedAsync(InteropSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.Server.BanAsync("127.0.0.1", TimeSpan.Zero, cancellationToken);

        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await session.Client.ConnectAsync(new Harness.ConnectOptions(session.ProxyEndPoint), cancellationToken));

        var banned = await session.ClientInbox.WaitForPacketAsync(
            p => p.MessageId is (byte)MessageId.ConnectionBanned or (byte)MessageId.ConnectionRequestAccepted,
            "ID_CONNECTION_BANNED",
            InteropSession.DefaultTimeout,
            cancellationToken);
        Assert.Equal("ID_CONNECTION_BANNED", banned.MessageName);
        Assert.Equal(session.ServerStarted.PeerGuid, banned.PeerGuid);
        Assert.Equal(session.ProxyEndPoint, banned.Address);
    }
}
