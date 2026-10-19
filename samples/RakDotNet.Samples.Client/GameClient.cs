using RakDotNet.Protocol.Wire;
using RakDotNet.Protocol.Wire.Messages;

namespace RakDotNet.Samples.Client;

/// <summary>Connects, sends one timestamped move written straight into a stack buffer, then closes.</summary>
public static class GameClient
{
    public static async Task RunAsync(string host, int port, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        await using var client = RakPeer.Start(new RakPeerOptions { MaxConnections = 1 });

        var result = await client.ConnectAsync(host, port, cancellationToken: cancellationToken);
        var server = result.EnsureConnected(); // throws RakConnectException carrying the result
        log.WriteLine($"Connected to {server.Address}");

        log.WriteLine($"Send: {SendMove(client, server, 1.5f, -2.0f)}");

        await server.CloseAsync(cancellationToken: cancellationToken);
        log.WriteLine("Closed");
    }

    private static SendStatus SendMove(RakPeer client, RakConnection server, float x, float y)
    {
        Span<byte> buffer = stackalloc byte[32];
        var writer = new BitStreamWriter(buffer);
        new TimestampHeader(client.GetTime()).Write(ref writer);
        writer.WriteByte((byte)MessageId.UserPacketEnum);
        writer.WriteSingle(x);
        writer.WriteSingle(y);
        return server.Send(writer, Reliability.UnreliableSequenced, Priority.High, orderingChannel: 1).Status;
    }
}
