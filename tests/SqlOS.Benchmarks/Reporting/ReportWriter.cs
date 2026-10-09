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

        text.AppendLine("**Every scenario**");
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
        text.Append(CultureInfo.InvariantCulture, $" product rows read @ {RetailTree.Count(largest.Products)} | server µs per row |");
        text.AppendLine(showsPlanning ? " server planning ms |" : "");
        text.Append("|---|---:|");
        text.Append(string.Concat(Enumerable.Repeat("---:|", steps.Count + (steps.Count > 1 ? 1 : 0) + 2 + (showsPlanning ? 1 : 0))));
        text.AppendLine();

        foreach (var scenario in largest.Scenarios)
        {
            text.Append(CultureInfo.InvariantCulture, $"| {scenario.Title} | {(IsPage(scenario.Kind) ? Selectivity(scenario.Selectivity) : "–")} |");
            foreach (var step in steps)
            {
                var match = step.Scenarios.FirstOrDefault(s => s.Id == scenario.Id);
                text.Append(CultureInfo.InvariantCulture, $" {Cell(match)} |");
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
        text.AppendLine("Median milliseconds per query as the application sees it, warm cache; every answer is checked against ground truth. Product rows read and server times come from the actual plan of a filter page; a page call runs several statements and reports its own counters below.");
        if (steps.SelectMany(s => s.Scenarios).Any(s => !s.FullPage && !s.TimedOut))
        {
            text.AppendLine("† Fewer authorized rows than the page asks for exist at this scale, so the engine read to the end of the table; excluded from the scale gate.");
        }

        if (steps.SelectMany(s => s.Scenarios).Any(s => s.TimedOut))
        {
            text.AppendLine("‡ Did not finish within the run's budget for one query; counted at the budget, a lower bound, in every ratio.");
        }

        AppendFilterVersusPage(text, steps);

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
            text.Append(CultureInfo.InvariantCulture, $"**Lineage maintenance** · at {RetailTree.Count(maintained.Products)}:");
            foreach (var result in maintained.Maintenance)
            {
                var cost = result.Id.StartsWith("insert.single", StringComparison.Ordinal) || result.Id.StartsWith("product.", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{result.MillisecondsPerRow:F2} ms each over {result.Rows:N0}")
                    : WithUnit(result.Milliseconds);
                var lineage = result.Id.StartsWith("product.", StringComparison.Ordinal)
                    ? "application rows; resource creation measured separately"
                    : result.Id.StartsWith("delete", StringComparison.Ordinal)
                    ? string.Create(CultureInfo.InvariantCulture, $"{result.Rows:N0} rows removed")
                    : string.Create(CultureInfo.InvariantCulture, $"{result.LineageRows:N0} lineages computed");
                text.Append(CultureInfo.InvariantCulture, $" {result.Title}: {cost}, {lineage};");
            }

            text.Length -= 1;
            text.Append('.');
            if (maintained.LineageCheck is { } check)
            {
                text.Append(CultureInfo.InvariantCulture,
                    $" Rebuilt from scratch by SqlOS in {Seconds(check.RebuildSeconds)}: {(check.Agrees ? "identical to the loaded lineage and scope columns and to those after the maintenance pass (counts and hashes)" : $"DIFFERENT (loaded {check.Loaded}, after maintenance {check.Restored}, rebuilt {check.Rebuilt})")}.");
            }

            text.AppendLine();
        }

        text.AppendLine();

        text.Append("**Dataset load** ·");
        foreach (var step in steps)
        {
            text.Append(CultureInfo.InvariantCulture,
                $" {RetailTree.Count(step.Products)} products / {step.Resources:N0} resources: {Seconds(step.LoadRowsSeconds)} rows + {Seconds(step.LoadIndexesSeconds)} indexes + {Seconds(step.LoadMaintenanceSeconds)} statistics + {Seconds(step.PageIndexSeconds)} grant counts and direct indexes, {step.DatabaseBytes / 1e9:F1} GB ·");
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

    /// <summary>
    /// The same page both ways, side by side: through <c>BuildFilterAsync</c> and through the page call, with
    /// what the page call did at the largest scale (round trips, statements, index rows fetched, streams opened).
    /// </summary>
    private static void AppendFilterVersusPage(StringBuilder text, IReadOnlyList<ScaleStep> steps)
    {
        var largest = steps[^1];
        var pages = largest.Scenarios.Where(s => s.Kind == "Page").ToList();
        if (pages.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture,
            $"**Filter vs. walk** · the same page over `BuildFilterAsync`'s filter, as one statement the optimizer plans (filter: the page written as a projection ordered after `Select`, the caller's access roots read when it runs) and as the page SqlOS walks (page). Median ms of the query alone per scale; at {RetailTree.Count(largest.Products)}, the whole request (BuildFilterAsync + query) and what each path read before its rows");
        text.AppendLine();
        text.Append("| Page | σ |");
        foreach (var step in steps)
        {
            text.Append(CultureInfo.InvariantCulture, $" {RetailTree.Count(step.Products)} filter | {RetailTree.Count(step.Products)} page |");
        }

        text.AppendLine(" request: filter (prepare + query) · page (prepare + query) | before the rows: filter roots read (ms) · page rounds / statements / rows fetched / streams · ms walk + load |");
        text.Append("|---|---:|");
        text.Append(string.Concat(Enumerable.Repeat("---:|", steps.Count * 2)));
        text.AppendLine("---|---|");

        foreach (var page in pages)
        {
            var twinId = "list." + page.Id["page.".Length..];
            var twin = largest.Scenarios.FirstOrDefault(s => s.Id == twinId);
            text.Append(CultureInfo.InvariantCulture, $"| {page.Title} | {Selectivity(page.Selectivity)} |");
            foreach (var step in steps)
            {
                text.Append(CultureInfo.InvariantCulture, $" {Cell(step.Scenarios.FirstOrDefault(s => s.Id == twinId))} | {Cell(step.Scenarios.FirstOrDefault(s => s.Id == page.Id))} |");
            }

            text.Append(CultureInfo.InvariantCulture, $" {Request(twin)} · {Request(page)} |");
            text.Append(CultureInfo.InvariantCulture, $" {(twin?.RootsFetched is { } roots ? string.Create(CultureInfo.InvariantCulture, $"{roots:N0} roots ({twin.RootsMs:F1} ms)") : "–")} · ");
            text.AppendLine(page.Rounds is null
                ? "– |"
                : string.Create(CultureInfo.InvariantCulture, $"{page.Rounds} / {page.Statements} / {page.RowsFetched:N0} / {page.Streams} · {page.WalkMs:F1} + {page.LoadMs:F1} |"));
        }
    }

    private static string Request(ScenarioResult? result)
        => result is { RequestMs: { } request, PrepareMs: { } prepare } && !result.TimedOut
            ? string.Create(CultureInfo.InvariantCulture, $"{Milliseconds(request)} ({prepare:F1} + {Milliseconds(result.MedianMs)})")
            : "–";

    private static string Cell(ScenarioResult? result)
    {
        if (result is null)
        {
            return "–";
        }

        if (result.TimedOut)
        {
            return string.Create(CultureInfo.InvariantCulture, $"> {result.MedianMs / 1_000:F0} s‡");
        }

        var mark = !result.Correct ? " ❌" : !result.FullPage ? "†" : "";
        return Milliseconds(result.MedianMs) + mark;
    }

    private static bool IsPage(string kind) => kind is "List" or "Page";

    private static string Selectivity(double value)
        => value >= 0.9999 ? "1"
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
