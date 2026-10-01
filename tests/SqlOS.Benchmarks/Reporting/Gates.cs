using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Reporting;

/// <summary>
/// Regression gates, defined in <c>gates.json</c>:
/// <list type="bullet">
/// <item><b>correctness</b>: every scenario returned exactly the authorized answer.</item>
/// <item><b>lineage</b>: the lineage and scope columns the loader wrote, the same after the maintenance pass,
/// and the same as SqlOS rebuilds from the resources alone (count and hash).</item>
/// <item><b>scale</b>: per-page cost must not grow with N. The median at the largest scale may be at most
/// <c>maxRatio</c> times the median at the smallest, plus <c>slackMilliseconds</c> for sub-millisecond
/// noise. A lineage page costs the cheaper of two plans (the caller's scope, or the rows in the requested
/// order), and the optimizer may switch between them as the table grows, so the ratio leaves room for that
/// switch. The scope-columns pages have their own, tighter ratio (<c>scopedMaxRatio</c>): a single-grant
/// caller's page is one index seek, so it must not move with N at all.</item>
/// <item><b>regression</b>: the current filter against the previous release's, on the same data in the same
/// job, for both the lineage and the scope-columns pages: the median may be at most <c>maxRatio</c> times
/// the previous one, plus slack.</item>
/// <item><b>improvement</b>: for the listed sparse pages, the lineage page must take at most
/// <c>lineageMaxRatio</c> of the previous function's time, and the scope-columns page at most
/// <c>scopedMaxRatio</c>.</item>
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

    internal sealed class ScaleGate : RatioGate
    {
        /// <summary>The ratio for the scope-columns pages, whose cost must not follow N.</summary>
        [JsonPropertyName("scopedMaxRatio")]
        public double ScopedMaxRatio { get; init; } = 1.5;
    }

    internal sealed class ImprovementGate
    {
        /// <summary>Lineage scenario ids whose twins must beat the previous function by the ratios.</summary>
        [JsonPropertyName("scenarios")]
        public List<string> Scenarios { get; init; } = [];

        [JsonPropertyName("lineageMaxRatio")]
        public double LineageMaxRatio { get; init; } = 0.2;

        [JsonPropertyName("scopedMaxRatio")]
        public double ScopedMaxRatio { get; init; } = 0.05;
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

                var ratio = scenario.Kind == "ListScoped" ? config.Scale.ScopedMaxRatio : config.Scale.MaxRatio;
                var limit = baseline.MedianMs * ratio + config.Scale.SlackMilliseconds;
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
                foreach (var id in new[] { reference.Baseline!, "scoped." + reference.Baseline })
                {
                    var current = step.Scenarios.FirstOrDefault(s => s.Id == id);
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

                foreach (var (twinId, ratio, name) in new[] { (id, config.Improvement.LineageMaxRatio, "lineage page"), ("scoped." + id, config.Improvement.ScopedMaxRatio, "scope-columns page") })
                {
                    var twin = step.Scenarios.FirstOrDefault(s => s.Id == twinId);
                    if (twin is null)
                    {
                        continue;
                    }

                    var limit = previous.MedianMs * ratio;
                    results.Add(new GateResult(
                        "improvement",
                        $"{twinId} @ {scale}",
                        twin.MedianMs <= limit,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{name} {twin.MedianMs:F2} ms vs previous function {previous.MedianMs:F2} ms (×{twin.MedianMs / previous.MedianMs:F4}; limit {limit:F2} ms)")));
                }
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
