using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Infrastructure;
using SqlOS.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// Runs each scenario the way an application request would: a fresh context, the public FGA service, and the
/// query EF Core builds from it. Only the authorized query itself is timed. Every result is checked against
/// ground truth computed independently from the dataset generator, so a fast wrong answer fails the run.
/// </summary>
internal sealed class ScenarioRunner(
    Func<BenchDbContext> createContext,
    SqlOSFgaOptions fga,
    RetailTree tree,
    string planDirectory,
    Log log)
{
    public async Task<List<ScenarioResult>> RunAsync(IReadOnlyList<Scenario> scenarios, long productCount, CancellationToken cancellationToken)
    {
        // One pass over every query shape first, so no scenario is timed while EF Core compiles its query or
        // the engine reads freshly rebuilt index pages for the first time. The sparse scan warms itself below.
        foreach (var scenario in scenarios.Where(s => !IsSparseScan(s)))
        {
            await ExecuteAsync(scenario, cancellationToken);
        }

        var results = new List<ScenarioResult>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            results.Add(await RunAsync(scenario, productCount, cancellationToken));
        }

        return results;
    }

    private static bool IsSparseScan(Scenario scenario)
        => scenario.Kind == ScenarioKind.List && scenario.StoreId is null && scenario.Selectivity < 0.001;

    private async Task<ScenarioResult> RunAsync(Scenario scenario, long productCount, CancellationToken cancellationToken)
    {
        // The first execution is untimed: its answer is the one verified, and for list scenarios the same
        // command is run once more under EXPLAIN ANALYZE / STATISTICS XML for rows examined and server time.
        var capture = scenario.Kind == ScenarioKind.List ? new CapturedPlan() : null;
        var first = await ExecuteAsync(scenario, cancellationToken, capture);
        var (correct, fullPage, detail) = Verify(scenario, first, productCount);
        var plan = capture is null ? null : await SavePlanAsync(scenario, capture, productCount, cancellationToken);

        // The captured run executed the query twice (the plan, then EF's own execution).
        var estimate = capture is null ? first.Elapsed.TotalMilliseconds : first.Elapsed.TotalMilliseconds / 2;
        var warmups = estimate switch { < 50 => 4, < 1_000 => 2, _ => 0 };
        for (var i = 0; i < warmups; i++)
        {
            await ExecuteAsync(scenario, cancellationToken);
        }

        var iterations = estimate switch { < 250 => 25, < 2_000 => 7, < 10_000 => 3, _ => 2 };
        var timings = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            timings[i] = (await ExecuteAsync(scenario, cancellationToken)).Elapsed.TotalMilliseconds;
        }

        Array.Sort(timings);

        var result = new ScenarioResult(
            scenario.Id,
            scenario.Title,
            scenario.Kind.ToString(),
            scenario.Selectivity,
            scenario.ProductDepth,
            scenario.Kind == ScenarioKind.List ? scenario.PageSize : 1,
            iterations,
            MedianMs: Percentile(timings, 0.50),
            P95Ms: Percentile(timings, 0.95),
            MinMs: timings[0],
            MaxMs: timings[^1],
            correct,
            fullPage,
            detail,
            plan?.ProductRowsExamined,
            plan?.PlanningMs,
            plan?.ExecutionMs,
            plan?.File);

        log.Info(
            $"  {scenario.Id,-30} p50 {result.MedianMs,9:F2} ms  p95 {result.P95Ms,9:F2} ms  n={iterations,2}" +
            (plan?.ProductRowsExamined is { } examined ? $"  examined {examined:N0}" : "") +
            (plan?.PlanningMs is { } planning ? $"  plan {planning:F2} ms" : "") +
            (plan?.ExecutionMs is { } execution ? $"  exec {execution:F2} ms" : "") +
            (correct ? "" : $"  WRONG: {detail}") +
            (fullPage ? "" : "  (partial page)"));
        return result;
    }

    private async Task<Execution> ExecuteAsync(Scenario scenario, CancellationToken cancellationToken, CapturedPlan? capture = null)
    {
        await using var db = createContext();
        var service = new SqlOSFgaAuthService(db, Options.Create(fga), NullLogger<SqlOSFgaAuthService>.Instance);
        switch (scenario.Kind)
        {
            case ScenarioKind.List:
            {
                var query = await BuildListQueryAsync(db, service, scenario);

                // Armed only now, after BuildFilterAsync's own lookups, so the plan is the list query's.
                if (capture is not null)
                {
                    PlanCapture.Arm(capture);
                }

                try
                {
                    var clock = Stopwatch.StartNew();
                    var page = await query.ToListAsync(cancellationToken);
                    clock.Stop();
                    return new Execution(clock.Elapsed, page, Allowed: null);
                }
                finally
                {
                    PlanCapture.Disarm();
                }
            }

            case ScenarioKind.PointFunction:
            {
                var subjects = JsonSerializer.Serialize(scenario.Principal.ResolvedSubjectIds);
                var resourceId = RetailTree.ProductResourceId(scenario.ProductId);
                var clock = Stopwatch.StartNew();
                var allowed = await db.IsResourceAccessible(resourceId, subjects, "perm_product_view").AnyAsync(cancellationToken);
                clock.Stop();
                return new Execution(clock.Elapsed, Page: null, allowed);
            }

            case ScenarioKind.PointApi:
            {
                var resourceId = RetailTree.ProductResourceId(scenario.ProductId);
                var clock = Stopwatch.StartNew();
                var allowed = await service.Allows(scenario.Principal.SubjectId, BenchmarkModel.ProductView, resourceId);
                clock.Stop();
                return new Execution(clock.Elapsed, Page: null, allowed);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    /// <summary>The canonical cursor page: authorized, optionally store-scoped, ordered by Id, k + 1 rows.</summary>
    private static async Task<IQueryable<Product>> BuildListQueryAsync(BenchDbContext db, SqlOSFgaAuthService service, Scenario scenario)
    {
        var authorized = await service.BuildFilterAsync<Product>(scenario.Principal.SubjectId, BenchmarkModel.ProductView);
        var cursor = scenario.Cursor;
        var query = db.Products.AsNoTracking().Where(authorized).Where(p => p.Id > cursor);
        if (scenario.StoreId is { } storeId)
        {
            query = query.Where(p => p.StoreId == storeId);
        }

        return query.OrderBy(p => p.Id).Take(scenario.PageSize + 1);
    }

    private async Task<PlanSummary?> SavePlanAsync(Scenario scenario, CapturedPlan plan, long productCount, CancellationToken cancellationToken)
    {
        if (plan.Error is not null || plan.Text is null)
        {
            log.Info($"  (plan capture for {scenario.Id} failed: {plan.Error ?? "no plan returned"})");
            return null;
        }

        Directory.CreateDirectory(planDirectory);
        var file = Path.Combine(planDirectory, $"{RetailTree.Count(productCount)}-{scenario.Id}.{plan.Extension}");
        await File.WriteAllTextAsync(file, plan.Text, cancellationToken);
        return new PlanSummary(plan.ProductRowsExamined, plan.PlanningMs, plan.ExecutionMs, Path.GetFileName(file));
    }

    /// <summary>
    /// Checks the answer against ground truth. A page shorter than k + 1 is still correct when it matches, but
    /// it means the query scanned to the end of the table (the O(N) case), so the scale gate ignores it.
    /// </summary>
    private (bool Correct, bool FullPage, string? Detail) Verify(Scenario scenario, Execution execution, long productCount)
    {
        if (scenario.Kind != ScenarioKind.List)
        {
            return execution.Allowed == scenario.ExpectAllowed
                ? (true, true, null)
                : (false, true, $"expected {(scenario.ExpectAllowed ? "allowed" : "denied")}, got {(execution.Allowed == true ? "allowed" : "denied")}");
        }

        var expected = new List<int>(scenario.PageSize + 1);
        for (long id = scenario.Cursor + 1; id <= productCount && expected.Count <= scenario.PageSize; id++)
        {
            var leaf = tree.LeafOf(id);
            if (leaf.IsUnder(scenario.Principal.ScopeResourceId) && (scenario.StoreId is null || leaf.StoreId == scenario.StoreId))
            {
                expected.Add((int)id);
            }
        }

        var page = execution.Page!;
        var actual = page.Select(p => p.Id).ToList();
        if (!actual.SequenceEqual(expected))
        {
            return (false, false, $"page [{Preview(actual)}] != expected [{Preview(expected)}]");
        }

        foreach (var product in page)
        {
            var leaf = tree.LeafOf(product.Id);
            if (product.StoreId != leaf.StoreId || product.ResourceId != RetailTree.ProductResourceId(product.Id))
            {
                return (false, false, $"product {product.Id} has store {product.StoreId} / {product.ResourceId}, expected {leaf.StoreId}");
            }
        }

        return expected.Count == scenario.PageSize + 1
            ? (true, true, null)
            : (true, false, $"only {expected.Count} authorized rows exist after the cursor at this scale");
    }

    private static string Preview(IReadOnlyList<int> ids)
        => string.Join(", ", ids.Take(4).Select(i => i.ToString(CultureInfo.InvariantCulture))) + (ids.Count > 4 ? $", … ({ids.Count})" : "");

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        var rank = p * (sorted.Length - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        return sorted[low] + (sorted[high] - sorted[low]) * (rank - low);
    }

    private sealed record Execution(TimeSpan Elapsed, List<Product>? Page, bool? Allowed);

    private sealed record PlanSummary(long? ProductRowsExamined, double? PlanningMs, double? ExecutionMs, string File);
}

internal sealed record ScenarioResult(
    string Id,
    string Title,
    string Kind,
    double Selectivity,
    string ProductDepth,
    int PageSize,
    int Iterations,
    double MedianMs,
    double P95Ms,
    double MinMs,
    double MaxMs,
    bool Correct,
    bool FullPage,
    string? CorrectnessDetail,
    long? ProductRowsExamined,
    double? ServerPlanningMs,
    double? ServerExecutionMs,
    string? PlanFile)
{
    /// <summary>
    /// Server execution time per candidate row examined, in microseconds: the paper's per-row constant,
    /// without planning, network, or EF Core overhead.
    /// </summary>
    public double? MicrosecondsPerRowExamined
        => ProductRowsExamined is > 0 && ServerExecutionMs is { } execution
            ? execution * 1000 / ProductRowsExamined.Value
            : null;
}
