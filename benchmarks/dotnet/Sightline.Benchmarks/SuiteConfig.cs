using System.Globalization;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;

namespace Sightline.Benchmarks;

/// <summary>
/// How every benchmark in the suite is run and reported.
/// </summary>
/// <remarks>
/// <para>
/// Memory is measured for every benchmark here rather than for those that remember to ask. A change
/// that doubles what the live view allocates per frame is a regression even when a desktop shows no
/// slowdown, because a phone pays for it in garbage collection, dropped frames and battery.
/// </para>
/// <para>
/// Operations a second sit beside the mean because each operation is something a person counts: a
/// frame, a megabyte, a request. For a frame that column is the frame rate, for a megabyte the
/// download speed.
/// </para>
/// <para>
/// BenchmarkDotNet switches Windows to its High performance power plan for a run unless told not
/// to. This suite tells it not to: a benchmark has no business changing a setting of the machine it
/// runs on, so the plan in force is recorded with the baseline instead. It applies as a mutator, on
/// top of whichever job the command line picks, so <c>--job short</c> still works.
/// </para>
/// <para>
/// The program BenchmarkDotNet generates gets ten minutes to build rather than its default two. On
/// a laptop that is busy building something else two is not always enough, and a build that times
/// out measures nothing.
/// </para>
/// <para>
/// The full JSON report is what tools/scripts/bench_compare.py reads and what the baseline keeps.
/// Results go to <c>artifacts/bench/&lt;time&gt;</c> under the working directory, which git ignores,
/// unless <c>--artifacts</c> names another folder.
/// </para>
/// </remarks>
internal static class SuiteConfig
{
    /// <summary>The configuration, with a fresh results folder named for the moment it was made.</summary>
    public static IConfig Create() => DefaultConfig.Instance
        .AddJob(Job.Default.DontEnforcePowerPlan().AsMutator())
        .AddDiagnoser(MemoryDiagnoser.Default)
        .AddColumn(StatisticColumn.OperationsPerSecond)
        .AddExporter(JsonExporter.Full)
        .WithBuildTimeout(TimeSpan.FromMinutes(10))
        .WithArtifactsPath(Path.GetFullPath(Path.Combine(
            "artifacts", "bench", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture))));
}
