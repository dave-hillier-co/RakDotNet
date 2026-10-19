using System.Diagnostics;
using RakDotNet.Protocol.Wire;
using RakDotNet.Samples.Client;
using RakDotNet.Samples.ManualUpdate;
using RakDotNet.Samples.Server;

namespace RakDotNet.Samples.Tests;

// The samples run end to end over loopback UDP: the server sample serves the client sample and the manual-update
// sample, each writing what it does to a log the test reads.
public sealed class SamplesTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_client_sample_moves_its_player_on_the_server_sample()
    {
        using var timeout = new CancellationTokenSource(Patience);
        using var serverLog = new Log();
        await using var server = GameServer.Start(port: 0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var serving = GameServer.RunAsync(server, serverLog.Writer, stop.Token);

        using var clientLog = new Log();
        await GameClient.RunAsync("localhost", server.Sockets[0].LocalAddress.Port, clientLog.Writer, timeout.Token);

        Assert.Equal(["Connected to 127.0.0.1:" + server.Sockets[0].LocalAddress.Port, "Send: Queued", "Closed"], clientLog.Lines);
        await serverLog.WaitFor(line => line.EndsWith("left: DisconnectionNotification", StringComparison.Ordinal), timeout.Token);
        var lines = serverLog.Lines;
        Assert.Equal(3, lines.Length);
        Assert.EndsWith(" connected", lines[0], StringComparison.Ordinal);
        Assert.EndsWith(" moved to 1.5, -2", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(" left: DisconnectionNotification", lines[2], StringComparison.Ordinal);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    [Fact]
    public async Task The_manual_update_sample_sees_its_connection_on_its_own_thread()
    {
        using var timeout = new CancellationTokenSource(Patience);
        await using var server = GameServer.Start(port: 0);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var serving = GameServer.RunAsync(server, TextWriter.Null, stop.Token);
        using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 8, Threading = PeerThreading.Manual });
        var serverAddress = new SystemAddress(System.Net.IPAddress.Loopback, server.Sockets[0].LocalAddress.Port);
        Assert.Equal(ConnectAttemptResult.Started, peer.Connect(serverAddress));

        using var log = new Log();
        var clock = Stopwatch.StartNew();
        while (!log.Lines.Contains($"Connected from {serverAddress}") && clock.Elapsed < Patience)
            GameLoop.Run(peer, frames: 10, frame: TimeSpan.FromMilliseconds(1), log.Writer);

        Assert.Contains($"Connected from {serverAddress}", log.Lines);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);
    }

    /// <summary>A thread-safe log that a test can wait on.</summary>
    private sealed class Log : IDisposable
    {
        private readonly StringWriter _text = new();

        public Log() => Writer = TextWriter.Synchronized(_text);

        public TextWriter Writer { get; }

        public string[] Lines
        {
            get
            {
                lock (Writer)
                    return _text.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public void Dispose() => _text.Dispose();

        public async Task WaitFor(Func<string, bool> line, CancellationToken cancellationToken)
        {
            while (!Lines.Any(line))
                await Task.Delay(5, cancellationToken);
        }
    }
}
