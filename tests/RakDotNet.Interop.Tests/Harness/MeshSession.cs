using RakDotNet.Protocol.Wire;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// Three or more peers that connect to each other directly, without a proxy, as peer-to-peer topologies need: a system
/// connects to addresses other systems report, which a proxy between two of them would not forward.
/// </summary>
public sealed class MeshSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = InteropSession.DefaultTimeout;

    private readonly List<InteropPeer> _peers;

    private MeshSession(List<InteropPeer> peers) => _peers = peers;

    /// <summary>The peers in start order, labelled "peer 0", "peer 1" and so on.</summary>
    public IReadOnlyList<InteropPeer> Peers => _peers;

    public InteropPeer this[int index] => _peers[index];

    /// <summary>Skips unless every kind is available, then starts a peer of each kind on 127.0.0.1.</summary>
    public static async Task<MeshSession> StartAsync(IReadOnlyList<PeerKind> kinds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        PeerMatrix.SkipUnlessAvailable([.. kinds]);
        var peers = new List<InteropPeer>();
        try
        {
            for (var i = 0; i < kinds.Count; i++)
            {
                peers.Add(await InteropPeer.StartAsync(kinds[i], $"peer {i}", new StartupOptions(), cancellationToken).ConfigureAwait(false));
            }
            return new MeshSession(peers);
        }
        catch
        {
            await DisposeAllAsync(peers).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Connects <paramref name="client"/> straight to <paramref name="server"/>'s bound address and waits for both sides to
    /// see the connection.
    /// </summary>
    public static async Task ConnectAsync(InteropPeer client, InteropPeer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(server);
        var result = await client.Driver.ConnectAsync(new ConnectOptions(server.Started.BoundAddress), cancellationToken).ConfigureAwait(false);
        Assert.Equal("CONNECTION_ATTEMPT_STARTED", result);
        await client.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.ConnectionRequestAccepted && p.PeerGuid == server.Started.PeerGuid,
            $"ID_CONNECTION_REQUEST_ACCEPTED from {server.Label}", DefaultTimeout, cancellationToken).ConfigureAwait(false);
        await server.Inbox.WaitForPacketAsync(
            p => p.MessageId == (byte)MessageId.NewIncomingConnection && p.PeerGuid == client.Started.PeerGuid,
            $"ID_NEW_INCOMING_CONNECTION from {client.Label}", DefaultTimeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Polls <paramref name="condition"/> until it holds, failing with <paramref name="description"/> after the timeout.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string description, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTime.UtcNow + DefaultTimeout;
        while (!await condition().ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not reached within {DefaultTimeout.TotalSeconds:0} s: {description}.");
            }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAllAsync(_peers).ConfigureAwait(false);
        if (TestContext.Current.TestOutputHelper is { } output)
        {
            foreach (var peer in _peers)
            {
                peer.WriteDiagnostics(output);
            }
        }
    }

    // Each peer is disposed even when an earlier one throws.
    private static async Task DisposeAllAsync(List<InteropPeer> peers)
    {
        List<Exception>? errors = null;
        foreach (var peer in peers)
        {
            try
            {
                await peer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (errors ??= []).Add(exception);
            }
        }
        if (errors is not null)
        {
            throw new AggregateException(errors);
        }
    }
}

/// <summary>
/// The matrix of a scenario with three or more peers: the first peer is of one kind and every other of the second, so
/// across the four combinations every pair of kinds talks in both roles, native-native first.
/// </summary>
public static class MeshMatrix
{
    public static TheoryData<PeerKind, PeerKind> FirstAndOthers => PeerMatrix.ClientServer;

    public static PeerKind[] Kinds(PeerKind first, PeerKind others, int count) =>
        [first, .. Enumerable.Repeat(others, count - 1)];
}
