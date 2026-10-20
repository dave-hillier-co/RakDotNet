using System.Text;
using RakDotNet.Interop.Tests.Harness;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// TwoWayAuthentication on both systems, from Samples/TwoWayAuthentication/TwoWayAuthenticationTest.cpp: instance 0 (the
/// server here) holds PWD0 and PWD1, instance 1 (the client) only PWD0. A challenge for a shared password succeeds on both
/// systems, in either direction and again; one for a password the other system lacks fails on both. The negotiation
/// (nonce request, nonce and request ID, SHA-1 of nonce and password) crosses the wire, so every result proves the two
/// implementations agree on it.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class TwoWayAuthenticationScenarios
{
    private const byte IncomingSuccess = (byte)MessageId.TwoWayAuthenticationIncomingChallengeSuccess;
    private const byte OutgoingSuccess = (byte)MessageId.TwoWayAuthenticationOutgoingChallengeSuccess;
    private const byte IncomingFailure = (byte)MessageId.TwoWayAuthenticationIncomingChallengeFailure;
    private const byte OutgoingFailure = (byte)MessageId.TwoWayAuthenticationOutgoingChallengeFailure;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TimeSpan Timeout => InteropSession.DefaultTimeout;

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task Challenges_for_a_shared_password_succeed_on_both_systems_in_either_direction(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (clientAuth, serverAuth) = await AttachWithSamplePasswordsAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // Stage 0: instance 0 challenges instance 1.
        Assert.True(await serverAuth.ChallengeAsync("PWD0", clientGuid, Token));
        await AssertSucceededAsync(challenger: session.ServerInbox, challenged: session.ClientInbox, challengerGuid: serverGuid, challengedGuid: clientGuid, "PWD0");

        // Stages 1 and 2: instance 1 challenges instance 0, then again.
        Assert.True(await clientAuth.ChallengeAsync("PWD0", serverGuid, Token));
        await AssertSucceededAsync(challenger: session.ClientInbox, challenged: session.ServerInbox, challengerGuid: clientGuid, challengedGuid: serverGuid, "PWD0");
        Assert.True(await clientAuth.ChallengeAsync("PWD0", serverGuid, Token));
        await AssertSucceededAsync(challenger: session.ClientInbox, challenged: session.ServerInbox, challengerGuid: clientGuid, challengedGuid: serverGuid, "PWD0");

        // The negotiation itself never reaches the application (TwoWayAuthentication.cpp:180-200).
        Assert.DoesNotContain(session.ServerInbox.Unconsumed.Concat(session.ClientInbox.Unconsumed), p => p.MessageId == (byte)MessageId.TwoWayAuthenticationNegotiation);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_challenge_for_a_password_the_other_system_lacks_fails_on_both(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var (_, serverAuth) = await AttachWithSamplePasswordsAsync(session);
        var (serverGuid, clientGuid) = await session.ConnectAsync(Token);

        // Stage 3: an identifier never added cannot be challenged with.
        Assert.False(await serverAuth.ChallengeAsync("PWD3", clientGuid, Token));

        // Stage 4: "should fail twice".
        Assert.True(await serverAuth.ChallengeAsync("PWD1", clientGuid, Token));
        var outgoing = await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == OutgoingFailure, "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_FAILURE", Timeout, Token);
        Assert.Equal([OutgoingFailure, .. RakString("PWD1")], outgoing.Data.ToArray());
        Assert.Equal(clientGuid, outgoing.PeerGuid);
        // TwoWayAuthentication.cpp:374-386: the challenger's hashed-nonce message, its first byte rewritten.
        var incoming = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == IncomingFailure, "ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_FAILURE", Timeout, Token);
        var data = incoming.Data.ToArray();
        Assert.Equal(1 + 1 + 2 + 6 + 20, data.Length);
        Assert.Equal(2, data[1]);
        Assert.Equal(RakString("PWD1"), data[4..^20]);
        Assert.Equal(serverGuid, incoming.PeerGuid);
    }

    [Theory]
    [MemberData(nameof(PeerMatrix.ClientServer), MemberType = typeof(PeerMatrix))]
    public async Task A_challenge_with_a_different_password_fails_on_both(PeerKind client, PeerKind server)
    {
        await using var session = await InteropSession.StartAsync(client, server, cancellationToken: Token);
        var clientAuth = (ITwoWayAuthenticationDriver)session.Client;
        var serverAuth = (ITwoWayAuthenticationDriver)session.Server;
        await clientAuth.AttachTwoWayAuthenticationAsync(Token);
        await serverAuth.AttachTwoWayAuthenticationAsync(Token);
        Assert.True(await clientAuth.AddPasswordAsync("PWD0", "Password0", Token));
        Assert.True(await serverAuth.AddPasswordAsync("PWD0", "NotPassword0", Token));
        var (serverGuid, _) = await session.ConnectAsync(Token);

        Assert.True(await clientAuth.ChallengeAsync("PWD0", serverGuid, Token));

        var outgoing = await session.ClientInbox.WaitForPacketAsync(p => p.MessageId == OutgoingFailure, "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_FAILURE", Timeout, Token);
        Assert.Equal([OutgoingFailure, .. RakString("PWD0")], outgoing.Data.ToArray());
        await session.ServerInbox.WaitForPacketAsync(p => p.MessageId == IncomingFailure, "ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_FAILURE", Timeout, Token);
        Assert.DoesNotContain(session.ClientInbox.Unconsumed.Concat(session.ServerInbox.Unconsumed), p => p.MessageId is IncomingSuccess or OutgoingSuccess);
    }

    // TwoWayAuthenticationTest.cpp: instance 0 adds PWD0 and PWD1, instance 1 adds PWD0.
    private static async Task<(ITwoWayAuthenticationDriver Client, ITwoWayAuthenticationDriver Server)> AttachWithSamplePasswordsAsync(InteropSession session)
    {
        var client = (ITwoWayAuthenticationDriver)session.Client;
        var server = (ITwoWayAuthenticationDriver)session.Server;
        await client.AttachTwoWayAuthenticationAsync(Token);
        await server.AttachTwoWayAuthenticationAsync(Token);
        Assert.True(await server.AddPasswordAsync("PWD0", "Password0", Token));
        Assert.True(await server.AddPasswordAsync("PWD1", "Password1", Token));
        Assert.True(await client.AddPasswordAsync("PWD0", "Password0", Token));
        return (client, server);
    }

    // PushToUser (TwoWayAuthentication.cpp:262-275): the result ID, then the identifier as a RakString, from the other system.
    private static async Task AssertSucceededAsync(PacketInbox challenger, PacketInbox challenged, ulong challengerGuid, ulong challengedGuid, string identifier)
    {
        var outgoing = await challenger.WaitForPacketAsync(p => p.MessageId == OutgoingSuccess, "ID_TWO_WAY_AUTHENTICATION_OUTGOING_CHALLENGE_SUCCESS", Timeout, Token);
        Assert.Equal([OutgoingSuccess, .. RakString(identifier)], outgoing.Data.ToArray());
        Assert.Equal(challengedGuid, outgoing.PeerGuid);
        var incoming = await challenged.WaitForPacketAsync(p => p.MessageId == IncomingSuccess, "ID_TWO_WAY_AUTHENTICATION_INCOMING_CHALLENGE_SUCCESS", Timeout, Token);
        Assert.Equal([IncomingSuccess, .. RakString(identifier)], incoming.Data.ToArray());
        Assert.Equal(challengerGuid, incoming.PeerGuid);
    }

    // RakString::Serialize: a big-endian u16 length, then the bytes.
    private static byte[] RakString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [(byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }
}
