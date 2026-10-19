// Manual mode: the application runs the I/O cycle once per frame, so plugins and events run on its thread.
using RakDotNet;
using RakDotNet.Samples.ManualUpdate;

using var peer = RakPeer.Start(new RakPeerOptions { MaxConnections = 8, Threading = PeerThreading.Manual });
GameLoop.Run(peer, frames: 600, frame: TimeSpan.FromMilliseconds(16), Console.Out);
