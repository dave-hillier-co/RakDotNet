using System.Diagnostics;
using System.Globalization;
using RakDotNet.Protocol.Wire;

namespace RakDotNet.Benchmarks;

/// <summary>
/// The <c>loopback</c> command: the same run as tools/interop-peer's <c>loopback-bench</c>, with the same arguments and
/// the same result line, so RakDotNet and native RakNet can be compared side by side (docs/performance.md).
/// </summary>
internal static class LoopbackComparison
{
    public const string Command = "loopback";

    private const string Usage =
        "usage: loopback [--messages N] [--bytes N] [--reliability NAME] [--rounds N] [--threading background|manual]";

    public static int Run(ReadOnlySpan<string> args, TextWriter output)
    {
        var messages = 100_000;
        var bytes = 400;
        var rounds = 3;
        var reliability = Reliability.ReliableOrdered;
        var threading = PeerThreading.Background;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--messages" when int.TryParse(value, CultureInfo.InvariantCulture, out messages) && messages > 0:
                case "--bytes" when int.TryParse(value, CultureInfo.InvariantCulture, out bytes) && bytes > 0:
                case "--rounds" when int.TryParse(value, CultureInfo.InvariantCulture, out rounds) && rounds > 0:
                case "--reliability" when TryParseReliability(value, out reliability):
                case "--threading" when Enum.TryParse(value, ignoreCase: true, out threading):
                    i++;
                    break;
                default:
                    Console.Error.WriteLine(Usage);
                    return 2;
            }
        }

        var message = new byte[bytes];
        message[0] = (byte)MessageId.UserPacketEnum;
        using var pair = new LoopbackPair(threading);
        for (var round = 1; round <= rounds; round++)
        {
            var stopwatch = Stopwatch.StartNew();
            pair.Transfer(message, messages, reliability);
            var seconds = stopwatch.Elapsed.TotalSeconds;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"RakDotNet loopback round={round} messages={messages} bytes={bytes} reliability={ReliabilityName(reliability)} "
                + $"seconds={seconds:F3} messages/s={messages / seconds:F0} MB/s={messages * (double)bytes / seconds / 1e6:F1}"));
        }
        return 0;
    }

    // RakNet's PacketReliability names, as the native benchmark and docs/interop-protocol.md spell them.
    private static readonly (string Name, Reliability Value)[] Names =
    [
        ("UNRELIABLE", Reliability.Unreliable),
        ("UNRELIABLE_SEQUENCED", Reliability.UnreliableSequenced),
        ("RELIABLE", Reliability.Reliable),
        ("RELIABLE_ORDERED", Reliability.ReliableOrdered),
        ("RELIABLE_SEQUENCED", Reliability.ReliableSequenced),
    ];

    private static bool TryParseReliability(string? name, out Reliability reliability)
    {
        foreach (var (candidate, value) in Names)
        {
            if (candidate == name)
            {
                reliability = value;
                return true;
            }
        }
        reliability = default;
        return false;
    }

    private static string ReliabilityName(Reliability reliability) => Names.First(n => n.Value == reliability).Name;
}
