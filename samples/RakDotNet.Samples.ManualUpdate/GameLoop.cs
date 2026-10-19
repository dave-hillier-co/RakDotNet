namespace RakDotNet.Samples.ManualUpdate;

/// <summary>A game loop that owns its thread: one Update and a drain of the stream per frame.</summary>
public static class GameLoop
{
    public static void Run(RakPeer peer, int frames, TimeSpan frame, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(log);
        for (var i = 0; i < frames; i++)
        {
            peer.Update();
            while (peer.Events.TryRead(out var e))
                log.WriteLine($"{e.Kind} from {e.Address}");

            Thread.Sleep(frame);
        }
    }
}
