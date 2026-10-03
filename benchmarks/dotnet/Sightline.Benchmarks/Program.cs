using BenchmarkDotNet.Running;

namespace Sightline.Benchmarks;

/// <summary>
/// The benchmark suite: <c>dotnet run -c Release --project benchmarks/dotnet/Sightline.Benchmarks -- --filter "*"</c>.
/// </summary>
/// <remarks>
/// Every BenchmarkDotNet option works after the <c>--</c>: <c>--filter</c> to choose benchmarks,
/// <c>--job short</c> for a quick run, <c>--list flat</c> to see them all.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, SuiteConfig.Create());

        // BenchmarkDotNet reports a benchmark that failed and carries on with the rest, exiting 0.
        // The exit code is what tells a script, bench.ps1 or anybody's own, that a figure is missing.
        return summaries.Any(summary => summary.HasCriticalValidationErrors || summary.Reports.Any(report => !report.Success))
            ? 1
            : 0;
    }
}
