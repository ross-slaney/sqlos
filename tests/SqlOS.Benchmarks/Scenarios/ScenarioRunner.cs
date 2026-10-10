using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
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
/// query EF Core builds from it. Three timings per page: <c>BuildFilterAsync</c> (the caller's principals and
/// the permission), the query alone (what the gates hold), and the whole request. Every result is checked
/// against ground truth computed independently from the dataset generator, so a fast wrong answer fails the
/// run.
/// </summary>
internal sealed class ScenarioRunner(
    Func<BenchDbContext> createContext,
    SqlOSFgaOptions fga,
    RetailTree tree,
    string planDirectory,
    int budgetSeconds,
    Log log)
{
    private const string ProductPermissionId = "perm_product_view";

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
        // The first execution is untimed: its answer is the one verified, and for pages the same command is
        // run once more under EXPLAIN ANALYZE / STATISTICS XML for rows examined and server time.
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
        double[] prepares;
        if (estimate > budgetSeconds * 1_000d / 2)
        {
            timings = [estimate];
            prepares = [first.PrepareMs];
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
                prepares = new double[iterations];
                for (var i = 0; i < iterations; i++)
                {
                    var run = await ExecuteAsync(scenario, cancellationToken);
                    timings[i] = run.Elapsed.TotalMilliseconds;
                    prepares[i] = run.PrepareMs;
                }
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                return OverBudget(scenario);
            }
        }

        var iterationsRun = timings.Length;
        var requests = timings.Zip(prepares, (query, prepare) => query + prepare).ToArray();
        Array.Sort(timings);
        Array.Sort(prepares);
        Array.Sort(requests);

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
            Shape = first.Shape,
            PrepareMs = scenario.IsPage ? Percentile(prepares, 0.50) : null,
            RequestMs = scenario.IsPage ? Percentile(requests, 0.50) : null,
        };

        log.Info(
            $"  {scenario.Id,-44} p50 {result.MedianMs,9:F2} ms  p95 {result.P95Ms,9:F2} ms  n={iterationsRun,2}" +
            (result.RequestMs is { } request ? $"  request {request:F2} ms (BuildFilterAsync {result.PrepareMs:F2}, {result.Shape})" : "") +
            (plan?.RowsExamined is { } examined ? $"  examined {examined:N0}" : "") +
            (plan?.PlanningMs is { } planning ? $"  plan {planning:F2} ms" : "") +
            (plan?.ExecutionMs is { } execution ? $"  exec {execution:F2} ms" : "") +
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
                // The whole request, as an application pays it: BuildFilterAsync (the caller's principals and
                // the permission), then the query with its predicate.
                var prepare = Stopwatch.StartNew();
                var filter = await service.BuildFilterAsync<Product>(scenario.Principal.SubjectId, BenchmarkModel.ProductView);
                prepare.Stop();
                var query = BuildQuery(db, filter, scenario);

                // Which of the two filters fn_ListFirst chose, read once from the verified run's SQL.
                var shape = capture is null ? null : query.ToQueryString().Contains("fn_VisibleSet", StringComparison.Ordinal) ? "list first" : "row check";

                // Armed only now, after BuildFilterAsync's own lookups, so the plan is the page statement's.
                if (capture is not null)
                {
                    PlanCapture.Arm(capture);
                }

                try
                {
                    var clock = Stopwatch.StartNew();
                    var page = await query.ToListAsync(cancellationToken);
                    clock.Stop();
                    return new Execution(clock.Elapsed, page, Allowed: null) { PrepareMs = prepare.Elapsed.TotalMilliseconds, Shape = shape };
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
    /// in price order, k + 1 rows, projected to the columns the harness verifies. The predicate composes as
    /// one EXISTS over <c>fn_Visible</c>, so the engine plans the whole statement.
    /// </summary>
    private static IQueryable<ProductRow> BuildQuery(BenchDbContext db, Expression<Func<Product, bool>> filter, Scenario scenario)
    {
        var rows = db.Products.AsNoTracking().Where(filter);
        if (scenario.StoreId is { } storeId)
        {
            rows = rows.Where(p => p.StoreId == storeId);
        }

        if (scenario.Order == PageOrder.Id)
        {
            var cursor = scenario.Cursor;
            rows = rows.Where(p => p.Id > cursor);
        }

        var ordered = scenario.Order == PageOrder.Price ? rows.OrderBy(p => p.Price).ThenBy(p => p.Id) : rows.OrderBy(p => p.Id);
        return ordered.Take(scenario.PageSize + 1).Select(p => new ProductRow { Id = p.Id, StoreId = p.StoreId, ResourceId = p.ResourceId, Price = p.Price });
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
        if (!actual.SequenceEqual(expected))
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

    private sealed record Execution(TimeSpan Elapsed, List<ProductRow>? Page, bool? Allowed)
    {
        /// <summary>Milliseconds in BuildFilterAsync, before the query.</summary>
        public double PrepareMs { get; init; }

        /// <summary>The filter fn_ListFirst chose ("list first" or "row check"), on the verified run.</summary>
        public string? Shape { get; init; }
    }

    private sealed record PlanSummary(long? RowsExamined, double? PlanningMs, double? ExecutionMs, string File);
}

/// <summary>A page's row as the harness verifies it: the key, the store, the resource and the price.</summary>
internal sealed class ProductRow
{
    public int Id { get; init; }

    public int StoreId { get; init; }

    public string ResourceId { get; init; } = string.Empty;

    public decimal Price { get; init; }
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

    /// <summary>For a page: the filter fn_ListFirst chose for the caller, "list first" or "row check".</summary>
    public string? Shape { get; init; }

    /// <summary>For a page: median milliseconds of BuildFilterAsync (the caller's principals, the permission, and fn_ListFirst's count).</summary>
    public double? PrepareMs { get; init; }

    /// <summary>For a page: median milliseconds of the whole request, BuildFilterAsync and the query.</summary>
    public double? RequestMs { get; init; }

    /// <summary>
    /// Server execution time per row examined, in microseconds: the paper's per-row constant, without
    /// planning, network, or EF Core overhead.
    /// </summary>
    public double? MicrosecondsPerRowExamined
        => RowsExamined is > 0 && ServerExecutionMs is { } execution
            ? execution * 1000 / RowsExamined.Value
            : null;
}
