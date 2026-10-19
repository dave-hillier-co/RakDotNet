using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Tests;

/// <summary>What a test keeps of a borrowed <see cref="PeerEvent"/>.</summary>
internal sealed record SeenEvent(
    PeerEventKind Kind,
    MessageId Id,
    byte[] Data,
    RakNetGuid RakNetGuid,
    SystemAddress Address,
    bool IsLocal,
    RakConnection? Connection,
    SendReceipt Receipt,
    bool IsIncoming,
    DisconnectReason DisconnectReason,
    bool ClosedLocally,
    ConnectFailure ConnectFailure)
{
    public static SeenEvent From(in PeerEvent e) => new(
        e.Kind, e.Id, e.ToArray(), e.RakNetGuid, e.Address, e.IsLocal, e.Connection, e.Receipt, e.IsIncoming,
        e.DisconnectReason, e.ClosedLocally, e.ConnectFailure);
}

/// <summary>
/// Two Manual-mode peers on loopback UDP sockets sharing a fake clock, so each test decides exactly when cycles run and
/// how far time moves.
/// </summary>
internal sealed class ManualPeers : IDisposable
{
    public static readonly SocketBinding Loopback = new(new IPEndPoint(IPAddress.Loopback, 0));

    private const int MaxSteps = 2_000;

    public ManualPeers(Func<RakPeerOptions, RakPeerOptions>? server = null, Func<RakPeerOptions, RakPeerOptions>? client = null)
    {
        Server = RakPeer.Start((server ?? (o => o))(Options(maxIncoming: 4)));
        Client = RakPeer.Start((client ?? (o => o))(Options(maxIncoming: 0)));
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));

    public RakPeer Server { get; }

    public RakPeer Client { get; }

    public List<SeenEvent> ServerEvents { get; } = [];

    public List<SeenEvent> ClientEvents { get; } = [];

    public SystemAddress ServerAddress => Server.Sockets[0].LocalAddress;

    public RakPeerOptions Options(int maxIncoming) => new()
    {
        MaxConnections = 4,
        MaxIncomingConnections = maxIncoming,
        Bindings = [Loopback],
        Threading = PeerThreading.Manual,
        TimeProvider = Time,
    };

    /// <summary>One cycle on each peer, then everything each stream holds.</summary>
    public void Step(TimeSpan? advance = null)
    {
        Time.Advance(advance ?? TimeSpan.FromMilliseconds(1));
        Server.Update();
        Drain(Server, ServerEvents);
        Client.Update();
        Drain(Client, ClientEvents);
    }

    /// <summary>Steps until <paramref name="condition"/> holds.</summary>
    public void RunUntil(Func<bool> condition, string description)
    {
        for (var i = 0; i < MaxSteps; i++)
        {
            if (condition())
                return;
            Step();
            if (i % 50 == 49)
                Thread.Sleep(1);
        }
        Assert.Fail($"Not reached after {MaxSteps} steps: {description}. Server saw [{Describe(ServerEvents)}]; client saw [{Describe(ClientEvents)}].");
    }

    /// <summary>Steps until both peers have reported the connection, and returns the client's connection to the server.</summary>
    public RakConnection Connect()
    {
        Assert.Equal(ConnectAttemptResult.Started, Client.Connect(ServerAddress));
        RunUntil(() => ClientEvents.Any(e => e.Kind == PeerEventKind.Connected) && ServerEvents.Any(e => e.Kind == PeerEventKind.Connected), "connected");
        return ClientEvents.Single(e => e.Kind == PeerEventKind.Connected).Connection!;
    }

    /// <summary>Steps until nothing more is sent or delivered for a few cycles.</summary>
    public void Settle()
    {
        for (var i = 0; i < 20; i++)
            Step();
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
    }

    public static void Drain(RakPeer peer, List<SeenEvent> into)
    {
        while (peer.Events.TryRead(out var e))
            into.Add(SeenEvent.From(e));
    }

    private static string Describe(List<SeenEvent> events) => string.Join(", ", events.Select(e => $"{e.Kind}:{e.Id}"));
}
