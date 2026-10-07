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
using SqlOS.Fga.Paging;
using SqlOS.Fga.Services;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// Runs each scenario the way an application request would: a fresh context, the public FGA service, and the
/// query EF Core builds from it. Only the authorized query itself is timed. Every result is checked against
/// ground truth computed independently from the dataset generator, so a fast wrong answer fails the run.
/// </summary>
internal sealed class ScenarioRunner(
    Func<BenchDbContext> createContext,
    DatabaseProvider provider,
    SqlOSFgaOptions fga,
    RetailTree tree,
    string planDirectory,
    int budgetSeconds,
    Log log)
{
    private const string ProductPermissionId = "perm_product_view";
    private readonly PlanCapture _plans = new(provider);

    public async Task<List<ScenarioResult>> RunAsync(IReadOnlyList<Scenario> scenarios, long productCount, CancellationToken cancellationToken)
    {
        // One pass over every query shape first, so no scenario is timed while EF Core compiles its query or
        // the engine reads freshly rebuilt index pages for the first time.
        foreach (var scenario in scenarios)
        {
            try
            {
                await ExecuteAsync(scenario, cancellationToken);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                // Recorded by the measured run below.
            }
        }

        var results = new List<ScenarioResult>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            results.Add(await RunAsync(scenario, productCount, cancellationToken));
        }

        return results;
    }

    private async Task<ScenarioResult> RunAsync(Scenario scenario, long productCount, CancellationToken cancellationToken)
    {
        // The first execution is untimed: its answer is the one verified, and for filter pages the same command
        // is run once more under EXPLAIN ANALYZE / STATISTICS XML for rows examined and server time. A page call
        // is several statements, so it reports the executor's own counters (rounds, statements, rows fetched)
        // instead of a plan.
        var capture = scenario.Kind == ScenarioKind.List ? new CapturedPlan { Relation = "Products" } : null;
        Execution first;
        try
        {
            first = await ExecuteAsync(scenario, cancellationToken, capture);
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return OverBudget(scenario);
        }

        var (correct, fullPage, detail) = Verify(scenario, first, productCount);
        var plan = capture is null ? null : await SavePlanAsync(scenario, capture, productCount, cancellationToken);

        // The captured run executed the query twice (the plan, then EF's own execution).
        var estimate = capture is null ? first.Elapsed.TotalMilliseconds : first.Elapsed.TotalMilliseconds / 2;

        // A query that already took more than half the budget is not run again: one more run could exceed the
        // budget and costs up to ten minutes. Its single verified run is the measurement.
        double[] timings;
        if (estimate > budgetSeconds * 1_000d / 2)
        {
            timings = [estimate];
        }
        else
        {
            try
            {
                var warmups = estimate switch { < 50 => 4, < 1_000 => 2, _ => 0 };
                for (var i = 0; i < warmups; i++)
                {
                    await ExecuteAsync(scenario, cancellationToken);
                }

                // Queries that take tens of seconds are timed once: their spread is small relative to their
                // length, and each run costs minutes of CI time on SQL Server.
                var iterations = estimate switch { < 250 => 25, < 2_000 => 7, < 10_000 => 3, _ => 1 };
                timings = new double[iterations];
                for (var i = 0; i < iterations; i++)
                {
                    var run = await ExecuteAsync(scenario, cancellationToken);
                    timings[i] = run.Elapsed.TotalMilliseconds;
                    if (run.Counters is not null)
                    {
                        // The breakdown of a warm execution, the last one; the counts are the same on every run.
                        first = first with { Counters = run.Counters };
                    }
                }
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                return OverBudget(scenario);
            }
        }

        var iterationsRun = timings.Length;
        Array.Sort(timings);

        var counters = first.Counters;
        var result = new ScenarioResult(
            scenario.Id,
            scenario.Title,
            scenario.Kind.ToString(),
            scenario.Selectivity,
            scenario.ProductDepth,
            scenario.IsPage ? scenario.PageSize : 1,
            iterationsRun,
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
            plan?.File)
        {
            StoreFiltered = scenario.StoreId is not null,
            Rounds = counters?.Rounds,
            Statements = counters?.Statements,
            RowsFetched = counters?.RowsFetched,
            Streams = counters?.StreamsOpened,
            ResolveMs = counters?.ResolveMs,
            WalkMs = counters?.WalkMs,
            LoadMs = counters?.LoadMs,
        };

        log.Info(
            $"  {scenario.Id,-44} p50 {result.MedianMs,9:F2} ms  p95 {result.P95Ms,9:F2} ms  n={iterationsRun,2}" +
            (plan?.RowsExamined is { } examined ? $"  examined {examined:N0}" : "") +
            (plan?.PlanningMs is { } planning ? $"  plan {planning:F2} ms" : "") +
            (plan?.ExecutionMs is { } execution ? $"  exec {execution:F2} ms" : "") +
            (counters is null ? "" : $"  rounds {counters.Rounds} stmts {counters.Statements} fetched {counters.RowsFetched} streams {counters.StreamsOpened}") +
            (correct ? "" : $"  WRONG: {detail}") +
            (fullPage ? "" : "  (partial page)"));
        return result;
    }

    /// <summary>
    /// A scenario that ran into the budget, on any of its executions: counted at the budget, a lower bound on
    /// its real cost, which keeps the comparisons sound and the run bounded.
    /// </summary>
    private ScenarioResult OverBudget(Scenario scenario)
    {
        log.Info($"  {scenario.Id,-44} did not finish within {budgetSeconds} s");
        return new ScenarioResult(
            scenario.Id,
            scenario.Title,
            scenario.Kind.ToString(),
            scenario.Selectivity,
            scenario.ProductDepth,
            scenario.IsPage ? scenario.PageSize : 1,
            Iterations: 0,
            MedianMs: budgetSeconds * 1_000d,
            P95Ms: budgetSeconds * 1_000d,
            MinMs: budgetSeconds * 1_000d,
            MaxMs: budgetSeconds * 1_000d,
            Correct: true,
            FullPage: false,
            CorrectnessDetail: null,
            RowsExamined: null,
            ServerPlanningMs: null,
            ServerExecutionMs: null,
            PlanFile: null)
        { TimedOut = true, StoreFiltered = scenario.StoreId is not null };
    }

    /// <summary>A query the engine stopped at the budget: SQL Server's timeout, or PostgreSQL's cancellation on Npgsql's.</summary>
    internal static bool IsTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is TimeoutException
                || current is Microsoft.Data.SqlClient.SqlException { Number: -2 }
                || current is Npgsql.PostgresException { SqlState: "57014" })
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Execution> ExecuteAsync(Scenario scenario, CancellationToken cancellationToken, CapturedPlan? capture = null)
    {
        await using var db = createContext();
        db.Database.SetCommandTimeout(TimeSpan.FromSeconds(budgetSeconds));
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

            case ScenarioKind.Page:
            {
                // The application's call: the query declares the filter and the order; SqlOS finds the rows the
                // caller may see and returns the page with its cursor. Everything, principal resolution
                // included, is inside the clock, as a request would pay it.
                var query = BuildPageQuery(db, scenario);
                var clock = Stopwatch.StartNew();
                var page = await service.PageAsync(query, scenario.Principal.SubjectId, BenchmarkModel.ProductView, cursor: null, scenario.PageSize, cancellationToken);
                clock.Stop();
                return new Execution(clock.Elapsed, page.Data.ToList(), Allowed: null) { HasNextPage = page.HasNextPage, Counters = service.LastPageCounters };
            }

            case ScenarioKind.PointFunction:
            {
                var subjects = JsonSerializer.Serialize(scenario.Principal.ResolvedSubjectIds);
                var resourceId = RetailTree.ProductResourceId(scenario.ProductId);
                var clock = Stopwatch.StartNew();
                var allowed = await db.IsResourceAccessible(resourceId, subjects, ProductPermissionId).AnyAsync(cancellationToken);
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

    /// <summary>
    /// The page an application asks for: authorized, optionally store-scoped, in key order after a cursor or
    /// in price order, k + 1 rows.
    /// </summary>
    private static async Task<IQueryable<Product>> BuildListQueryAsync(BenchDbContext db, SqlOSFgaAuthService service, Scenario scenario)
    {
        var authorized = await service.BuildFilterAsync<Product>(scenario.Principal.SubjectId, BenchmarkModel.ProductView);
        IQueryable<Product> query = db.Products.AsNoTracking().Where(authorized);

        if (scenario.StoreId is { } storeId)
        {
            query = query.Where(p => p.StoreId == storeId);
        }

        if (scenario.Order == PageOrder.Price)
        {
            return query.OrderBy(p => p.Price).ThenBy(p => p.Id).Take(scenario.PageSize + 1);
        }

        var cursor = scenario.Cursor;
        return query.Where(p => p.Id > cursor).OrderBy(p => p.Id).Take(scenario.PageSize + 1);
    }

    /// <summary>
    /// The same page as a page call: the application's filters and the order, nothing else. A page from the
    /// middle of the table is the application's own predicate on the key, which the per-level seeks serve.
    /// </summary>
    private static IQueryable<Product> BuildPageQuery(BenchDbContext db, Scenario scenario)
    {
        IQueryable<Product> query = db.Products.AsNoTracking();
        if (scenario.StoreId is { } storeId)
        {
            query = query.Where(p => p.StoreId == storeId);
        }

        if (scenario.Order == PageOrder.Price)
        {
            return query.OrderBy(p => p.Price).ThenBy(p => p.Id);
        }

        if (scenario.Cursor > 0)
        {
            var cursor = scenario.Cursor;
            query = query.Where(p => p.Id > cursor);
        }

        return query.OrderBy(p => p.Id);
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
    /// but it means the engine had to read to the end of the table, so the scale gate ignores it.
    /// </summary>
    private (bool Correct, bool FullPage, string? Detail) Verify(Scenario scenario, Execution execution, long productCount)
    {
        if (!scenario.IsPage)
        {
            return execution.Allowed == scenario.ExpectAllowed
                ? (true, true, null)
                : (false, true, $"expected {(scenario.ExpectAllowed ? "allowed" : "denied")}, got {(execution.Allowed == true ? "allowed" : "denied")}");
        }

        var rows = scenario.PageSize + 1;
        var expected = new List<int>(rows);
        bool Visible(long id) => scenario.Principal.Sees(tree, id) && (scenario.StoreId is null || tree.LeafOf(id).StoreId == scenario.StoreId);
        if (scenario.Order == PageOrder.Price)
        {
            foreach (var id in DatasetRows.IdsByPrice(productCount))
            {
                if (Visible(id))
                {
                    expected.Add((int)id);
                    if (expected.Count == rows)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            for (long id = scenario.Cursor + 1; id <= productCount && expected.Count < rows; id++)
            {
                if (Visible(id))
                {
                    expected.Add((int)id);
                }
            }
        }

        var page = execution.Page!;
        var actual = page.Select(p => p.Id).ToList();
        if (scenario.Kind == ScenarioKind.Page)
        {
            // The page call returns k rows and says whether a next page exists; the filter page returns k + 1.
            var wanted = expected.Take(scenario.PageSize).ToList();
            if (!actual.SequenceEqual(wanted))
            {
                return (false, false, $"page [{Preview(actual)}] != expected [{Preview(wanted)}]");
            }

            var more = expected.Count > scenario.PageSize;
            if (execution.HasNextPage != more)
            {
                return (false, false, $"hasNextPage {execution.HasNextPage}, expected {more}");
            }
        }
        else if (!actual.SequenceEqual(expected))
        {
            return (false, false, $"page [{Preview(actual)}] != expected [{Preview(expected)}]");
        }

        foreach (var product in page)
        {
            var leaf = tree.LeafOf(product.Id);
            if (product.StoreId != leaf.StoreId || product.ResourceId != RetailTree.ProductResourceId(product.Id) || product.Price != DatasetRows.Price(product.Id))
            {
                return (false, false, $"product {product.Id} has store {product.StoreId} / {product.ResourceId} / {product.Price}, expected {leaf.StoreId} / {DatasetRows.Price(product.Id)}");
            }
        }

        return expected.Count == rows
            ? (true, true, null)
            : (true, false, $"only {expected.Count} authorized rows exist at this scale");
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

    private sealed record Execution(TimeSpan Elapsed, List<Product>? Page, bool? Allowed)
    {
        /// <summary>For a page call: whether it reported a next page.</summary>
        public bool HasNextPage { get; init; }

        /// <summary>For a page call: what the executor did, in its own units.</summary>
        public SqlOSFgaPageCounters? Counters { get; init; }
    }

    private sealed record PlanSummary(long? RowsExamined, double? PlanningMs, double? ExecutionMs, string File);
}

/// <param name="RowsExamined">Product rows the plan read, produced or discarded, summed over loops.</param>
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
    long? RowsExamined,
    double? ServerPlanningMs,
    double? ServerExecutionMs,
    string? PlanFile)
{
    /// <summary>The first execution exceeded the run's budget; the timings hold the budget, a lower bound.</summary>
    public bool TimedOut { get; init; }

    /// <summary>The page was filtered to one store as well, so it touches that store's rows through its own index.</summary>
    public bool StoreFiltered { get; init; }

    /// <summary>For a page call: round trips the executor made.</summary>
    public int? Rounds { get; init; }

    /// <summary>For a page call: statements it ran (the prelude and every round).</summary>
    public int? Statements { get; init; }

    /// <summary>For a page call: index rows the rounds fetched, the page's rows and the ones judged or discarded.</summary>
    public int? RowsFetched { get; init; }

    /// <summary>For a page call: streams opened (one seek each).</summary>
    public int? Streams { get; init; }

    /// <summary>For a page call, on its first (verified) execution: milliseconds resolving the caller's principals and the permission.</summary>
    public double? ResolveMs { get; init; }

    /// <summary>For a page call, on its first execution: milliseconds in the walk (prelude and rounds).</summary>
    public double? WalkMs { get; init; }

    /// <summary>For a page call, on its first execution: milliseconds loading the page's rows through the application's query.</summary>
    public double? LoadMs { get; init; }

    /// <summary>
    /// Server execution time per row examined, in microseconds: the paper's per-row constant, without
    /// planning, network, or EF Core overhead.
    /// </summary>
    public double? MicrosecondsPerRowExamined
        => RowsExamined is > 0 && ServerExecutionMs is { } execution
            ? execution * 1000 / RowsExamined.Value
            : null;
}
