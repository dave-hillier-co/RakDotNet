// BenchmarkDotNet suite (docs/performance.md). "loopback" runs the side-by-side comparison with native RakNet instead.
// The exit code is nonzero when any benchmark fails to build or run, so CI can run a short job and fail only on error.
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using RakDotNet.Benchmarks;

if (args.Length > 0 && args[0] == LoopbackComparison.Command)
    return LoopbackComparison.Run(args.AsSpan(1), Console.Out);

var config = DefaultConfig.Instance.AddColumn(ThroughputColumn.Messages, ThroughputColumn.Megabytes);
var summaries = BenchmarkSwitcher.FromAssembly(typeof(LoopbackBenchmarks).Assembly).Run(args, config).ToList();
var failed = summaries.Count == 0
    || summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Length != s.BenchmarksCases.Length || s.Reports.Any(r => !r.Success));
if (failed)
    Console.Error.WriteLine("One or more benchmarks failed.");
return failed ? 1 : 0;
