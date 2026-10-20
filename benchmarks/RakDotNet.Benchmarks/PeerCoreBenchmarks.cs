using BenchmarkDotNet.Attributes;
using RakDotNet.Protocol.Peer;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// <see cref="PeerCore"/>, the layer that routes datagrams to connections and turns what they deliver into peer
/// events, measured by replaying a <see cref="PeerRecording"/>. One operation is one step: with one message per step
/// that is one message in its own datagram; with 16 it is one datagram carrying 16 small messages.
/// </summary>
/// <remarks>
/// <see cref="ClientSendAndUpdate"/>: the client sends the step's messages, updates, and takes the server's recorded
/// acknowledgements. <see cref="ServerReceiveAndUpdate"/>: the server takes the client's recorded datagrams, updates
/// and delivers the messages as events. When the recording runs out the replayed side is replaced by a freshly
/// connected one, once every <see cref="RecordingSteps"/> operations. Datagrams and message events are taken as RakPeer
/// takes them: without a copy, and recycled once read.
/// </remarks>
[MemoryDiagnoser]
public class PeerCoreBenchmarks
{
    private const int RecordingSteps = 16_384;
    private const int MessageBytes = 32;

    private PeerRecording _recording = null!;
    private PeerCore _client = null!;
    private PeerCore _server = null!;
    private int _clientStep;
    private int _serverStep;

    [Params(1, 16)]
    public int MessagesPerStep { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var message = new byte[MessageBytes];
        message[0] = (byte)MessageId.UserPacketEnum;
        _recording = new PeerRecording(RecordingSteps, MessagesPerStep, message);
        VerifyReplay();
        _client = PeerRecording.ConnectedPair().Client;
        _server = PeerRecording.ConnectedPair().Server;
    }

    [Benchmark]
    public void ClientSendAndUpdate()
    {
        if (_clientStep == RecordingSteps)
        {
            _client = PeerRecording.ConnectedPair().Client;
            _clientStep = 0;
        }
        var step = _clientStep++;
        var nowUs = _recording.TimeOf(step);
        _recording.SendStep(_client, nowUs);
        while (_client.TryDequeueDatagram(out _, out _, out _))
        {
        }
        var acks = _recording.ToClient(step);
        for (var i = 0; i < acks.Count; i++)
            _client.Receive(PeerRecording.ServerAddress, acks[i], nowUs);
        while (_client.TryDequeueEvent(out _))
        {
        }
    }

    [Benchmark]
    public int ServerReceiveAndUpdate()
    {
        if (_serverStep == RecordingSteps)
        {
            _server = PeerRecording.ConnectedPair().Server;
            _serverStep = 0;
        }
        var step = _serverStep++;
        var nowUs = _recording.TimeOf(step);
        var datagrams = _recording.ToServer(step);
        for (var i = 0; i < datagrams.Count; i++)
            _server.Receive(PeerRecording.ClientAddress, datagrams[i], nowUs);
        _server.Update(nowUs);
        while (_server.TryDequeueDatagram(out _, out _, out _))
        {
        }
        return PeerRecording.CountDelivered(_server);
    }

    // A replayed core must send exactly what the live one sent, or the replay measures something else.
    private void VerifyReplay()
    {
        var client = PeerRecording.ConnectedPair().Client;
        var server = PeerRecording.ConnectedPair().Server;
        for (var step = 0; step < RecordingSteps; step++)
        {
            var nowUs = _recording.TimeOf(step);
            _recording.SendStep(client, nowUs);
            AssertSame(PeerRecording.Take(client), _recording.ToServer(step), "client", step);
            foreach (var ack in _recording.ToClient(step))
                client.Receive(PeerRecording.ServerAddress, ack, nowUs);
            PeerRecording.CountDelivered(client);

            foreach (var datagram in _recording.ToServer(step))
                server.Receive(PeerRecording.ClientAddress, datagram, nowUs);
            server.Update(nowUs);
            PeerRecording.CountDelivered(server);
            AssertSame(PeerRecording.Take(server), _recording.ToClient(step), "server", step);
        }
    }

    private static void AssertSame(List<byte[]> replayed, IReadOnlyList<byte[]> recorded, string side, int step)
    {
        if (replayed.Count != recorded.Count)
            throw new InvalidOperationException($"The replayed {side} sent {replayed.Count} datagrams at step {step}, not {recorded.Count}.");
        for (var i = 0; i < replayed.Count; i++)
        {
            if (!replayed[i].AsSpan().SequenceEqual(recorded[i]))
                throw new InvalidOperationException($"The replayed {side} diverged from the recording at step {step}.");
        }
    }
}
