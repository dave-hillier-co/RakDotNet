using RakDotNet.Protocol.Wire;

namespace RakDotNet.Samples.Server;

/// <summary>The server loop: one ordered stream, read with WaitToReadAsync and an inner TryRead loop.</summary>
public static class GameServer
{
    /// <summary>Starts a server on every IPv4 interface; port 0 takes any free port.</summary>
    public static RakPeer Start(int port) => RakPeer.Start(new RakPeerOptions
    {
        MaxConnections = 64,
        MaxIncomingConnections = 64,
        Bindings = [SocketBinding.Port(port)],
    });

    /// <summary>Handles events until the server shuts down or <paramref name="cancellationToken"/> is cancelled.</summary>
    public static async Task RunAsync(RakPeer server, TextWriter log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(log);
        while (await server.Events.WaitToReadAsync(cancellationToken))
        {
            while (server.Events.TryRead(out var e))
            {
                switch (e.Kind)
                {
                    case PeerEventKind.Connected:
                        e.Connection!.UserState = new Player();
                        log.WriteLine($"{e.Address} connected");
                        break;
                    case PeerEventKind.Message when e.Id == GameIds.Move:
                        var reader = e.GetBodyReader();
                        if (reader.TryReadSingle(out var x) && reader.TryReadSingle(out var y))
                        {
                            ((Player)e.Connection!.UserState!).MoveTo(x, y);
                            log.WriteLine($"{e.Address} moved to {x}, {y}");
                        }
                        break;
                    case PeerEventKind.Disconnected:
                        log.WriteLine($"{e.Address} left: {e.DisconnectReason}");
                        break;
                }
            }
        }
    }
}

/// <summary>The application's message IDs, from ID_USER_PACKET_ENUM.</summary>
public static class GameIds
{
    public const MessageId Move = MessageId.UserPacketEnum;
}

internal sealed class Player
{
    public float X { get; private set; }

    public float Y { get; private set; }

    public void MoveTo(float x, float y) => (X, Y) = (x, y);
}
