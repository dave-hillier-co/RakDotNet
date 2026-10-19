using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Recaptures tests/vectors from native-native runs. Recapturing is an explicit act (docs/golden-vectors.md), so this
/// only runs with RAKDOTNET_RECAPTURE=1. Only reliable-under-loss injects faults; the datagrams the proxy dropped are
/// kept, since the native peer sent them.
/// </summary>
[Collection(OneScenarioAtATime.Name)]
public sealed class GoldenVectorCapture
{
    public const string RecaptureEnvironmentVariable = "RAKDOTNET_RECAPTURE";

    // Long enough for the trailing ACKs of the last exchange to cross the proxy.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios =>
    [
        "unconnected-ping",
        "connect-send-disconnect",
        "split-packet",
        "unconnected-ping-open-connections",
        "advertise-system",
        "no-free-incoming-connections",
        "connection-banned",
        "all-channels",
        "reliable-under-loss",
    ];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Recaptures_the_golden_vector_when_requested(string scenario)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RecaptureEnvironmentVariable) == "1",
            $"Set {RecaptureEnvironmentVariable}=1 to recapture tests/vectors/{scenario}.json.");

        var serverOptions = scenario == "no-free-incoming-connections" ? new StartupOptions(MaxIncoming: 0) : null;
        await using var session = await InteropSession.StartAsync(PeerKind.Native, PeerKind.Native, FaultOptions.None, serverOptions, Token);
        var description = scenario switch
        {
            "unconnected-ping" => "Native client pings a native server with an offline ping response of 'hello'.",
            "connect-send-disconnect" =>
                "Native client connects to a native server, sends RELIABLE_ORDERED, UNRELIABLE, RELIABLE_SEQUENCED and RELIABLE_ORDERED_WITH_ACK_RECEIPT messages on channels 0 and 3, then disconnects.",
            "split-packet" => "Native client connects to a native server and sends one 4000-byte RELIABLE_ORDERED message, which is split.",
            "unconnected-ping-open-connections" => "Native client pings a native server that accepts connections, asking for a pong only if it does.",
            "advertise-system" => "Native client advertises itself to a native server with the data 'hello world' and its terminating zero.",
            "no-free-incoming-connections" => "Native client tries to connect to a native server that accepts no incoming connections and is refused.",
            "connection-banned" => "Native client tries to connect to a native server that has banned 127.0.0.1 and is refused.",
            "all-channels" =>
                "Native client connects to a native server and sends three messages on each of the 32 ordering channels: RELIABLE_ORDERED, RELIABLE_SEQUENCED, then RELIABLE_ORDERED.",
            "reliable-under-loss" =>
                "Native client connects to a native server and sends 40 RELIABLE_ORDERED messages of 200 bytes on four channels while the proxy drops 20% of datagrams, so the capture holds NAKs and resends.",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        switch (scenario)
        {
            case "unconnected-ping":
                await ScenarioFlows.UnconnectedPingAsync(session, Token);
                break;
            case "connect-send-disconnect":
                await ScenarioFlows.ConnectSendDisconnectAsync(session, Token);
                break;
            case "split-packet":
                await ScenarioFlows.SplitPacketAsync(session, Token);
                break;
            case "unconnected-ping-open-connections":
                Assert.True(await session.Client.PingAsync(session.ProxyEndPoint, onlyIfAccepting: true, Token));
                await session.ClientInbox.WaitForPacketAsync(
                    p => p.MessageId == (byte)Protocol.Wire.MessageId.UnconnectedPong, "ID_UNCONNECTED_PONG", InteropSession.DefaultTimeout, Token);
                break;
            case "advertise-system":
                await OfflineFlows.AdvertiseSystemAsync(session, Token);
                break;
            case "no-free-incoming-connections":
                await ConnectionLimitFlows.ConnectBeyondTheLimitAsync(session, session.ClientPeer, Token);
                break;
            case "connection-banned":
                await AddressFlows.ConnectWhileBannedAsync(session, Token);
                break;
            case "all-channels":
                await OrderingFlows.SendOnEveryChannelAsync(session, FaultOptions.None, messagesPerChannel: 3, maxSize: 16, Token);
                break;
            case "reliable-under-loss":
                await ScenarioFlows.ReliableOrderedUnderLossAsync(
                    session, new FaultOptions(Seed: 20261022, LossProbability: 0.2), count: 40, messageSize: 200, Token);
                break;
        }
        await Task.Delay(Settle, Token);

        var vector = GoldenVectorWriter.FromProxy(
            scenario,
            description,
            TimeProvider.System.GetUtcNow(),
            session.ClientStarted.PeerGuid,
            session.ServerStarted.PeerGuid,
            session.Proxy.Datagrams);
        var path = Path.Combine(RepoPaths.VectorsDirectory, $"{scenario}.json");
        GoldenVectorWriter.WriteFile(path, vector);
        TestContext.Current.TestOutputHelper?.WriteLine($"Wrote {vector.Datagrams.Count} datagrams to {path}.");

        Assert.NotEmpty(vector.Datagrams);
        Assert.Equal(vector.Datagrams.Count, GoldenVectorReader.ReadFile(path).Datagrams.Count);
    }
}
