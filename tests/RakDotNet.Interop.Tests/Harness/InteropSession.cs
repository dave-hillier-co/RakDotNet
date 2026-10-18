using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A server peer, a client peer and a <see cref="UdpProxy"/> between them. The client always talks to
/// <see cref="ProxyEndPoint"/>, so every datagram is recorded and subject to the proxy's faults.
/// </summary>
public sealed class InteropSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private InteropSession(IPeerDriver server, PeerStarted serverStarted, IPeerDriver client, PeerStarted clientStarted, UdpProxy proxy)
    {
        Server = server;
        ServerStarted = serverStarted;
        Client = client;
        ClientStarted = clientStarted;
        Proxy = proxy;
        ServerInbox = new PacketInbox(server.Packets, $"server ({server.Kind})");
        ClientInbox = new PacketInbox(client.Packets, $"client ({client.Kind})");
    }

    public IPeerDriver Server { get; }

    public PeerStarted ServerStarted { get; }

    public IPeerDriver Client { get; }

    public PeerStarted ClientStarted { get; }

    public UdpProxy Proxy { get; }

    public PacketInbox ServerInbox { get; }

    public PacketInbox ClientInbox { get; }

    public System.Net.IPEndPoint ProxyEndPoint => Proxy.ListenEndPoint;

    /// <summary>Skips unless both kinds are available, then starts server, proxy and client.</summary>
    public static async Task<InteropSession> StartAsync(PeerKind client, PeerKind server, FaultOptions? faults = null, CancellationToken cancellationToken = default)
    {
        PeerMatrix.SkipUnlessAvailable(client, server);

        IPeerDriver? serverDriver = null;
        IPeerDriver? clientDriver = null;
        UdpProxy? proxy = null;
        try
        {
            serverDriver = await PeerMatrix.LaunchAsync(server, cancellationToken).ConfigureAwait(false);
            var serverStarted = await serverDriver.StartupAsync(new StartupOptions(), cancellationToken).ConfigureAwait(false);
            proxy = UdpProxy.Start(serverStarted.BoundAddress, faults);
            clientDriver = await PeerMatrix.LaunchAsync(client, cancellationToken).ConfigureAwait(false);
            var clientStarted = await clientDriver.StartupAsync(new StartupOptions(), cancellationToken).ConfigureAwait(false);
            return new InteropSession(serverDriver, serverStarted, clientDriver, clientStarted, proxy);
        }
        catch
        {
            if (clientDriver is not null)
            {
                await clientDriver.DisposeAsync().ConfigureAwait(false);
            }
            if (proxy is not null)
            {
                await proxy.DisposeAsync().ConfigureAwait(false);
            }
            if (serverDriver is not null)
            {
                await serverDriver.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Connects the client to the server through the proxy and waits for both sides to see the connection.
    /// </summary>
    /// <returns>The server's GUID as the client sees it and the client's GUID as the server sees it.</returns>
    public async Task<(ulong ServerGuid, ulong ClientGuid)> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var result = await Client.ConnectAsync(new ConnectOptions(ProxyEndPoint), cancellationToken).ConfigureAwait(false);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", result);

        var accepted = await ClientInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted, "ID_CONNECTION_REQUEST_ACCEPTED", DefaultTimeout, cancellationToken).ConfigureAwait(false);
        var incoming = await ServerInbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection, "ID_NEW_INCOMING_CONNECTION", DefaultTimeout, cancellationToken).ConfigureAwait(false);

        Assert.Equal(ServerStarted.PeerGuid, accepted.PeerGuid);
        Assert.Equal(ClientStarted.PeerGuid, incoming.PeerGuid);
        return (accepted.PeerGuid, incoming.PeerGuid);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        await Server.DisposeAsync().ConfigureAwait(false);
        await Proxy.DisposeAsync().ConfigureAwait(false);
        ClientInbox.Dispose();
        ServerInbox.Dispose();

        if (TestContext.Current.TestOutputHelper is { } output)
        {
            output.WriteLine($"Proxy recorded {Proxy.Datagrams.Count} datagrams.");
            WriteDiagnostics(output, "server", Server);
            WriteDiagnostics(output, "client", Client);
        }
    }

    private static void WriteDiagnostics(ITestOutputHelper output, string role, IPeerDriver driver)
    {
        if (driver is NativePeerDriver native && native.StandardError.Length > 0)
        {
            output.WriteLine($"--- {role} stderr ---");
            output.WriteLine(native.StandardError);
        }
    }
}
