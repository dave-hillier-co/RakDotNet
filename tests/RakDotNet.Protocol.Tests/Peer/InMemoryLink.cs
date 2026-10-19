using System.Net;
using RakDotNet.Protocol.Peer;

namespace RakDotNet.Protocol.Tests.Peer;

/// <summary>
/// A hand-written stand-in for the network and the clock: peers attached at addresses exchange datagrams with one
/// step of latency, and time advances only when a test steps it.
/// </summary>
internal sealed class InMemoryLink
{
    private readonly Dictionary<IPEndPoint, PeerCore> _peers = [];
    private readonly Dictionary<PeerCore, List<PeerEvent>> _events = [];
    private readonly Dictionary<PeerCore, long> _clockOffsetsUs = [];

    public InMemoryLink(long startUs = 1_000_000_000)
    {
        NowUs = startUs;
    }

    public long NowUs { get; private set; }

    public long NowMs => NowUs / 1000;

    /// <summary>Every datagram delivered, in order.</summary>
    public List<(IPEndPoint From, IPEndPoint To, byte[] Data)> Delivered { get; } = [];

    /// <summary>Every datagram sent, including those dropped, in order.</summary>
    public List<(IPEndPoint From, IPEndPoint To, byte[] Data)> Sent { get; } = [];

    /// <summary>When it returns true for a datagram, the datagram is dropped.</summary>
    public Func<IPEndPoint, IPEndPoint, byte[], bool>? Drop { get; set; }

    /// <summary>Attaches <paramref name="peer"/> at <paramref name="address"/>.</summary>
    /// <param name="clockOffsetUs">How far the peer's clock is ahead of the link's.</param>
    public PeerCore Attach(IPEndPoint address, PeerCore peer, long clockOffsetUs = 0)
    {
        _peers.Add(address, peer);
        _events.Add(peer, []);
        _clockOffsetsUs.Add(peer, clockOffsetUs);
        return peer;
    }

    /// <summary>The time on <paramref name="peer"/>'s own clock.</summary>
    public long NowUsOf(PeerCore peer) => NowUs + _clockOffsetsUs[peer];

    /// <summary>Moves <paramref name="peer"/>'s clock by <paramref name="us"/>.</summary>
    public void ShiftClock(PeerCore peer, long us) => _clockOffsetsUs[peer] += us;

    public IReadOnlyList<PeerEvent> EventsOf(PeerCore peer) => _events[peer];

    /// <summary>Delivers what was sent since the last step, advances the clock, then updates every peer.</summary>
    public void Step(long us = 1_000)
    {
        NowUs += us;
        var inFlight = new List<(IPEndPoint From, OutgoingDatagram Datagram)>();
        foreach (var (from, peer) in _peers)
        {
            while (peer.TryDequeueDatagram(out var datagram))
                inFlight.Add((from, datagram));
        }
        foreach (var (from, datagram) in inFlight)
        {
            Sent.Add((from, datagram.Remote, datagram.Data));
            if (Drop?.Invoke(from, datagram.Remote, datagram.Data) == true)
                continue;
            Delivered.Add((from, datagram.Remote, datagram.Data));
            if (_peers.TryGetValue(datagram.Remote, out var to))
                to.Receive(from, datagram.Data, NowUsOf(to));
        }
        foreach (var peer in _peers.Values)
        {
            peer.Update(NowUsOf(peer));
            while (peer.TryDequeueEvent(out var e))
                _events[peer].Add(e);
        }
    }

    public void Run(TimeSpan duration, long stepUs = 1_000)
    {
        var until = NowUs + (long)duration.TotalMicroseconds;
        while (NowUs < until)
            Step(stepUs);
    }

    /// <summary>Steps until <paramref name="condition"/> holds, failing after <paramref name="limit"/>.</summary>
    public void RunUntil(Func<bool> condition, TimeSpan limit, long stepUs = 1_000)
    {
        var until = NowUs + (long)limit.TotalMicroseconds;
        while (!condition())
        {
            Assert.True(NowUs < until, $"Condition not met within {limit}.");
            Step(stepUs);
        }
    }
}
