using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Scenarios;

namespace SqlOS.Benchmarks.Reporting;

/// <summary>
/// Regression gates, defined in <c>gates.json</c>:
/// <list type="bullet">
/// <item><b>correctness</b>: every scenario returned exactly the authorized answer.</item>
/// <item><b>lineage</b>: the lineage and scope columns the loader wrote, the same after the maintenance pass,
/// and the same as SqlOS rebuilds from the resources alone (count and hash).</item>
/// <item><b>scale</b>: per-page cost must follow the work the paper predicts, not N. The median at the
/// largest scale may be at most <c>maxRatio</c> times the median at the smallest, times the growth of the
/// rows the page has to touch, plus <c>slackMilliseconds</c> for sub-millisecond noise. A page through the
/// scope column touches k rows at any N (one index seek), so it has no growth term; a page filtered to a
/// store touches that store's σN rows through its own index. The previous function's pages are measured at
/// every scale but not gated: how its cost grows is a finding about it (its per-row walk gets dearer as the
/// tree outgrows memory), not a regression of the current filter.</item>
/// <item><b>regression</b>: the current filter against the previous release's, on the same data in the same
/// job: the median may be at most <c>maxRatio</c> times the previous one, plus slack.</item>
/// <item><b>improvement</b>: for the listed sparse pages, the current filter must take at most
/// <c>maxRatio</c> of the previous function's time.</item>
/// <item><b>ceiling</b>: an absolute budget per scenario and engine, for regressions that slow every scale
/// alike (a heavier function body, a lost index).</item>
/// </list>
/// Every timing gate compares runs on the same machine, so they hold on shared CI runners.
/// </summary>
internal sealed class GateConfig
{
    [JsonPropertyName("scale")]
    public ScaleGate Scale { get; init; } = new();

    [JsonPropertyName("regression")]
    public RatioGate Regression { get; init; } = new() { MaxRatio = 1.15, SlackMilliseconds = 2.0 };

    [JsonPropertyName("improvement")]
    public ImprovementGate Improvement { get; init; } = new();

    [JsonPropertyName("ceilingsMilliseconds")]
    public Dictionary<string, Dictionary<string, double>> CeilingsMilliseconds { get; init; } = new();

    public static GateConfig Load(string path)
        => JsonSerializer.Deserialize<GateConfig>(File.ReadAllText(path))
           ?? throw new InvalidOperationException($"Could not read {path}.");

    internal class RatioGate
    {
        [JsonPropertyName("maxRatio")]
        public double MaxRatio { get; init; } = 2.0;

        [JsonPropertyName("slackMilliseconds")]
        public double SlackMilliseconds { get; init; } = 2.0;

        [JsonPropertyName("exempt")]
        public List<string> Exempt { get; init; } = [];
    }

    internal sealed class ScaleGate : RatioGate;

    internal sealed class ImprovementGate
    {
        /// <summary>Scenario ids that must beat their previous-function twin by the ratio.</summary>
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
            foreach (var scenario in largest.Scenarios.Where(s => !config.Scale.Exempt.Contains(s.Id) && !IsReference(s)))
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

        foreach (var step in report.Steps)
        {
            var scale = RetailTree.Count(step.Products);
            foreach (var reference in step.Scenarios.Where(s => s.Kind is "ListReference" or "PointFunctionReference" && s.Baseline is not null))
            {
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
            }

            foreach (var id in config.Improvement.Scenarios)
            {
                var previous = step.Scenarios.FirstOrDefault(s => s.Id == "reference." + id);
                if (previous is null)
                {
                    continue;
                }

                var current = step.Scenarios.FirstOrDefault(s => s.Id == id);
                if (current is null)
                {
                    continue;
                }

                var limit = previous.MedianMs * config.Improvement.MaxRatio;
                results.Add(new GateResult(
                    "improvement",
                    $"{id} @ {scale}",
                    current.MedianMs <= limit,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{current.MedianMs:F2} ms vs previous function {previous.MedianMs:F2} ms (×{current.MedianMs / previous.MedianMs:F4}; limit {limit:F2} ms)")));
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

    /// <summary>The previous release's function, measured for comparison.</summary>
    private static bool IsReference(ScenarioResult scenario)
        => scenario.Kind is "ListReference" or "PointFunctionReference";

    /// <summary>
    /// The rows a page has to touch at a catalog of <paramref name="products"/> rows, from the paper: a page
    /// through the scope column k, the previous function min(k / σ, N), a page filtered to one store that
    /// store's σN rows. Point checks touch one row.
    /// </summary>
    internal static double ExpectedRows(ScenarioResult scenario, long products)
    {
        if (!scenario.Kind.StartsWith("List", StringComparison.Ordinal))
        {
            return 1;
        }

        var k = scenario.PageSize + 1d;
        var scope = scenario.Selectivity * products;
        if (scenario.StoreFiltered)
        {
            return Math.Max(k, scope);
        }

        return scenario.Kind == "ListReference" ? Math.Min(k / scenario.Selectivity, products) : k;
    }
}
