using System.Net;

namespace RakDotNet.Interop.Tests.Harness;

/// <summary>
/// A driver used for its TCP interface, with the reports its polling collected so far. Scenarios wait on them in any
/// order: a report that does not match a wait stays for later waits. Disposing stops the interface and the driver.
/// </summary>
public sealed class TcpPeer : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    private readonly List<TcpEvent> _unconsumed = [];

    private TcpPeer(IPeerDriver driver, string label)
    {
        Driver = driver;
        Tcp = (ITcpDriver)driver;
        Label = label;
    }

    public IPeerDriver Driver { get; }

    public ITcpDriver Tcp { get; }

    public string Label { get; }

    /// <summary>The listening port once <see cref="ListenAsync"/> has run.</summary>
    public int Port { get; private set; }

    public IPEndPoint EndPoint => new(IPAddress.Loopback, Port);

    /// <summary>Skips unless the kind is available, then launches its driver.</summary>
    public static async Task<TcpPeer> LaunchAsync(PeerKind kind, string role, CancellationToken cancellationToken = default) =>
        new(await PeerMatrix.LaunchAsync(kind, cancellationToken).ConfigureAwait(false), $"{role} ({kind})");

    /// <summary>tcpStart with incoming connections on 127.0.0.1 and an ephemeral port.</summary>
    public async Task ListenAsync(bool packetized = false, int maxIncoming = 4, CancellationToken cancellationToken = default)
    {
        var started = await Tcp.StartTcpAsync(new TcpStartOptions(packetized, 0, maxIncoming), cancellationToken).ConfigureAwait(false);
        Assert.True(started.Started, $"{Label} did not start");
        Assert.NotEqual(0, started.Port);
        Port = started.Port;
    }

    /// <summary>tcpStart without incoming connections, which gives one slot unless <paramref name="maxConnections"/> says more.</summary>
    public async Task StartClientAsync(bool packetized = false, int maxConnections = 0, CancellationToken cancellationToken = default)
    {
        var started = await Tcp.StartTcpAsync(new TcpStartOptions(packetized, 0, 0, maxConnections, Host: null), cancellationToken).ConfigureAwait(false);
        Assert.True(started.Started, $"{Label} did not start");
    }

    /// <summary>Waits for the first unconsumed report that matches and consumes it.</summary>
    public async Task<TcpEvent> WaitForAsync(Func<TcpEvent, bool> predicate, string expectation, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var index = _unconsumed.FindIndex(e => predicate(e));
            if (index >= 0)
            {
                var found = _unconsumed[index];
                _unconsumed.RemoveAt(index);
                return found;
            }
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{Label}: timed out waiting for {expectation}. Unconsumed: {Describe(_unconsumed)}");
            await CollectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<TcpEvent> WaitForKindAsync(string kind, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        WaitForAsync(e => e.Kind == kind, kind, timeout, cancellationToken);

    /// <summary>
    /// Collects the data of packets from <paramref name="from"/>, consuming them, until <paramref name="done"/> holds for
    /// what was collected; TCP may split or join what was sent.
    /// </summary>
    public async Task<byte[]> ReceiveBytesAsync(IPEndPoint from, Func<byte[], bool> done, string expectation, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var received = new List<byte>();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            for (var i = 0; i < _unconsumed.Count;)
            {
                if (_unconsumed[i].Kind == "packet" && Equals(_unconsumed[i].Address, from))
                {
                    received.AddRange(_unconsumed[i].Data);
                    _unconsumed.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            if (done([.. received]))
                return [.. received];
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{Label}: timed out waiting for {expectation}; {received.Count} bytes arrived. Unconsumed: {Describe(_unconsumed)}");
            await CollectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the connection once everything sent to it has left the outgoing buffer: CloseConnection empties the buffer
    /// (TCPInterface.cpp:248-253, 1266-1278), so data still in it would never be sent.
    /// </summary>
    public async Task CloseWhenSentAsync(IPEndPoint address, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while ((await Tcp.GetTcpInfoAsync(address, cancellationToken).ConfigureAwait(false)).OutgoingBytes > 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{Label}: the data sent to {address} did not leave the outgoing buffer.");
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
        await Tcp.TcpCloseAsync(address, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Collects for <paramref name="duration"/> and returns everything unconsumed, without consuming it.</summary>
    public async Task<IReadOnlyList<TcpEvent>> SettleAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline)
            await CollectAsync(cancellationToken).ConfigureAwait(false);
        return [.. _unconsumed];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Tcp.StopTcpAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PeerCommandException or PeerProcessExitedException or TimeoutException)
        {
            // Never started, or the process is gone.
        }
        await Driver.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CollectAsync(CancellationToken cancellationToken)
    {
        _unconsumed.AddRange(await Tcp.PollTcpAsync(cancellationToken).ConfigureAwait(false));
        await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(IEnumerable<TcpEvent> events) =>
        string.Join(", ", events.Select(e => e.Kind == "packet" ? $"packet {e.Address} {e.Data.Length} bytes" : $"{e.Kind} {e.Address}"));
}
