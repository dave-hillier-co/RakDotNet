using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Testing.Tests;

/// <summary>What a test keeps of a borrowed <see cref="PeerEvent"/>.</summary>
internal sealed record SeenEvent(PeerEventKind Kind, MessageId Id, byte[] Data, SystemAddress Address, RakConnection? Connection)
{
    public static SeenEvent From(in PeerEvent e) => new(e.Kind, e.Id, e.ToArray(), e.Address, e.Connection);
}

/// <summary>Manual-mode peers on one <see cref="InMemoryNetwork"/> sharing a fake clock.</summary>
internal sealed class ManualMesh(InMemoryNetwork network, FakeTimeProvider time) : IDisposable
{
    private const int MaxSteps = 5_000;
    private readonly List<(RakPeer Peer, List<SeenEvent> Seen)> _peers = [];

    public RakPeer Add(int maxIncoming, int maxConnections = 4)
    {
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = maxConnections,
            MaxIncomingConnections = maxIncoming,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Transport = network,
            Threading = PeerThreading.Manual,
            TimeProvider = time,
        });
        _peers.Add((peer, []));
        return peer;
    }

    public List<SeenEvent> Seen(RakPeer peer) => _peers.Single(p => p.Peer == peer).Seen;

    /// <summary>Advances the clock, then runs one cycle on every peer and drains its stream.</summary>
    public void Step(TimeSpan? advance = null)
    {
        time.Advance(advance ?? TimeSpan.FromMilliseconds(1));
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

    public void Settle()
    {
        for (var i = 0; i < 50; i++)
            Step();
    }

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
