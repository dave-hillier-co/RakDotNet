using System.Net;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// The datagrams a connected client and server <see cref="PeerCore"/> exchange, step by step, while the client sends a
/// steady stream of messages. Like <see cref="SessionRecording"/>, it relies on the core being deterministic: a fresh
/// pair connected the same way reaches the same state, so either side can be replayed alone.
/// </summary>
internal sealed class PeerRecording
{
    public const long StepUs = 1_000;

    public static readonly IPEndPoint ClientAddress = new(IPAddress.Parse("10.0.0.1"), 50_001);
    public static readonly IPEndPoint ServerAddress = new(IPAddress.Parse("10.0.0.2"), 19_132);

    private const ulong ClientGuid = 0x1111_2222_3333_4444;
    private const ulong ServerGuid = 0x5555_6666_7777_8888;
    private const int MaxHandshakeSteps = 1_000;
    private const int SettleSteps = 100;

    private readonly List<byte[]>[] _toServer;
    private readonly List<byte[]>[] _toClient;

    public PeerRecording(int steps, int messagesPerStep, byte[] message)
    {
        Steps = steps;
        MessagesPerStep = messagesPerStep;
        Message = message;
        _toServer = new List<byte[]>[steps];
        _toClient = new List<byte[]>[steps];

        var (client, server, startUs) = ConnectedPair();
        StartUs = startUs;
        var delivered = 0;
        for (var step = 0; step < steps; step++)
        {
            var nowUs = TimeOf(step);
            SendStep(client, nowUs);
            _toServer[step] = Take(client);
            foreach (var datagram in _toServer[step])
                server.Receive(ClientAddress, datagram, nowUs);
            server.Update(nowUs);
            delivered += CountDelivered(server);
            _toClient[step] = Take(server);
            foreach (var datagram in _toClient[step])
                client.Receive(ServerAddress, datagram, nowUs);
            CountDelivered(client);
        }
        if (delivered != steps * messagesPerStep)
            throw new InvalidOperationException($"The recording delivered {delivered} of {steps * messagesPerStep} messages.");
    }

    public int Steps { get; }

    public int MessagesPerStep { get; }

    public byte[] Message { get; }

    /// <summary>The time the pair finished connecting; step 0 runs one step later.</summary>
    public long StartUs { get; }

    public long TimeOf(int step) => StartUs + (step + 1) * StepUs;

    public IReadOnlyList<byte[]> ToServer(int step) => _toServer[step];

    public IReadOnlyList<byte[]> ToClient(int step) => _toClient[step];

    /// <summary>A client and server that have connected and settled, and the time they reached.</summary>
    public static (PeerCore Client, PeerCore Server, long NowUs) ConnectedPair()
    {
        var client = new PeerCore(new PeerOptions
        {
            Guid = ClientGuid,
            LocalAddresses = LocalAddressList.Build([ClientAddress.Address], (ushort)ClientAddress.Port),
        });
        var server = new PeerCore(new PeerOptions
        {
            Guid = ServerGuid,
            MaxIncomingConnections = 8,
            LocalAddresses = LocalAddressList.Build([ServerAddress.Address], (ushort)ServerAddress.Port),
        });
        var nowUs = 1_000_000_000L;
        var result = client.Connect(ServerAddress, default, attemptCount: 12, intervalMs: 500, timeoutMs: 0, nowUs);
        if (result != ConnectionAttemptResult.ConnectionAttemptStarted)
            throw new InvalidOperationException($"Connect returned {result}.");

        var connected = 0;
        for (var step = 0; connected < 2 || step < SettleSteps; step++)
        {
            if (step == MaxHandshakeSteps)
                throw new InvalidOperationException("The pair did not connect.");
            nowUs += StepUs;
            while (client.TryDequeueDatagram(out var datagram))
                server.Receive(ClientAddress, datagram.Data, nowUs);
            while (server.TryDequeueDatagram(out var datagram))
                client.Receive(ServerAddress, datagram.Data, nowUs);
            client.Update(nowUs);
            server.Update(nowUs);
            while (client.TryDequeueEvent(out var e))
                connected += e is OutgoingConnectionCompleted ? 1 : 0;
            while (server.TryDequeueEvent(out var e))
                connected += e is IncomingConnectionCompleted ? 1 : 0;
        }
        while (client.TryDequeueDatagram(out var datagram))
            server.Receive(ClientAddress, datagram.Data, nowUs);
        while (server.TryDequeueDatagram(out var datagram))
            client.Receive(ServerAddress, datagram.Data, nowUs);
        return (client, server, nowUs);
    }

    /// <summary>Sends this step's messages from <paramref name="client"/> to the server, then updates it.</summary>
    public void SendStep(PeerCore client, long nowUs)
    {
        for (var i = 0; i < MessagesPerStep; i++)
            client.Send(ServerGuid, Message, Reliability.ReliableOrdered, Priority.High, channel: 0, nowUs);
        client.Update(nowUs);
    }

    public static int CountDelivered(PeerCore peer)
    {
        var delivered = 0;
        while (peer.TryDequeueEvent(out var e))
            delivered += e is MessageReceived ? 1 : 0;
        return delivered;
    }

    public static List<byte[]> Take(PeerCore peer)
    {
        var datagrams = new List<byte[]>();
        while (peer.TryDequeueDatagram(out var datagram))
            datagrams.Add(datagram.Data);
        return datagrams;
    }
}
