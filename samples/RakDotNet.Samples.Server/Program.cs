// A server on the background loop, reading borrowed events without allocating.
using RakDotNet;
using RakDotNet.Protocol.Wire;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

await using var server = RakPeer.Start(new RakPeerOptions
{
    MaxConnections = 64,
    MaxIncomingConnections = 64,
    Bindings = [SocketBinding.Port(19132)],
});

while (await server.Events.WaitToReadAsync(cts.Token))
{
    while (server.Events.TryRead(out var e))
    {
        switch (e.Kind)
        {
            case PeerEventKind.Connected:
                e.Connection!.UserState = new Player();
                Console.WriteLine($"{e.Address} connected");
                break;
            case PeerEventKind.Message when e.Id == GameIds.Move:
                var reader = e.GetBodyReader();
                if (reader.TryReadSingle(out var x) && reader.TryReadSingle(out var y))
                    ((Player)e.Connection!.UserState!).MoveTo(x, y);
                break;
            case PeerEventKind.Disconnected:
                Console.WriteLine($"{e.Address} left: {e.DisconnectReason}");
                break;
        }
    }
}

internal static class GameIds
{
    public const MessageId Move = MessageId.UserPacketEnum;
}

internal sealed class Player
{
    public float X { get; private set; }

    public float Y { get; private set; }

    public void MoveTo(float x, float y) => (X, Y) = (x, y);
}
