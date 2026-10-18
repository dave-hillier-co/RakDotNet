using RakDotNet.Interop.Tests.Harness;

namespace RakDotNet.Interop.Tests.Scenarios;

/// <summary>
/// Recaptures tests/vectors from native-native runs without faults. Recapturing is an explicit act
/// (docs/golden-vectors.md), so this only runs with RAKDOTNET_RECAPTURE=1.
/// </summary>
public sealed class GoldenVectorCapture
{
    public const string RecaptureEnvironmentVariable = "RAKDOTNET_RECAPTURE";

    // Long enough for the trailing ACKs of the last exchange to cross the proxy.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios => ["unconnected-ping", "connect-send-disconnect", "split-packet"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task Recaptures_the_golden_vector_when_requested(string scenario)
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable(RecaptureEnvironmentVariable) == "1",
            $"Set {RecaptureEnvironmentVariable}=1 to recapture tests/vectors/{scenario}.json.");

        await using var session = await InteropSession.StartAsync(PeerKind.Native, PeerKind.Native, FaultOptions.None, Token);
        var description = scenario switch
        {
            "unconnected-ping" => "Native client pings a native server with an offline ping response of 'hello'.",
            "connect-send-disconnect" =>
                "Native client connects to a native server, sends RELIABLE_ORDERED, UNRELIABLE, RELIABLE_SEQUENCED and RELIABLE_ORDERED_WITH_ACK_RECEIPT messages on channels 0 and 3, then disconnects.",
            "split-packet" => "Native client connects to a native server and sends one 4000-byte RELIABLE_ORDERED message, which is split.",
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
