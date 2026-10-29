using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace Resourcefull.Tests.Performance;

/// <summary>What one scenario cost: medians over its repetitions.</summary>
internal sealed record Measurement(long RoundTrips, long LogicalReads, long AllocatedBytes, long PeakHeapBytes, double Seconds,
    long ContentBytes)
{
    [JsonIgnore]
    public double MebibytesPerSecond => ContentBytes / 1024.0 / 1024.0 / Seconds;
}

/// <summary>
/// The committed measurements every run is held to. A metric that gets worse than its tolerance allows fails the
/// scenario when <c>RESOURCEFULL_PERF_ENFORCE</c> is <c>true</c>, as it is in CI; otherwise the comparison is only
/// reported, since a developer's machine and SQL Server differ from CI's. The baseline comes from CI too: to change it,
/// copy a CI run's <c>performance-results.json</c> artifact over <c>baseline.json</c> and commit it with the reason.
/// </summary>
internal static class Baseline
{
    // Round trips don't vary from run to run, so any increase fails. Reads and allocations vary a little with SQL
    // Server's plans and SqlClient's buffering, and reads also by a page or two with how the rows lie on their pages.
    // Peak heap depends on when the GC runs, and time on how busy the machine is, so those two only catch large
    // regressions.
    private const double ReadsTolerance = 0.10;
    private const long ReadsSlackPages = 2;
    private const double AllocationTolerance = 0.10;
    private const double PeakHeapTolerance = 0.50;
    private const long PeakHeapSlackBytes = 1024 * 1024;
    private const double TimeTolerance = 1.00;
    private const double TimeSlackSeconds = 0.05;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static bool Enforce => Environment.GetEnvironmentVariable("RESOURCEFULL_PERF_ENFORCE") == "true";

    /// <summary>Every run's measurements, uploaded by CI so trends can be compared across runs.</summary>
    public static string ResultsPath => Path.Combine(AppContext.BaseDirectory, "performance-results.json");

    public static void Check(string scenario, Measurement measured, ITestOutputHelper output)
    {
        Record(ResultsPath, scenario, measured);

        // Read from the copy built next to the tests; a missing file fails rather than passing everything unchecked.
        var all = JsonSerializer.Deserialize<SortedDictionary<string, Measurement>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "baseline.json")), JsonOptions)!;
        all.TryGetValue(scenario, out Measurement? baseline);
        var failures = new List<string>();
        // Once there is a baseline, a scenario missing from it was renamed or added without one, and would go unchecked.
        if (baseline is null && all.Count > 0) failures.Add($"{scenario} has no baseline.");
        Compare("round trips", measured.RoundTrips, baseline?.RoundTrips, 0, 0, failures, output);
        Compare("logical reads", measured.LogicalReads, baseline?.LogicalReads, ReadsTolerance, ReadsSlackPages, failures,
            output);
        Compare("allocated bytes", measured.AllocatedBytes, baseline?.AllocatedBytes, AllocationTolerance, 0, failures, output);
        Compare("peak heap bytes", measured.PeakHeapBytes, baseline?.PeakHeapBytes, PeakHeapTolerance, PeakHeapSlackBytes,
            failures, output);
        Compare("seconds", measured.Seconds, baseline?.Seconds, TimeTolerance, TimeSlackSeconds, failures, output);
        if (measured.ContentBytes > 0) output.WriteLine($"throughput: {measured.MebibytesPerSecond:F1} MiB/s");

        if (failures.Count > 0 && Enforce)
        {
            Assert.Fail($"{scenario} regressed past its baseline:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}" +
                $"{Environment.NewLine}If the change is intended, update the baseline (see CONTRIBUTING.md) and say why in the commit.");
        }
    }

    private static void Compare(string metric, double measured, double? baseline, double tolerance, double slack,
        List<string> failures, ITestOutputHelper output)
    {
        if (baseline is null)
        {
            output.WriteLine($"{metric}: {measured:#,0.####} (no baseline)");
            return;
        }

        double limit = baseline.Value * (1 + tolerance) + slack;
        string line = $"{metric}: {measured:#,0.####} against a baseline of {baseline:#,0.####} (limit {limit:#,0.####})";
        output.WriteLine(line);
        if (measured > limit) failures.Add(line);
    }

    private static void Record(string path, string scenario, Measurement measured)
    {
        SortedDictionary<string, Measurement> all = File.Exists(path)
            ? JsonSerializer.Deserialize<SortedDictionary<string, Measurement>>(File.ReadAllText(path), JsonOptions)!
            : [];
        all[scenario] = measured;
        File.WriteAllText(path, JsonSerializer.Serialize(all, JsonOptions) + Environment.NewLine);
    }
}
