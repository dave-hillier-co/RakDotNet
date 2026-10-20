using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A server with a public address and clients each behind a <see cref="NatRouter"/> of a <see cref="NatEmulator"/>, or
/// on the network directly. Clients reach the server at its own address; there is no <see cref="UdpProxy"/>, because
/// the routers are the network between them.
/// </summary>
public sealed class NatSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = InteropSession.DefaultTimeout;

    private readonly List<NatPeer> _clients = [];

    private NatSession(NatEmulator emulator, NatPeer server)
    {
        Emulator = emulator;
        Server = server;
    }

    public NatEmulator Emulator { get; }

    public NatPeer Server { get; }

    public IPEndPoint ServerAddress => Server.Started.BoundAddress;

    /// <summary>Where a NatTypeDetectionServer on the server bound S1P2, S2P3, S3P4 and S4P5, once a scenario starts one.</summary>
    public IReadOnlyList<IPEndPoint> ServerDetectionSockets { get; set; } = [];

    /// <summary>Skips unless the kind is available, then starts the server on the network directly.</summary>
    public static async Task<NatSession> StartAsync(PeerKind server, CancellationToken cancellationToken = default)
    {
        PeerMatrix.SkipUnlessAvailable(server);
        var emulator = new NatEmulator();
        try
        {
            var serverPeer = await NatPeer.StartAsync(server, "server", router: null, cancellationToken).ConfigureAwait(false);
            return new NatSession(emulator, serverPeer);
        }
        catch
        {
            await emulator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Starts a client of <paramref name="kind"/> behind a new router of <paramref name="routerType"/>, or on the network when null.</summary>
    public async Task<NatPeer> AddClientAsync(PeerKind kind, NatType? routerType, CancellationToken cancellationToken = default)
    {
        PeerMatrix.SkipUnlessAvailable(kind);
        var router = routerType is { } type ? Emulator.AddRouter(type) : null;
        var label = $"client {_clients.Count + 1}" + (router is null ? "" : $" behind {router}");
        var client = await NatPeer.StartAsync(kind, label, router, cancellationToken).ConfigureAwait(false);
        _clients.Add(client);
        return client;
    }

    /// <summary>Connects <paramref name="client"/> to the server and waits for both sides to see it.</summary>
    /// <returns>The server's GUID and the address the server sees the client at.</returns>
    public async Task<(ulong ServerGuid, IPEndPoint ClientAddress)> ConnectAsync(NatPeer client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", await client.Driver.ConnectAsync(new ConnectOptions(ServerAddress), cancellationToken).ConfigureAwait(false));
        var accepted = await client.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == Server.Started.PeerGuid,
            "ID_CONNECTION_REQUEST_ACCEPTED", DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var incoming = await Server.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == client.Started.PeerGuid,
            $"ID_NEW_INCOMING_CONNECTION from {client.Label}", DefaultTimeout, cancellationToken).ConfigureAwait(false);
        return (accepted.PeerGuid, incoming.Address);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var client in _clients)
                await client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Server.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await Emulator.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (TestContext.Current.TestOutputHelper is { } output)
        {
            Server.WriteDiagnostics(output);
            foreach (var client in _clients)
                client.WriteDiagnostics(output);
            foreach (var router in Emulator.Routers)
            {
                output.WriteLine($"--- {router} ---");
                foreach (var outcome in router.Records.GroupBy(r => (r.Outbound, r.Remote, r.Outcome)))
                    output.WriteLine($"{(outcome.Key.Outbound ? "out to" : "in from")} {outcome.Key.Remote}: {outcome.Key.Outcome} x{outcome.Count()}");
            }
        }
    }
}

/// <summary>One peer of a <see cref="NatSession"/>.</summary>
public sealed class NatPeer : IAsyncDisposable
{
    private NatPeer(string label, PeerKind kind, IPeerDriver driver, PeerStarted started, NatRouter? router)
    {
        Label = label;
        Kind = kind;
        Driver = driver;
        Started = started;
        Router = router;
        Inbox = new PacketInbox(driver.Packets, $"{label} ({driver.Kind})");
    }

    public string Label { get; }

    public PeerKind Kind { get; }

    public IPeerDriver Driver { get; }

    public INatDriver Nat => (INatDriver)Driver;

    public PeerStarted Started { get; }

    /// <summary>The router the peer is behind, or null when it is on the network directly.</summary>
    public NatRouter? Router { get; }

    public PacketInbox Inbox { get; }

    internal static async Task<NatPeer> StartAsync(PeerKind kind, string label, NatRouter? router, CancellationToken cancellationToken)
    {
        var driver = await PeerMatrix.LaunchAsync(kind, cancellationToken).ConfigureAwait(false);
        try
        {
            if (router is not null)
                await ((INatDriver)driver).SetNatGatewayAsync(router.GatewayEndPoint, cancellationToken).ConfigureAwait(false);
            var started = await driver.StartupAsync(new StartupOptions(), cancellationToken).ConfigureAwait(false);
            return new NatPeer(label, kind, driver, started, router);
        }
        catch
        {
            await driver.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Driver.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            Inbox.Dispose();
        }
    }

    public void WriteDiagnostics(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (Driver is NativePeerDriver native && native.StandardError.Length > 0)
        {
            output.WriteLine($"--- {Label} stderr ---");
            output.WriteLine(native.StandardError);
        }
        if (Driver is RakDotNetPeerDriver rakDotNet && rakDotNet.NatDebugMessages.Count > 0)
        {
            output.WriteLine($"--- {Label} NatPunchthroughClient ---");
            foreach (var message in rakDotNet.NatDebugMessages)
                output.WriteLine(message);
        }
    }
}
