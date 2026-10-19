using System.Net;
using Microsoft.Extensions.Time.Testing;
using RakDotNet.Testing;

namespace RakDotNet.Tests;

// docs/api-design.md section 1: the hot path allocates nothing per message in steady state. An idle I/O cycle runs
// every 10 ms in Background mode, so what it allocates must not grow with the number of connections.
public sealed class IdleCycleAllocationTests : IDisposable
{
    private const int Cycles = 50;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
    private readonly InMemoryNetwork _network;
    private readonly List<RakPeer> _peers = [];

    public IdleCycleAllocationTests() => _network = new InMemoryNetwork(_time);

    [Fact]
    public void An_idle_cycle_allocates_no_more_with_many_connections_than_with_one()
    {
        var oneServer = Start(maxIncoming: 1);
        ConnectClients(oneServer, 1);
        var manyServer = Start(maxIncoming: 16);
        ConnectClients(manyServer, 16);

        var one = BytesPerIdleCycle(oneServer);
        var many = BytesPerIdleCycle(manyServer);

        // The core's own per-cycle bookkeeping may allocate a few bytes per connection; the layer above adds nothing.
        Assert.True(many - one <= 15 * 32, $"An idle cycle allocated {one} B with 1 connection and {many} B with 16.");
    }

    public void Dispose()
    {
        foreach (var peer in _peers)
            peer.Dispose();
    }

    private RakPeer Start(int maxIncoming)
    {
        var peer = RakPeer.Start(new RakPeerOptions
        {
            MaxConnections = Math.Max(1, maxIncoming),
            MaxIncomingConnections = maxIncoming,
            Bindings = [new SocketBinding(new IPEndPoint(IPAddress.Loopback, 0))],
            Transport = _network,
            Threading = PeerThreading.Manual,
            TimeProvider = _time,
        });
        _peers.Add(peer);
        return peer;
    }

    private void ConnectClients(RakPeer server, int count)
    {
        var clients = Enumerable.Range(0, count).Select(_ => Start(maxIncoming: 0)).ToList();
        foreach (var client in clients)
            Assert.Equal(ConnectAttemptResult.Started, client.Connect(server.Sockets[0].LocalAddress));
        for (var i = 0; i < 1_000 && server.ConnectionCount < count; i++)
            Step(server, clients);
        Assert.Equal(count, server.ConnectionCount);
        // Let the handshake's ACKs settle so nothing is resent while the server is measured.
        for (var i = 0; i < 100; i++)
            Step(server, clients);
    }

    private void Step(RakPeer server, List<RakPeer> clients)
    {
        Cycle(server);
        foreach (var client in clients)
        {
            client.Update();
            Drain(client);
        }
    }

    private long BytesPerIdleCycle(RakPeer server)
    {
        for (var i = 0; i < Cycles; i++)
            Cycle(server);
        long total = 0;
        for (var i = 0; i < Cycles; i++)
        {
            _time.Advance(TimeSpan.FromMilliseconds(1));
            var before = GC.GetAllocatedBytesForCurrentThread();
            server.Update();
            total += GC.GetAllocatedBytesForCurrentThread() - before;
            Drain(server);
        }
        return total / Cycles;
    }

    private void Cycle(RakPeer peer)
    {
        _time.Advance(TimeSpan.FromMilliseconds(1));
        peer.Update();
        Drain(peer);
    }

    private static void Drain(RakPeer peer)
    {
        while (peer.Events.TryRead(out _))
        {
        }
    }
}
