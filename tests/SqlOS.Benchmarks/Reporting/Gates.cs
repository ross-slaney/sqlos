using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SqlOS.Benchmarks.Reporting;

/// <summary>
/// Regression gates, defined in <c>gates.json</c>:
/// <list type="bullet">
/// <item><b>correctness</b>: every scenario returned exactly the authorized answer.</item>
/// <item><b>scale</b>: the paper's claim as a test. Per-page cost must not grow with N; the median at the
/// largest scale may be at most <c>maxRatio</c> times the median at the smallest, plus <c>slackMilliseconds</c>
/// for sub-millisecond noise. An O(N) plan regression fails this by orders of magnitude.</item>
/// <item><b>ceiling</b>: an absolute budget per scenario and engine, for regressions that slow every scale
/// alike (a heavier function body, a lost index).</item>
/// </list>
/// Both timing gates compare runs on the same machine, so they hold on shared CI runners.
/// </summary>
internal sealed class GateConfig
{
    [JsonPropertyName("scale")]
    public ScaleGate Scale { get; init; } = new();

    [JsonPropertyName("ceilingsMilliseconds")]
    public Dictionary<string, Dictionary<string, double>> CeilingsMilliseconds { get; init; } = new();

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
            foreach (var scenario in step.Scenarios.Concat(step.Density))
            {
                results.Add(new GateResult(
                    "correctness",
                    $"{scenario.Id} @ {Data.RetailTree.Count(step.Products)}",
                    scenario.Correct,
                    scenario.Correct ? "exact authorized answer" : scenario.CorrectnessDetail ?? "wrong answer"));
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
                        $"{scenario.MedianMs:F2} ms at {Data.RetailTree.Count(largest.Products)} vs {baseline.MedianMs:F2} ms at {Data.RetailTree.Count(smallest.Products)} (×{scenario.MedianMs / baseline.MedianMs:F2}; limit {limit:F2} ms)")));
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
                        $"{scenario.Id} @ {Data.RetailTree.Count(step.Products)}",
                        scenario.MedianMs <= ceiling,
                        string.Create(CultureInfo.InvariantCulture, $"{scenario.MedianMs:F2} ms (ceiling {ceiling:F0} ms)")));
                }
            }
        }

        return results;
    }
}
