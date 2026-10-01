using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Reporting;

/// <summary>
/// Regression gates, defined in <c>gates.json</c>:
/// <list type="bullet">
/// <item><b>correctness</b>: every scenario returned exactly the authorized answer.</item>
/// <item><b>closure</b>: the closure the loader wrote, the closure after the maintenance pass, and the closure
/// SqlOS rebuilds from scratch are the same set of rows.</item>
/// <item><b>scale</b>: the paper's claim as a test. Per-page cost must not grow with N; the median at the
/// largest scale may be at most <c>maxRatio</c> times the median at the smallest, plus <c>slackMilliseconds</c>
/// for sub-millisecond noise. An O(N) plan regression fails this by orders of magnitude.</item>
/// <item><b>regression</b>: the current function against the previous release's, on the same data in the same
/// job: the current median may be at most <c>maxRatio</c> times the previous one, plus slack.</item>
/// <item><b>improvement</b>: for the listed row-filter scenarios, the closure page must take at most
/// <c>maxRatio</c> of the row-filter page's time.</item>
/// <item><b>ceiling</b>: an absolute budget per scenario and engine, for regressions that slow every scale
/// alike (a heavier function body, a lost index).</item>
/// </list>
/// Every timing gate compares runs on the same machine, so they hold on shared CI runners.
/// </summary>
internal sealed class GateConfig
{
    [JsonPropertyName("scale")]
    public RatioGate Scale { get; init; } = new();

    [JsonPropertyName("regression")]
    public RatioGate Regression { get; init; } = new() { MaxRatio = 1.15, SlackMilliseconds = 2.0 };

    [JsonPropertyName("improvement")]
    public ImprovementGate Improvement { get; init; } = new();

    [JsonPropertyName("ceilingsMilliseconds")]
    public Dictionary<string, Dictionary<string, double>> CeilingsMilliseconds { get; init; } = new();

    public static GateConfig Load(string path)
        => JsonSerializer.Deserialize<GateConfig>(File.ReadAllText(path))
           ?? throw new InvalidOperationException($"Could not read {path}.");

    internal sealed class RatioGate
    {
        [JsonPropertyName("maxRatio")]
        public double MaxRatio { get; init; } = 2.0;

        [JsonPropertyName("slackMilliseconds")]
        public double SlackMilliseconds { get; init; } = 2.0;

        [JsonPropertyName("exempt")]
        public List<string> Exempt { get; init; } = [];
    }

    internal sealed class ImprovementGate
    {
        /// <summary>Row-filter scenario ids whose closure-page twin must be faster by the ratio.</summary>
        [JsonPropertyName("scenarios")]
        public List<string> Scenarios { get; init; } = [];

        [JsonPropertyName("maxRatio")]
        public double MaxRatio { get; init; } = 0.05;
    }
}

internal static class GateEvaluator
{
    public static List<GateResult> Evaluate(BenchmarkReport report, GateConfig config)
    {
        var results = new List<GateResult>();
        foreach (var step in report.Steps)
        {
            foreach (var scenario in step.Scenarios.Concat(step.Density))
            {
                results.Add(new GateResult(
                    "correctness",
                    $"{scenario.Id} @ {RetailTree.Count(step.Products)}",
                    scenario.Correct,
                    scenario.Correct ? "exact authorized answer" : scenario.CorrectnessDetail ?? "wrong answer"));
            }

            if (step.ClosureCheck is { } check)
            {
                results.Add(new GateResult(
                    "closure",
                    $"closure @ {RetailTree.Count(step.Products)}",
                    check.Agrees,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"loaded {check.LoadedRows:N0} rows, after maintenance {check.RestoredRows:N0}, rebuilt {check.RebuiltRows:N0}; hashes {(check.Agrees ? "equal" : "differ")}")));
            }
        }

        if (report.Steps.Count >= 2)
        {
            var smallest = report.Steps[0];
            var largest = report.Steps[^1];
            foreach (var scenario in largest.Scenarios.Where(s => !config.Scale.Exempt.Contains(s.Id)))
            {
                var baseline = smallest.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
                if (baseline is null || !baseline.FullPage || !scenario.FullPage)
                {
                    // A short page means the scan ran to the end of the table: O(N) by construction, and
                    // only because the catalog is too small for this principal to fill a page.
                    continue;
                }

                var limit = baseline.MedianMs * config.Scale.MaxRatio + config.Scale.SlackMilliseconds;
                results.Add(new GateResult(
                    "scale",
                    scenario.Id,
                    scenario.MedianMs <= limit,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{scenario.MedianMs:F2} ms at {RetailTree.Count(largest.Products)} vs {baseline.MedianMs:F2} ms at {RetailTree.Count(smallest.Products)} (×{scenario.MedianMs / baseline.MedianMs:F2}; limit {limit:F2} ms)")));
            }
        }

        foreach (var step in report.Steps)
        {
            var scale = RetailTree.Count(step.Products);
            foreach (var reference in step.Scenarios.Where(s => s.Kind is "ListReference" or "PointFunctionReference" && s.Baseline is not null))
            {
                var current = step.Scenarios.FirstOrDefault(s => s.Id == reference.Baseline);
                if (current is null || config.Regression.Exempt.Contains(current.Id))
                {
                    continue;
                }

                var limit = reference.MedianMs * config.Regression.MaxRatio + config.Regression.SlackMilliseconds;
                results.Add(new GateResult(
                    "regression",
                    $"{current.Id} @ {scale}",
                    current.MedianMs <= limit,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{current.MedianMs:F2} ms vs {reference.MedianMs:F2} ms with the previous function (×{current.MedianMs / reference.MedianMs:F2}; limit {limit:F2} ms)")));
            }

            foreach (var id in config.Improvement.Scenarios)
            {
                var rowFilter = step.Scenarios.FirstOrDefault(s => s.Id == id);
                var closurePage = step.Scenarios.FirstOrDefault(s => s.Id == "visible." + id);
                if (rowFilter is null || closurePage is null)
                {
                    continue;
                }

                var limit = rowFilter.MedianMs * config.Improvement.MaxRatio;
                results.Add(new GateResult(
                    "improvement",
                    $"{id} @ {scale}",
                    closurePage.MedianMs <= limit,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"closure page {closurePage.MedianMs:F2} ms vs row filter {rowFilter.MedianMs:F2} ms (×{closurePage.MedianMs / rowFilter.MedianMs:F4}; limit {limit:F2} ms)")));
            }
        }

        if (config.CeilingsMilliseconds.TryGetValue(report.Provider, out var ceilings))
        {
            foreach (var step in report.Steps)
            {
                foreach (var scenario in step.Scenarios)
                {
                    if (!ceilings.TryGetValue(scenario.Id, out var ceiling))
                    {
                        continue;
                    }

                    results.Add(new GateResult(
                        "ceiling",
                        $"{scenario.Id} @ {RetailTree.Count(step.Products)}",
                        scenario.MedianMs <= ceiling,
                        string.Create(CultureInfo.InvariantCulture, $"{scenario.MedianMs:F2} ms (ceiling {ceiling:F0} ms)")));
                }
            }
        }

        return results;
    }
}
