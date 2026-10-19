// Manual mode: the application runs the I/O cycle once per frame, so plugins and events run on its thread.
using RakDotNet;

using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 8, Threading = PeerThreading.Manual });

var frame = TimeSpan.FromMilliseconds(16);
for (var i = 0; i < 600; i++)
{
    peer.Update();
    while (peer.Events.TryRead(out var e))
        Console.WriteLine($"{e.Kind} from {e.Address}");

    Thread.Sleep(frame);
}
