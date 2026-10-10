using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Scenarios;

namespace SqlOS.Benchmarks.Reporting;

/// <summary>
/// The gates, defined in <c>gates.json</c>:
/// <list type="bullet">
/// <item><b>correctness</b>: every scenario returned exactly the authorized answer.</item>
/// <item><b>lineage</b>: the lineage the loader wrote, the same after the maintenance pass, and the same as
/// SqlOS rebuilds from the resources alone (count and hash).</item>
/// <item><b>scale</b>: per-page cost must follow the work the paper predicts, not N. The median at the
/// largest scale may be at most <c>maxRatio</c> times the median at the smallest, times the growth of the
/// rows the page has to touch, plus <c>slackMilliseconds</c> for sub-millisecond noise. A page through the
/// filter touches k rows at any N, so it has no growth term; a page filtered to a store touches that
/// store's σN rows through its own index.</item>
/// <item><b>regression</b>: every scenario's median against the constant set for it and the engine in
/// <c>regressionMilliseconds</c>: what the scenario costs today, with headroom for runner noise. A change
/// that makes any page or point check slower than that fails the run.</item>
/// </list>
/// </summary>
internal sealed class GateConfig
{
    [JsonPropertyName("scale")]
    public ScaleGate Scale { get; init; } = new();

    [JsonPropertyName("regressionMilliseconds")]
    public Dictionary<string, Dictionary<string, double>> RegressionMilliseconds { get; init; } = new();

    public static GateConfig Load(string path)
        => JsonSerializer.Deserialize<GateConfig>(File.ReadAllText(path))
           ?? throw new InvalidOperationException($"Could not read {path}.");

    internal sealed class ScaleGate
    {
        [JsonPropertyName("maxRatio")]
        public double MaxRatio { get; init; } = 2.0;

        [JsonPropertyName("slackMilliseconds")]
        public double SlackMilliseconds { get; init; } = 2.0;

        [JsonPropertyName("exempt")]
        public List<string> Exempt { get; init; } = [];
    }
}

internal static class GateEvaluator
{
    public static List<GateResult> Evaluate(BenchmarkReport report, GateConfig config)
    {
        var results = new List<GateResult>();
        foreach (var step in report.Steps)
        {
            foreach (var scenario in step.Scenarios.Concat(step.Density).Where(s => !s.TimedOut))
            {
                results.Add(new GateResult(
                    "correctness",
                    $"{scenario.Id} @ {RetailTree.Count(step.Products)}",
                    scenario.Correct,
                    scenario.Correct ? "exact authorized answer" : scenario.CorrectnessDetail ?? "wrong answer"));
            }

            if (step.LineageCheck is { } check)
            {
                results.Add(new GateResult(
                    "lineage",
                    $"lineage @ {RetailTree.Count(step.Products)}",
                    check.Agrees,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"loaded {check.Loaded}, after maintenance {check.Restored}, rebuilt {check.Rebuilt}; hashes {(check.Agrees ? "equal" : "differ")}")));
            }
        }

        if (report.Steps.Count >= 2)
        {
            var smallest = report.Steps[0];
            var largest = report.Steps[^1];
            foreach (var scenario in largest.Scenarios.Where(s => !config.Scale.Exempt.Contains(s.Id)))
            {
                var baseline = smallest.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
                if (baseline is null || !baseline.FullPage || !scenario.FullPage || baseline.TimedOut || scenario.TimedOut)
                {
                    // A short page means the engine read to the end of the table, and only because the catalog
                    // is too small for this principal to fill a page.
                    continue;
                }

                var growth = ExpectedRows(scenario, largest.Products) / ExpectedRows(baseline, smallest.Products);
                var limit = baseline.MedianMs * config.Scale.MaxRatio * growth + config.Scale.SlackMilliseconds;
                results.Add(new GateResult(
                    "scale",
                    scenario.Id,
                    scenario.MedianMs <= limit,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{scenario.MedianMs:F2} ms at {RetailTree.Count(largest.Products)} vs {baseline.MedianMs:F2} ms at {RetailTree.Count(smallest.Products)} (×{scenario.MedianMs / baseline.MedianMs:F2}; predicted work ×{growth:F1}; limit {limit:F2} ms)")));
            }
        }

        if (config.RegressionMilliseconds.TryGetValue(report.Provider, out var limits))
        {
            foreach (var step in report.Steps)
            {
                foreach (var scenario in step.Scenarios)
                {
                    if (!limits.TryGetValue(scenario.Id, out var limit))
                    {
                        continue;
                    }

                    results.Add(new GateResult(
                        "regression",
                        $"{scenario.Id} @ {RetailTree.Count(step.Products)}",
                        scenario.MedianMs <= limit,
                        string.Create(CultureInfo.InvariantCulture, $"{scenario.MedianMs:F2} ms (limit {limit:F0} ms)")));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// The rows a page has to touch at a catalog of <paramref name="products"/> rows, from the paper: a page
    /// through the filter k, a page filtered to one store that store's σN rows. Point checks touch one row.
    /// </summary>
    internal static double ExpectedRows(ScenarioResult scenario, long products)
    {
        if (scenario.Kind is not "List")
        {
            return 1;
        }

        var k = scenario.PageSize + 1d;
        return scenario.StoreFiltered ? Math.Max(k, scenario.Selectivity * products) : k;
    }
}
