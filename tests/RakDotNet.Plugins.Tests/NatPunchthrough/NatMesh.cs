using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Testing;

namespace RakDotNet.Plugins.Tests.NatPunchthrough;

/// <summary>
/// Manual-mode peers on one <see cref="InMemoryNetwork"/> and fake clock, each on an address of its own and either on
/// the network directly or behind an <see cref="InMemoryNat"/>.
/// </summary>
internal sealed class NatMesh : IDisposable
{
    private const int MaxSteps = 30_000;
    private readonly List<(RakPeer Peer, List<SeenEvent> Seen)> _peers = [];

    public NatMesh()
    {
        Network = new InMemoryNetwork(Time);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

    public InMemoryNetwork Network { get; }

    /// <summary>A NAT router with its own public address.</summary>
    public InMemoryNat Nat(string publicAddress, NatBehaviour behaviour) => new(Network, IPAddress.Parse(publicAddress), behaviour);

    /// <summary>Starts a peer on <paramref name="address"/>, behind <paramref name="nat"/> or on the network directly.</summary>
    public RakPeer Add(string address, InMemoryNat? nat = null, int maxIncoming = 8, params IRakPlugin[] plugins) =>
        Add([address], nat, maxIncoming, plugins);

    /// <summary>Starts a peer with a socket on each of <paramref name="addresses"/>.</summary>
    public RakPeer Add(string[] addresses, InMemoryNat? nat = null, int maxIncoming = 8, params IRakPlugin[] plugins)
    {
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = 8,
            MaxIncomingConnections = maxIncoming,
            Bindings = [.. addresses.Select(a => new SocketBinding(new IPEndPoint(IPAddress.Parse(a), 0)))],
            Transport = nat is null ? Network : nat,
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

    /// <summary>Connects <paramref name="client"/> to <paramref name="server"/>'s first socket and returns the client's connection.</summary>
    public RakConnection Connect(RakPeer client, RakPeer server) => Connect(client, server.Sockets[0].LocalAddress, server);

    /// <summary>Connects <paramref name="client"/> to <paramref name="address"/>, where <paramref name="server"/> listens.</summary>
    public RakConnection Connect(RakPeer client, Protocol.Wire.SystemAddress address, RakPeer server)
    {
        var before = Seen(server).Count(e => e.Kind == PeerEventKind.Connected);
        Assert.Equal(ConnectAttemptResult.Started, client.Connect(address));
        RunUntil(
            () => Seen(client).Any(e => e.Kind == PeerEventKind.Connected && e.RakNetGuid == server.RakNetGuid)
                && Seen(server).Count(e => e.Kind == PeerEventKind.Connected) > before,
            "connected");
        return Seen(client).Last(e => e.Kind == PeerEventKind.Connected && e.RakNetGuid == server.RakNetGuid).Connection!;
    }

    public void Dispose()
    {
        foreach (var (peer, _) in _peers)
            peer.Dispose();
    }
}
