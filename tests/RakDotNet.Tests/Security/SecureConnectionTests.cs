using System.Net;
using RakDotNet.Protocol.Wire;
using RakDotNet.Security;

namespace RakDotNet.Tests.Security;

// RakNet's secure connection through the public API: InitializeSecurity as RakPeerOptions.Security, Connect's PublicKey
// as ConnectOptions.Security, two real peers on loopback.
public sealed class SecureConnectionTests : IDisposable
{
    private readonly RakKeyPair _serverKeys = RakKeyPair.Generate();

    public void Dispose() => _serverKeys.Dispose();

    [Fact]
    public void A_client_that_knows_the_servers_key_connects_securely_and_exchanges_messages()
    {
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) });

        var server = Connect(peers, ConnectSecurity.KnownServerKey(_serverKeys.PublicKey));
        server.Send(new byte[] { (byte)MessageId.UserPacketEnum, 1, 2, 3 }, Reliability.ReliableOrdered);
        peers.RunUntil(() => peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Message), "message");

        Assert.True(server.IsSecure);
        Assert.True(peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!.IsSecure);
        Assert.Equal(new byte[] { (byte)MessageId.UserPacketEnum, 1, 2, 3 }, peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Message).Data);
        Assert.True(peers.Server.IsSecurityEnabled);
        // GetMTUSize reports the negotiated MTU; the encryption overhead comes off inside the reliability layer.
        Assert.Equal(1492, server.Mtu);
    }

    [Fact]
    public void A_client_accepting_any_key_connects_securely()
    {
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) });

        Assert.True(Connect(peers, ConnectSecurity.AcceptAnyPublicKey).IsSecure);
    }

    [Fact]
    public void An_insecure_connection_is_not_secure_and_has_no_identity_key()
    {
        using var peers = new ManualPeers();

        var server = peers.Connect();

        Assert.False(server.IsSecure);
        Assert.Null(server.RemoteIdentityKey);
        Assert.False(peers.Server.IsSecurityEnabled);
    }

    [Fact]
    public void Two_way_authentication_gives_the_server_the_clients_identity_key()
    {
        using var identity = RakKeyPair.Generate();
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) { RequireClientKey = true } });

        var server = Connect(peers, ConnectSecurity.TwoWayAuthentication(_serverKeys.PublicKey, identity));

        var client = peers.ServerEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;
        Assert.Equal(identity.PublicKey, client.RemoteIdentityKey);
        Assert.Null(server.RemoteIdentityKey);
    }

    [Fact]
    public void A_wrong_server_key_is_reported_as_a_public_key_mismatch()
    {
        using var other = RakKeyPair.Generate();
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) });

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress, new ConnectOptions { Security = ConnectSecurity.KnownServerKey(other.PublicKey) }));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ConnectFailed), "failed");

        var failed = peers.ClientEvents.First(e => e.Kind == PeerEventKind.ConnectFailed);
        Assert.Equal(ConnectFailure.PublicKeyMismatch, failed.ConnectFailure);
        Assert.Equal(new[] { (byte)MessageId.PublicKeyMismatch }, failed.Data);
        Assert.Equal(peers.Server.RakNetGuid, failed.RakNetGuid);
    }

    [Fact]
    public void An_insecure_client_is_refused_by_a_secure_server()
    {
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) });

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ConnectFailed), "failed");

        Assert.Equal(ConnectFailure.PublicKeyMismatch, peers.ClientEvents.First(e => e.Kind == PeerEventKind.ConnectFailed).ConnectFailure);
    }

    [Fact]
    public void A_secure_client_is_told_an_insecure_server_lacks_security()
    {
        using var peers = new ManualPeers();

        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress, new ConnectOptions { Security = ConnectSecurity.KnownServerKey(_serverKeys.PublicKey) }));
        peers.RunUntil(() => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.ConnectFailed), "failed");

        Assert.Equal(ConnectFailure.OurSystemRequiresSecurity, peers.ClientEvents.First(e => e.Kind == PeerEventKind.ConnectFailed).ConnectFailure);
    }

    [Fact]
    public async Task ConnectAsync_completes_with_the_security_failure()
    {
        using var other = RakKeyPair.Generate();
        using var peers = new ManualPeers(server: o => o with { Security = new ServerSecurity(_serverKeys) });

        var connecting = peers.Client.ConnectAsync(peers.ServerAddress, new ConnectOptions { Security = ConnectSecurity.KnownServerKey(other.PublicKey) }, TestContext.Current.CancellationToken);
        peers.RunUntil(() => connecting.IsCompleted, "completed");

        var result = await connecting;
        Assert.Equal(ConnectFailure.PublicKeyMismatch, result.Failure);
    }

    [Fact]
    public void Connecting_with_a_disposed_identity_is_a_programming_error()
    {
        var identity = RakKeyPair.Generate();
        identity.Dispose();
        using var peers = new ManualPeers();

        Assert.Throws<ObjectDisposedException>(() =>
            peers.Client.Connect(peers.ServerAddress, new ConnectOptions { Security = ConnectSecurity.TwoWayAuthentication(_serverKeys.PublicKey, identity) }));
    }

    [Fact]
    public void Starting_with_a_disposed_key_pair_is_a_programming_error()
    {
        var keys = RakKeyPair.Generate();
        keys.Dispose();

        Assert.Throws<ObjectDisposedException>(() => RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 1,
            Bindings = [ManualPeers.Loopback],
            Threading = PeerThreading.Manual,
            Security = new ServerSecurity(keys),
        }));
    }

    private static RakConnection Connect(ManualPeers peers, ConnectSecurity security)
    {
        Assert.Equal(ConnectAttemptResult.Started, peers.Client.Connect(peers.ServerAddress, new ConnectOptions { Security = security }));
        peers.RunUntil(
            () => peers.ClientEvents.Any(e => e.Kind == PeerEventKind.Connected) && peers.ServerEvents.Any(e => e.Kind == PeerEventKind.Connected),
            "connected");
        return peers.ClientEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;
    }
}

public sealed class SecurityExceptionListTests
{
    [Fact]
    public void Starts_with_the_options_list_and_reports_what_it_holds()
    {
        using var peer = Start(["127.0.0.1", "10.0.*"]);

        Assert.Equal(["127.0.0.1", "10.0.*"], peer.SecurityExceptions);
        Assert.Equal(2, peer.SecurityExceptions.Count);
        Assert.True(peer.SecurityExceptions.Contains(IPAddress.Loopback));
        Assert.True(peer.SecurityExceptions.Contains("10.0.200.3"));
        Assert.False(peer.SecurityExceptions.Contains("10.1.0.1"));
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.*", "127.0.0.200", true)]
    [InlineData("127.*", "127.255.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.12", false)]
    [InlineData("127.0.0.12", "127.0.0.1", false)]
    [InlineData("*", "1.2.3.4", true)]
    [InlineData("", "1.2.3.4", false)]
    [InlineData("1.2.3.4", "", false)]
    [InlineData("1234567890123456*", "1234567890123456", false)] // RakString.cpp:756: an address over 15 characters never matches.
    public void Patterns_match_as_RakString_IPAddressMatch_compares_them(string pattern, string address, bool matches)
    {
        using var peer = Start([]);
        peer.SecurityExceptions.Add(pattern);

        Assert.Equal(matches, peer.SecurityExceptions.Contains(address));
    }

    [Fact]
    public void Remove_takes_out_every_pattern_the_address_matches()
    {
        // RakPeer.cpp:868-881: RemoveFromSecurityExceptionList(ip) removes each entry whose pattern matches ip.
        using var peer = Start(["10.0.0.*", "10.0.0.5", "10.0.1.1"]);

        Assert.True(peer.SecurityExceptions.Remove("10.0.0.5"));

        Assert.Equal(["10.0.1.1"], peer.SecurityExceptions);
        Assert.False(peer.SecurityExceptions.Remove("10.0.0.5"));
    }

    [Fact]
    public void A_pattern_can_be_listed_twice_and_Clear_empties_the_list()
    {
        using var peer = Start([]);
        peer.SecurityExceptions.Add("1.2.3.4");
        peer.SecurityExceptions.Add("1.2.3.4");

        Assert.Equal(2, peer.SecurityExceptions.Count);
        peer.SecurityExceptions.Clear();
        Assert.Empty(peer.SecurityExceptions);
    }

    [Fact]
    public void Changes_after_shutdown_throw()
    {
        var peer = Start([]);
        peer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => peer.SecurityExceptions.Add("1.2.3.4"));
        Assert.False(peer.SecurityExceptions.Contains("1.2.3.4"));
    }

    private static RakPeer Start(IReadOnlyList<string> exceptions) => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 1,
        Bindings = [ManualPeers.Loopback],
        Threading = PeerThreading.Manual,
        SecurityExceptions = exceptions,
    });
}
