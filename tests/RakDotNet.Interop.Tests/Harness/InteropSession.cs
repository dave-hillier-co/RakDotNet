using System.Net;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A server peer, one or more client peers and a <see cref="UdpProxy"/> between them. Clients always talk to
/// <see cref="ProxyEndPoint"/>, so every datagram is recorded and subject to the proxy's faults; the server reaches a
/// client at <see cref="AddressAtServer"/>, which goes through the proxy too.
/// </summary>
public sealed class InteropSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly List<InteropPeer> _extraClients = [];

    private InteropSession(InteropPeer server, InteropPeer client, UdpProxy proxy)
    {
        ServerPeer = server;
        ClientPeer = client;
        Proxy = proxy;
    }

    /// <summary>The server, its startup result and its packets.</summary>
    public InteropPeer ServerPeer { get; }

    /// <summary>The first client, its startup result and its packets.</summary>
    public InteropPeer ClientPeer { get; }

    public IPeerDriver Server => ServerPeer.Driver;

    public PeerStarted ServerStarted => ServerPeer.Started;

    public IPeerDriver Client => ClientPeer.Driver;

    public PeerStarted ClientStarted => ClientPeer.Started;

    public UdpProxy Proxy { get; }

    public PacketInbox ServerInbox => ServerPeer.Inbox;

    public PacketInbox ClientInbox => ClientPeer.Inbox;

    public IPEndPoint ProxyEndPoint => Proxy.ListenEndPoint;

    /// <summary>Skips unless both kinds are available, then starts server, proxy and client.</summary>
    /// <param name="client">The first client's kind.</param>
    /// <param name="server">The server's kind.</param>
    /// <param name="faults">The proxy's faults from the start; scenarios usually switch them on after connecting.</param>
    /// <param name="serverOptions">The server's startup options, or the defaults.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    public static async Task<InteropSession> StartAsync(
        PeerKind client,
        PeerKind server,
        FaultOptions? faults = null,
        StartupOptions? serverOptions = null,
        CancellationToken cancellationToken = default)
    {
        PeerMatrix.SkipUnlessAvailable(client, server);

        InteropPeer? serverPeer = null;
        InteropPeer? clientPeer = null;
        UdpProxy? proxy = null;
        try
        {
            serverPeer = await InteropPeer.StartAsync(server, "server", serverOptions ?? new StartupOptions(), cancellationToken).ConfigureAwait(false);
            proxy = UdpProxy.Start(serverPeer.Started.BoundAddress, faults);
            clientPeer = await InteropPeer.StartAsync(client, "client", new StartupOptions(), cancellationToken).ConfigureAwait(false);
            return new InteropSession(serverPeer, clientPeer, proxy);
        }
        catch
        {
            if (clientPeer is not null)
            {
                await clientPeer.DisposeAsync().ConfigureAwait(false);
            }
            if (proxy is not null)
            {
                await proxy.DisposeAsync().ConfigureAwait(false);
            }
            if (serverPeer is not null)
            {
                await serverPeer.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>Starts another client of <paramref name="kind"/>, owned and disposed by the session.</summary>
    public async Task<InteropPeer> AddClientAsync(PeerKind kind, CancellationToken cancellationToken = default)
    {
        PeerMatrix.SkipUnlessAvailable(kind);
        var peer = await InteropPeer.StartAsync(kind, $"client {_extraClients.Count + 2}", new StartupOptions(), cancellationToken).ConfigureAwait(false);
        _extraClients.Add(peer);
        return peer;
    }

    /// <summary>The address the server sees <paramref name="client"/> at, through the proxy.</summary>
    public IPEndPoint AddressAtServer(InteropPeer client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return Proxy.UpstreamEndPointFor(client.Started.BoundAddress);
    }

    /// <summary>
    /// Connects the client to the server through the proxy and waits for both sides to see the connection.
    /// </summary>
    /// <returns>The server's GUID as the client sees it and the client's GUID as the server sees it.</returns>
    public Task<(ulong ServerGuid, ulong ClientGuid)> ConnectAsync(CancellationToken cancellationToken = default) =>
        ConnectAsync(ClientPeer, cancellationToken);

    /// <summary>Connects <paramref name="client"/> to the server through the proxy and waits for both sides to see it.</summary>
    /// <returns>The server's GUID as the client sees it and the client's GUID as the server sees it.</returns>
    public async Task<(ulong ServerGuid, ulong ClientGuid)> ConnectAsync(InteropPeer client, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var result = await client.Driver.ConnectAsync(new ConnectOptions(ProxyEndPoint), cancellationToken).ConfigureAwait(false);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", result);

        var accepted = await client.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var incoming = await ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == client.Started.PeerGuid,
            $"ID_NEW_INCOMING_CONNECTION from {client.Label}",
            DefaultTimeout,
            cancellationToken).ConfigureAwait(false);

        Assert.Equal(ServerStarted.PeerGuid, accepted.PeerGuid);
        return (accepted.PeerGuid, incoming.PeerGuid);
    }

    public async ValueTask DisposeAsync()
    {
        // Each part is disposed even when an earlier one throws, so a failing client cannot leak the server or proxy.
        try
        {
            foreach (var extra in _extraClients)
            {
                await extra.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await ClientPeer.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await ServerPeer.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await Proxy.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        if (TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine($"Proxy recorded {Proxy.Datagrams.Count} datagrams.");
            ServerPeer.WriteDiagnostics(output);
            ClientPeer.WriteDiagnostics(output);
            foreach (var extra in _extraClients)
            {
                extra.WriteDiagnostics(output);
            }
        }
    }
}

/// <summary>One peer of an <see cref="InteropSession"/>: its driver, startup result and buffered packets.</summary>
public sealed class InteropPeer : IAsyncDisposable
{
    private InteropPeer(string label, PeerKind kind, IPeerDriver driver, PeerStarted started)
    {
        Label = label;
        Kind = kind;
        Driver = driver;
        Started = started;
        Inbox = new PacketInbox(driver.Packets, $"{label} ({driver.Kind})");
    }

    /// <summary>The peer's role in test output, e.g. "server" or "client 2".</summary>
    public string Label { get; }

    public PeerKind Kind { get; }

    public IPeerDriver Driver { get; }

    public PeerStarted Started { get; }

    public PacketInbox Inbox { get; }

    public static async Task<InteropPeer> StartAsync(PeerKind kind, string label, StartupOptions options, CancellationToken cancellationToken = default)
    {
        var driver = await PeerMatrix.LaunchAsync(kind, cancellationToken).ConfigureAwait(false);
        try
        {
            var started = await driver.StartupAsync(options, cancellationToken).ConfigureAwait(false);
            return new InteropPeer(label, kind, driver, started);
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
    }
}
