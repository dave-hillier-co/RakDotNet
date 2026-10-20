using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace RakDotNet.Benchmarks;

/// <summary>
/// Messages per second, or megabytes of message payload per second, from the mean time per operation, for benchmarks
/// whose operation is one message of <c>MessageBytes</c> bytes.
/// </summary>
internal sealed class ThroughputColumn(bool bytes) : IColumn
{
    private const string MessageBytesParameter = nameof(LoopbackBenchmarks.MessageBytes);

    public static ThroughputColumn Messages { get; } = new(bytes: false);

    public static ThroughputColumn Megabytes { get; } = new(bytes: true);

    public string Id => ColumnName;

    public string ColumnName => bytes ? "MB/s" : "Messages/s";

    public bool AlwaysShow => false;

    public ColumnCategory Category => ColumnCategory.Custom;

    public int PriorityInCategory => bytes ? 1 : 0;

    public bool IsNumeric => true;

    public UnitType UnitType => UnitType.Dimensionless;

    public string Legend => bytes
        ? "Message payload megabytes (10^6 bytes) delivered per second"
        : "Messages delivered per second";

    public bool IsAvailable(Summary summary) => summary.BenchmarksCases.Any(HasMessageBytes);

    public bool IsDefault(Summary summary, BenchmarkCase benchmarkCase) => false;

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase) =>
        GetValue(summary, benchmarkCase, SummaryStyle.Default);

    public string GetValue(Summary summary, BenchmarkCase benchmarkCase, SummaryStyle style)
    {
        if (!HasMessageBytes(benchmarkCase) || summary[benchmarkCase]?.ResultStatistics is not { } statistics || statistics.Mean <= 0)
            return "-";
        var messagesPerSecond = 1e9 / statistics.Mean;
        if (!bytes)
            return messagesPerSecond.ToString("N0", CultureInfo.InvariantCulture);
        var messageBytes = Convert.ToDouble(benchmarkCase.Parameters[MessageBytesParameter], CultureInfo.InvariantCulture);
        return (messagesPerSecond * messageBytes / 1e6).ToString("N1", CultureInfo.InvariantCulture);
    }

    private static bool HasMessageBytes(BenchmarkCase benchmarkCase) =>
        benchmarkCase.Parameters.Items.Any(p => p.Name == MessageBytesParameter);
}
