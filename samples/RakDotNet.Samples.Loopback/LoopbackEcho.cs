using System.Net;
using System.Text;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Samples.Loopback;

/// <summary>
/// A server and a client in one process over UDP on 127.0.0.1: the client connects and sends a text message, the
/// server echoes it back, and the client reads the echo. CI publishes this sample with NativeAOT and runs it.
/// </summary>
public static class LoopbackEcho
{
    private static readonly SocketBinding Loopback = new(new IPEndPoint(IPAddress.Loopback, 0));

    /// <summary>Returns true once the client has read the echo of <paramref name="text"/>.</summary>
    public static async Task<bool> RunAsync(string text, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        await using var server = RakPeer.Start(new RakPeerOptions { MaxConnections = 1, MaxIncomingConnections = 1, Bindings = [Loopback] });
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1, Bindings = [Loopback] });
        log.WriteLine($"Server listening on {server.Sockets[0].LocalAddress}");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var serving = EchoAsync(server, log, stop.Token);

        var connected = await client.ConnectAsync(server.Sockets[0].LocalAddress, cancellationToken: cancellationToken);
        var toServer = connected.EnsureConnected();
        log.WriteLine($"Client connected to {toServer.Address}");

        toServer.Send(Encode(text), Reliability.ReliableOrdered);
        var echo = await ReadMessageAsync(client, cancellationToken);
        log.WriteLine($"Client received: {echo}");

        await stop.CancelAsync();
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
        }
        return echo == text;
    }

    // Sends every message straight back to the connection it came from.
    private static async Task EchoAsync(RakPeer server, TextWriter log, CancellationToken cancellationToken)
    {
        while (await server.Events.WaitToReadAsync(cancellationToken))
        {
            while (server.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Message && e.Id == MessageId.UserPacketEnum)
                {
                    log.WriteLine($"Server received: {Decode(e.Body)}");
                    e.Connection!.Send(e.Data, Reliability.ReliableOrdered);
                }
            }
        }
    }

    private static async Task<string> ReadMessageAsync(RakPeer peer, CancellationToken cancellationToken)
    {
        while (await peer.Events.WaitToReadAsync(cancellationToken))
        {
            while (peer.Events.TryRead(out var e))
            {
                if (e.Kind == PeerEventKind.Message && e.Id == MessageId.UserPacketEnum)
                    return Decode(e.Body);
            }
        }
        throw new InvalidOperationException("The client shut down before the echo arrived.");
    }

    private static byte[] Encode(string text) => [(byte)MessageId.UserPacketEnum, .. Encoding.UTF8.GetBytes(text)];

    private static string Decode(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(body);
}
