using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Infrastructure;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Services;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// Runs each scenario the way an application request would: a fresh context, the public FGA service, and the
/// query EF Core builds from it. Only the authorized query itself is timed (for <c>ListVisibleAsync</c>, the
/// whole call, which is the query). Every result is checked against ground truth computed independently from
/// the dataset generator, so a fast wrong answer fails the run.
/// </summary>
internal sealed class ScenarioRunner(
    Func<BenchDbContext> createContext,
    DatabaseProvider provider,
    SqlOSFgaOptions fga,
    RetailTree tree,
    string planDirectory,
    Log log)
{
    private const string ProductPermissionId = "perm_product_view";
    private readonly PlanCapture _plans = new(provider);

    public async Task<List<ScenarioResult>> RunAsync(IReadOnlyList<Scenario> scenarios, long productCount, CancellationToken cancellationToken)
    {
        // One pass over every query shape first, so no scenario is timed while EF Core compiles its query or
        // the engine reads freshly rebuilt index pages for the first time. The sparse scans warm themselves below.
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

    /// <summary>The row-filter page of a sparse principal: k / σ rows examined, minutes on SQL Server.</summary>
    internal static bool IsSparseScan(Scenario scenario)
        => scenario.Kind is ScenarioKind.List or ScenarioKind.ListReference
           && scenario.StoreId is null
           && scenario.Selectivity < 0.001;

    private async Task<ScenarioResult> RunAsync(Scenario scenario, long productCount, CancellationToken cancellationToken)
    {
        // The first execution is untimed: its answer is the one verified, and for page scenarios the same
        // command is run once more under EXPLAIN ANALYZE / STATISTICS XML for rows examined and server time.
        var capture = scenario.Kind is ScenarioKind.List or ScenarioKind.ListReference ? new CapturedPlan { Relation = "Products" } : null;
        var first = await ExecuteAsync(scenario, cancellationToken, capture);
        var (correct, fullPage, detail) = Verify(scenario, first, productCount);

        // The closure page runs outside EF Core, so its plan is captured from the same command the service builds.
        var captured = scenario.Kind == ScenarioKind.Visible ? await ExplainVisiblePageAsync(scenario, cancellationToken) : capture;
        var plan = captured is null ? null : await SavePlanAsync(scenario, captured, productCount, cancellationToken);

        // The captured run executed the query twice (the plan, then EF's own execution).
        var estimate = capture is null ? first.Elapsed.TotalMilliseconds : first.Elapsed.TotalMilliseconds / 2;
        var warmups = estimate switch { < 50 => 4, < 1_000 => 2, _ => 0 };
        for (var i = 0; i < warmups; i++)
        {
            await ExecuteAsync(scenario, cancellationToken);
        }

        // Queries that take tens of seconds (the sparse scans) are timed once; their spread is small relative
        // to their length, and each run costs minutes of CI time on SQL Server.
        var iterations = estimate switch { < 250 => 25, < 2_000 => 7, < 10_000 => 3, _ => 1 };
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
            scenario.Baseline,
            scenario.Selectivity,
            scenario.ProductDepth,
            scenario.IsPage ? scenario.PageSize : 1,
            iterations,
            MedianMs: Percentile(timings, 0.50),
            P95Ms: Percentile(timings, 0.95),
            MinMs: timings[0],
            MaxMs: timings[^1],
            correct,
            fullPage,
            detail,
            plan?.RowsExamined,
            plan?.PlanningMs,
            plan?.ExecutionMs,
            plan?.File);

        log.Info(
            $"  {scenario.Id,-40} p50 {result.MedianMs,9:F2} ms  p95 {result.P95Ms,9:F2} ms  n={iterations,2}" +
            (plan?.RowsExamined is { } examined ? $"  examined {examined:N0}" : "") +
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
            case ScenarioKind.ListReference:
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
                    return new Execution(clock.Elapsed, page, Allowed: null, NextCursor: null);
                }
                finally
                {
                    PlanCapture.Disarm();
                }
            }

            case ScenarioKind.Visible:
            {
                // The whole call: subject resolution, the permission, the type, the closure page, and the
                // entity rows. It is what an application pays for the page.
                var cursor = VisibleCursor(scenario);
                var clock = Stopwatch.StartNew();
                var page = await service.ListVisibleAsync<Product>(
                    scenario.Principal.SubjectId, BenchmarkModel.ProductView, "product", scenario.PageSize, cursor, cancellationToken);
                clock.Stop();
                return new Execution(clock.Elapsed, page.Items.ToList(), Allowed: null, page.NextCursor);
            }

            case ScenarioKind.PointFunction:
            case ScenarioKind.PointFunctionReference:
            {
                var subjects = JsonSerializer.Serialize(scenario.Principal.ResolvedSubjectIds);
                var resourceId = RetailTree.ProductResourceId(scenario.ProductId);
                var clock = Stopwatch.StartNew();
                var allowed = scenario.Kind == ScenarioKind.PointFunction
                    ? await db.IsResourceAccessible(resourceId, subjects, ProductPermissionId).AnyAsync(cancellationToken)
                    : await db.IsResourceAccessibleReference(resourceId, subjects, ProductPermissionId).AnyAsync(cancellationToken);
                clock.Stop();
                return new Execution(clock.Elapsed, Page: null, allowed, NextCursor: null);
            }

            case ScenarioKind.PointApi:
            {
                var resourceId = RetailTree.ProductResourceId(scenario.ProductId);
                var clock = Stopwatch.StartNew();
                var allowed = await service.Allows(scenario.Principal.SubjectId, BenchmarkModel.ProductView, resourceId);
                clock.Stop();
                return new Execution(clock.Elapsed, Page: null, allowed, NextCursor: null);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    /// <summary>The closure page's cursor for the scenario: the sequence number of the product the page starts after.</summary>
    private string? VisibleCursor(Scenario scenario)
        => scenario.Cursor == 0 ? null : tree.ProductSeq(scenario.Cursor).ToString(CultureInfo.InvariantCulture);

    /// <summary>The canonical cursor page: authorized, optionally store-scoped, ordered by Id, k + 1 rows.</summary>
    private static async Task<IQueryable<Product>> BuildListQueryAsync(BenchDbContext db, SqlOSFgaAuthService service, Scenario scenario)
    {
        var cursor = scenario.Cursor;
        IQueryable<Product> query;
        if (scenario.Kind == ScenarioKind.List)
        {
            var authorized = await service.BuildFilterAsync<Product>(scenario.Principal.SubjectId, BenchmarkModel.ProductView);
            query = db.Products.AsNoTracking().Where(authorized);
        }
        else
        {
            // The previous release's function, with the subject set BuildFilterAsync resolves for this person.
            var subjects = JsonSerializer.Serialize(scenario.Principal.ResolvedSubjectIds);
            query = db.Products.AsNoTracking().Where(p => db.IsResourceAccessibleReference(p.ResourceId, subjects, ProductPermissionId).Any());
        }

        query = query.Where(p => p.Id > cursor);
        if (scenario.StoreId is { } storeId)
        {
            query = query.Where(p => p.StoreId == storeId);
        }

        return query.OrderBy(p => p.Id).Take(scenario.PageSize + 1);
    }

    /// <summary>Explains the closure page exactly as <c>ListVisibleAsync</c> issues it, counting closure entries read.</summary>
    private async Task<CapturedPlan> ExplainVisiblePageAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        await using var db = createContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        var sql = SqlOSDatabase.Resolve(db.Database);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.BuildVisibleResourcesPageSql(fga);
        command.Parameters.Add(sql.CreateParameter("@SubjectIds", JsonSerializer.Serialize(scenario.Principal.ResolvedSubjectIds)));
        command.Parameters.Add(sql.CreateParameter("@PermissionId", ProductPermissionId));
        command.Parameters.Add(sql.CreateParameter("@ResourceTypeId", "product"));
        command.Parameters.Add(sql.CreateParameter("@Cursor", scenario.Cursor == 0 ? 0L : tree.ProductSeq(scenario.Cursor)));
        command.Parameters.Add(sql.CreateParameter("@PageSize", scenario.PageSize));
        return await _plans.ExplainAsync(command, SqlOSFgaResourceClosure.TableName(fga), cancellationToken);
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
        return new PlanSummary(plan.RowsExamined, plan.PlanningMs, plan.ExecutionMs, Path.GetFileName(file));
    }

    /// <summary>
    /// Checks the answer against ground truth. A page shorter than requested is still correct when it matches,
    /// but for the row-filter scenarios it means the scan ran to the end of the table (the O(N) case), so the
    /// scale gate ignores it.
    /// </summary>
    private (bool Correct, bool FullPage, string? Detail) Verify(Scenario scenario, Execution execution, long productCount)
    {
        if (!scenario.IsPage)
        {
            return execution.Allowed == scenario.ExpectAllowed
                ? (true, true, null)
                : (false, true, $"expected {(scenario.ExpectAllowed ? "allowed" : "denied")}, got {(execution.Allowed == true ? "allowed" : "denied")}");
        }

        // Row-filter pages fetch k + 1 rows (the application's "has more"); the closure page fetches k and
        // returns a cursor. Product sequence numbers follow product ids, so both orders are the id order.
        var rows = scenario.Kind == ScenarioKind.Visible ? scenario.PageSize : scenario.PageSize + 1;
        var expected = new List<int>(rows);
        for (long id = scenario.Cursor + 1; id <= productCount && expected.Count < rows; id++)
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

        if (scenario.Kind == ScenarioKind.Visible)
        {
            var expectedCursor = expected.Count == scenario.PageSize
                ? tree.ProductSeq(expected[^1]).ToString(CultureInfo.InvariantCulture)
                : null;
            if (execution.NextCursor != expectedCursor)
            {
                return (false, false, $"cursor {execution.NextCursor ?? "null"} != expected {expectedCursor ?? "null"}");
            }
        }

        return expected.Count == rows
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

    private sealed record Execution(TimeSpan Elapsed, List<Product>? Page, bool? Allowed, string? NextCursor);

    private sealed record PlanSummary(long? RowsExamined, double? PlanningMs, double? ExecutionMs, string File);
}

/// <param name="Baseline">For a twin scenario, the id of the current-path scenario it is compared with.</param>
/// <param name="RowsExamined">
/// Rows the plan read from the relation that bounds the query: product rows for a row-filter page, closure
/// entries for a closure page.
/// </param>
internal sealed record ScenarioResult(
    string Id,
    string Title,
    string Kind,
    string? Baseline,
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
    long? RowsExamined,
    double? ServerPlanningMs,
    double? ServerExecutionMs,
    string? PlanFile)
{
    /// <summary>
    /// Server execution time per row examined, in microseconds: the paper's per-row constant, without
    /// planning, network, or EF Core overhead.
    /// </summary>
    public double? MicrosecondsPerRowExamined
        => RowsExamined is > 0 && ServerExecutionMs is { } execution
            ? execution * 1000 / RowsExamined.Value
            : null;
}
