using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Scenarios;

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
                var match = step.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
                if (match is null)
                {
                    text.Append(" – |");
                    continue;
                }

                var mark = !match.Correct ? " ❌" : !match.FullPage ? "†" : "";
                text.Append(CultureInfo.InvariantCulture, $" {Milliseconds(match.MedianMs)}{mark} |");
            }

            if (steps.Count > 1)
            {
                var baseline = smallest.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
                text.Append(baseline is null
                    ? " – |"
                    : string.Create(CultureInfo.InvariantCulture, $" ×{scenario.MedianMs / baseline.MedianMs:F2} |"));
            }

            text.Append(CultureInfo.InvariantCulture, $" {(scenario.RowsExamined is { } rows ? rows.ToString("N0", CultureInfo.InvariantCulture) : "–")} |");
            text.Append(CultureInfo.InvariantCulture, $" {(scenario.MicrosecondsPerRowExamined is { } us ? us.ToString("F1", CultureInfo.InvariantCulture) : "–")} |");
            text.AppendLine(showsPlanning
                ? $" {(scenario.ServerPlanningMs is { } planning ? planning.ToString("F2", CultureInfo.InvariantCulture) : "–")} |"
                : "");
        }

        text.AppendLine();
        text.AppendLine("Median milliseconds per query as the application sees it, warm cache; every answer is checked against ground truth. Rows examined and server times come from the actual plan: product rows for a row-filter page, closure entries for a `ListVisibleAsync` page. The `ListVisibleAsync` time is the whole call (subjects, permission, type, page, rows).");
        if (steps.SelectMany(s => s.Scenarios).Any(s => !s.FullPage))
        {
            text.AppendLine("† Fewer authorized rows than the page asks for exist at this scale, so a row-filter scan ran to the end of the table (the O(N) case); excluded from the scale gate.");
        }

        AppendTwins(text, largest, "ListReference", "**Previous function** (regression check, same data, same job)",
            (current, twin) => string.Create(CultureInfo.InvariantCulture, $"{current.Title}: {WithUnit(current.MedianMs)} now vs {WithUnit(twin.MedianMs)} before (×{current.MedianMs / twin.MedianMs:F2})"));
        AppendTwins(text, largest, "PointFunctionReference", null,
            (current, twin) => string.Create(CultureInfo.InvariantCulture, $"{current.Title}: {WithUnit(current.MedianMs)} now vs {WithUnit(twin.MedianMs)} before (×{current.MedianMs / twin.MedianMs:F2})"));
        AppendTwins(text, largest, "Visible", "**`ListVisibleAsync`** (the closure page against the row-filter page)",
            (current, twin) => string.Create(CultureInfo.InvariantCulture,
                $"{current.Title}: {WithUnit(current.MedianMs)} → {WithUnit(twin.MedianMs)} (×{twin.MedianMs / current.MedianMs:F3}{(twin.RowsExamined is { } rows ? $", {rows:N0} closure entries read" : "")})"));

        if (smallest.Density.Count > 0)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture,
                $"**Grant density** · at {RetailTree.Count(smallest.Products)}, with {BenchmarkModel.RootCrowdGrants} other people's grants added to the root:");
            foreach (var dense in smallest.Density)
            {
                var plain = smallest.Scenarios.First(s => "density." + s.Id == dense.Id);
                text.Append(CultureInfo.InvariantCulture,
                    $" {plain.Title}: {WithUnit(plain.MedianMs)} → {WithUnit(dense.MedianMs)} (×{dense.MedianMs / plain.MedianMs:F1});");
            }

            text.Length -= 1;
            text.AppendLine(". The model's per-row bound depends only on the caller's own grants, so ×1 is the target. Reported, not gated.");
        }

        var maintained = steps.FirstOrDefault(s => s.Maintenance.Count > 0);
        if (maintained is not null)
        {
            text.AppendLine();
            text.Append(CultureInfo.InvariantCulture, $"**Closure maintenance** · at {RetailTree.Count(maintained.Products)} ({maintained.ClosureRows:N0} closure rows):");
            foreach (var result in maintained.Maintenance)
            {
                var perRow = result.Id.StartsWith("insert.single", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{result.MillisecondsPerRow:F2} ms each over {result.Rows:N0}")
                    : WithUnit(result.Milliseconds);
                var closure = result.Id.StartsWith("insert", StringComparison.Ordinal) || result.Id.StartsWith("delete", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(result.ClosureRows):N0} closure rows {(result.ClosureRows < 0 ? "removed" : "added")}")
                    : string.Create(CultureInfo.InvariantCulture, $"{result.ClosureRows:N0} closure rows recomputed");
                text.Append(CultureInfo.InvariantCulture, $" {result.Title}: {perRow}, {closure};");
            }

            text.Length -= 1;
            text.Append('.');
            if (maintained.ClosureCheck is { } check)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" Rebuilt from scratch by SqlOS in {Seconds(check.RebuildSeconds)}: {check.RebuiltRows:N0} rows, {(check.Agrees ? "identical to the loaded closure and to the closure after the maintenance pass (count and hash)" : "DIFFERENT from the loaded closure")}.");
            }

            text.AppendLine();
        }

        text.AppendLine();

        text.Append("**Dataset load** ·");
        foreach (var step in steps)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" {RetailTree.Count(step.Products)} products / {step.Resources:N0} resources / {step.ClosureRows:N0} closure rows: {Seconds(step.LoadRowsSeconds)} rows + {Seconds(step.LoadClosureSeconds)} closure + {Seconds(step.LoadIndexesSeconds)} indexes + {Seconds(step.LoadMaintenanceSeconds)} statistics, {step.DatabaseBytes / 1e9:F1} GB ·");
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
            $"<sub>{environment.Cpu} × {environment.ProcessorCount} · {environment.MemoryBytes / 1e9:F0} GB · {environment.OperatingSystem} · {report.Server} · {(environment.Commit is { Length: >= 7 } c ? c[..7] : "local")} · {TimeSpan.FromSeconds(report.DurationSeconds):h\\:mm\\:ss}</sub>");
        return text.ToString();
    }

    /// <summary>One paragraph pairing each twin of <paramref name="kind"/> at the largest scale with its current-path scenario.</summary>
    private static void AppendTwins(StringBuilder text, ScaleStep step, string kind, string? heading, Func<ScenarioResult, ScenarioResult, string> describe)
    {
        var pairs = step.Scenarios
            .Where(s => s.Kind == kind && s.Baseline is not null)
            .Select(twin => (Current: step.Scenarios.FirstOrDefault(s => s.Id == twin.Baseline), Twin: twin))
            .Where(p => p.Current is not null)
            .ToList();
        if (pairs.Count == 0)
        {
            return;
        }

        if (heading is not null)
        {
            text.AppendLine();
            text.Append(heading);
            text.Append(CultureInfo.InvariantCulture, $" · at {RetailTree.Count(step.Products)}:");
        }

        foreach (var (current, twin) in pairs)
        {
            text.Append(' ');
            text.Append(describe(current!, twin));
            text.Append(';');
        }

        text.Length -= 1;
        text.AppendLine(".");
    }

    private static string Selectivity(double value, string kind)
        => kind is not ("List" or "ListReference" or "Visible") ? "–"
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

    private static string WithUnit(double milliseconds)
        => milliseconds >= 10_000 ? Milliseconds(milliseconds) : Milliseconds(milliseconds) + " ms";

    private static string Seconds(double seconds)
        => seconds >= 60
            ? $"{(int)(seconds / 60)}m{(int)(seconds % 60):D2}s"
            : seconds.ToString("F1", CultureInfo.InvariantCulture) + "s";
}
