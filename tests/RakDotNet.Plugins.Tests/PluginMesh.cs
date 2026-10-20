using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;
using RakDotNet.Testing;

namespace RakDotNet.Plugins.Tests;

/// <summary>What a test keeps of a borrowed <see cref="PeerEvent"/>.</summary>
internal sealed record SeenEvent(PeerEventKind Kind, byte[] Data, RakNetGuid RakNetGuid, SystemAddress Address, RakConnection? Connection)
{
    public byte Id => Data.Length > 0 ? Data[0] : (byte)0;

    public static SeenEvent From(in PeerEvent e) => new(e.Kind, e.ToArray(), e.RakNetGuid, e.Address, e.Connection);
}

/// <summary>Manual-mode peers on one <see cref="InMemoryNetwork"/> sharing a fake clock.</summary>
internal sealed class PluginMesh : IDisposable
{
    private const int MaxSteps = 5_000;
    private readonly List<(RakPeer Peer, List<SeenEvent> Seen)> _peers = [];

    public PluginMesh()
    {
        Network = new InMemoryNetwork(Time);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

    public InMemoryNetwork Network { get; }

    public RakPeer Add(int maxIncoming = 4, params IRakPlugin[] plugins)
    {
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 4,
            MaxIncomingConnections = maxIncoming,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Transport = Network,
            Threading = PeerThreading.Manual,
            TimeProvider = Time,
            Plugins = plugins,
        });
        _peers.Add((peer, []));
        return peer;
    }

    public List<SeenEvent> Seen(RakPeer peer) => _peers.Single(p => p.Peer == peer).Seen;

    /// <summary>Advances the clock, then runs one cycle on every peer and drains its stream.</summary>
    public void Step(TimeSpan? advance = null)
    {
        Time.Advance(advance ?? TimeSpan.FromMilliseconds(1));
        foreach (var (peer, seen) in _peers)
        {
            if (!peer.IsActive)
                continue;
            peer.Update();
            while (peer.Events.TryRead(out var e))
                seen.Add(SeenEvent.From(e));
        }
    }

    public void RunUntil(Func<bool> condition, string description)
    {
        for (var i = 0; i < MaxSteps; i++)
        {
            if (condition())
                return;
            Step();
        }
        Assert.Fail($"Not reached after {MaxSteps} steps: {description}.");
    }

    /// <summary>Steps for <paramref name="duration"/> of fake time, 1 ms at a time.</summary>
    public void Run(TimeSpan duration)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromMilliseconds(1))
            Step();
    }

    public void Settle() => Run(TimeSpan.FromMilliseconds(50));

    /// <summary>Connects <paramref name="client"/> to <paramref name="server"/> and returns the client's connection.</summary>
    public RakConnection Connect(RakPeer client, RakPeer server)
    {
        Assert.Equal(ConnectAttemptResult.Started, client.Connect(server.Sockets[0].LocalAddress));
        RunUntil(() => Seen(client).Any(e => e.Kind == PeerEventKind.Connected) && Seen(server).Any(e => e.Kind == PeerEventKind.Connected), "connected");
        return Seen(client).Last(e => e.Kind == PeerEventKind.Connected).Connection!;
    }

    public void Dispose()
    {
        foreach (var (peer, _) in _peers)
            peer.Dispose();
    }
}
