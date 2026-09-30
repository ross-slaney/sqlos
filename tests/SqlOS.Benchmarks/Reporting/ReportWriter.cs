using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlOS.Benchmarks.Data;

namespace SqlOS.Benchmarks.Reporting;

internal static class ReportWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task<string> WriteAsync(BenchmarkReport report, string directory, string? summaryPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(report, Json), cancellationToken);

        var markdown = Markdown(report);
        await File.WriteAllTextAsync(Path.Combine(directory, "summary.md"), markdown, cancellationToken);
        if (summaryPath is not null)
        {
            await File.AppendAllTextAsync(summaryPath, markdown + Environment.NewLine, cancellationToken);
        }

        return markdown;
    }

    public static string Markdown(BenchmarkReport report)
    {
        var text = new StringBuilder();
        var steps = report.Steps;
        var smallest = steps[0];
        var largest = steps[^1];

        text.AppendLine(CultureInfo.InvariantCulture, $"### SHRBAC benchmarks · {report.Engine}");
        text.AppendLine();
        text.AppendLine(report.Dataset.Description);
        text.AppendLine();

        text.Append("| Scenario | σ |");
        foreach (var step in steps)
        {
            text.Append(CultureInfo.InvariantCulture, $" {RetailTree.Count(step.Products)} |");
        }

        if (steps.Count > 1)
        {
            text.Append(CultureInfo.InvariantCulture, $" {RetailTree.Count(largest.Products)} ÷ {RetailTree.Count(smallest.Products)} |");
        }

        var showsPlanning = largest.Scenarios.Any(s => s.ServerPlanningMs is not null);
        text.Append(CultureInfo.InvariantCulture, $" rows examined @ {RetailTree.Count(largest.Products)} | server µs per row |");
        text.AppendLine(showsPlanning ? " server planning ms |" : "");
        text.Append("|---|---:|");
        text.Append(string.Concat(Enumerable.Repeat("---:|", steps.Count + (steps.Count > 1 ? 1 : 0) + 2 + (showsPlanning ? 1 : 0))));
        text.AppendLine();

        foreach (var scenario in largest.Scenarios)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {scenario.Title} | {Selectivity(scenario.Selectivity, scenario.Kind)} |");
            foreach (var step in steps)
            {
                var match = step.Scenarios.First(s => s.Id == scenario.Id);
                var mark = !match.Correct ? " ❌" : !match.FullPage ? "†" : "";
                text.Append(CultureInfo.InvariantCulture, $" {Milliseconds(match.MedianMs)}{mark} |");
            }

            if (steps.Count > 1)
            {
                var baseline = smallest.Scenarios.First(s => s.Id == scenario.Id);
                text.Append(CultureInfo.InvariantCulture, $" ×{scenario.MedianMs / baseline.MedianMs:F2} |");
            }

            text.Append(CultureInfo.InvariantCulture, $" {(scenario.ProductRowsExamined is { } rows ? rows.ToString("N0", CultureInfo.InvariantCulture) : "–")} |");
            text.Append(CultureInfo.InvariantCulture, $" {(scenario.MicrosecondsPerRowExamined is { } us ? us.ToString("F1", CultureInfo.InvariantCulture) : "–")} |");
            text.AppendLine(showsPlanning
                ? $" {(scenario.ServerPlanningMs is { } planning ? planning.ToString("F2", CultureInfo.InvariantCulture) : "–")} |"
                : "");
        }

        text.AppendLine();
        text.AppendLine("Median milliseconds per query as the application sees it, warm cache; every answer is checked against ground truth. Rows examined and server times come from the actual plan.");
        if (steps.SelectMany(s => s.Scenarios).Any(s => !s.FullPage))
        {
            text.AppendLine("† Fewer than k + 1 authorized rows exist at this scale, so the scan ran to the end of the table (the O(N) case); excluded from the scale gate.");
        }

        if (smallest.Density.Count > 0)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture,
                $"**Grant density** · at {RetailTree.Count(smallest.Products)}, with {BenchmarkModel.RootCrowdGrants} other people's grants added to the root:");
            foreach (var dense in smallest.Density)
            {
                var plain = smallest.Scenarios.First(s => "density." + s.Id == dense.Id);
                text.Append(CultureInfo.InvariantCulture,
                    $" {plain.Title}: {Milliseconds(plain.MedianMs)} → {Milliseconds(dense.MedianMs)} ms (×{dense.MedianMs / plain.MedianMs:F1});");
            }

            text.Length -= 1;
            text.AppendLine(". The model's per-row bound depends only on the caller's own grants, so ×1 is the target. Reported, not gated.");
        }

        text.AppendLine();

        text.Append("**Dataset load** ·");
        foreach (var step in steps)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" {RetailTree.Count(step.Products)} products / {step.Resources:N0} resources: {Seconds(step.LoadRowsSeconds)} rows + {Seconds(step.LoadIndexesSeconds)} indexes + {Seconds(step.LoadMaintenanceSeconds)} statistics, {step.DatabaseBytes / 1e9:F1} GB ·");
        }

        text.Length -= 2;
        text.AppendLine();
        text.AppendLine();

        var gates = report.Gates.GroupBy(g => g.Gate).ToList();
        text.Append("**Gates** ·");
        foreach (var gate in gates)
        {
            var passed = gate.Count(g => g.Passed);
            text.Append(CultureInfo.InvariantCulture, $" {(passed == gate.Count() ? "✅" : "❌")} {gate.Key} {passed}/{gate.Count()} ·");
        }

        text.Length -= 2;
        text.AppendLine();

        var failures = report.Gates.Where(g => !g.Passed).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("| Failed gate | Subject | Detail |");
            text.AppendLine("|---|---|---|");
            foreach (var failure in failures)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {failure.Gate} | {failure.Subject} | {failure.Detail} |");
            }
        }

        text.AppendLine();
        var environment = report.Environment;
        text.AppendLine(CultureInfo.InvariantCulture,
            $"<sub>{environment.ProcessorCount} CPUs · {environment.MemoryBytes / 1e9:F0} GB · {environment.OperatingSystem} · {report.Server} · {(environment.Commit is { Length: >= 7 } c ? c[..7] : "local")} · {TimeSpan.FromSeconds(report.DurationSeconds):h\\:mm\\:ss}</sub>");
        return text.ToString();
    }

    private static string Selectivity(double value, string kind)
        => kind != "List" ? "–"
            : value >= 0.9999 ? "1"
            : value >= 0.01 ? (value * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%"
            : (value * 100).ToString("0.####", CultureInfo.InvariantCulture) + "%";

    private static string Milliseconds(double value)
        => value switch
        {
            >= 10_000 => (value / 1000).ToString("F1", CultureInfo.InvariantCulture) + " s",
            >= 100 => value.ToString("F0", CultureInfo.InvariantCulture),
            _ => value.ToString("F2", CultureInfo.InvariantCulture),
        };

    private static string Seconds(double seconds)
        => seconds >= 60
            ? $"{(int)(seconds / 60)}m{(int)(seconds % 60):D2}s"
            : seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
}
