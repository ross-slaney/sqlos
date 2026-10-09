using System.Data.Common;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// Captures actual execution plans: for the next query EF Core runs in the current async flow (by executing
/// the exact command EF generated once more under <c>EXPLAIN (ANALYZE, BUFFERS)</c> or
/// <c>SET STATISTICS XML ON</c>), or for a command the harness builds itself. Used for one untimed execution
/// per scenario, never inside a measurement.
/// </summary>
internal sealed class PlanCapture(DatabaseProvider provider) : DbCommandInterceptor
{
    private static readonly AsyncLocal<CapturedPlan?> Pending = new();

    /// <summary>Arms capture for the next reader command in this async flow; call <see cref="Disarm"/> after it.</summary>
    public static void Arm(CapturedPlan plan) => Pending.Value = plan;

    public static void Disarm() => Pending.Value = null;

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        // EF runs the command in a child flow, which cannot clear the caller's AsyncLocal; the flag makes the
        // capture happen once even if the query issues more than one command. The command captured is the one
        // over the relation: a planned statement first reads the caller's access roots in a command of its own.
        if (Pending.Value is { Done: false } plan && command.CommandText.Contains(plan.Relation, StringComparison.Ordinal))
        {
            plan.Done = true;
            try
            {
                await CaptureAsync(command, plan, cancellationToken);
            }
            catch (Exception ex)
            {
                plan.Error = ex.Message;
            }
        }

        return result;
    }

    /// <summary>Explains a command the harness built itself (the page query runs outside EF Core).</summary>
    public async Task<CapturedPlan> ExplainAsync(DbCommand command, string relation, CancellationToken cancellationToken)
    {
        var plan = new CapturedPlan { Relation = relation, Done = true };
        try
        {
            await CaptureAsync(command, plan, cancellationToken);
        }
        catch (Exception ex)
        {
            plan.Error = ex.Message;
        }

        return plan;
    }

    private async Task CaptureAsync(DbCommand command, CapturedPlan plan, CancellationToken cancellationToken)
    {
        await using var explain = command.Connection!.CreateCommand();
        explain.Transaction = command.Transaction;
        explain.CommandTimeout = command.CommandTimeout;
        foreach (DbParameter parameter in command.Parameters)
        {
            explain.Parameters.Add(((ICloneable)parameter).Clone());
        }

        if (provider == DatabaseProvider.PostgreSql)
        {
            explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.CommandText;
            var json = (string)(await explain.ExecuteScalarAsync(cancellationToken))!;
            plan.Text = json;
            plan.Extension = "json";
            plan.RowsExamined = PostgresRows(json, plan.Relation);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement[0];
                plan.PlanningMs = root.TryGetProperty("Planning Time", out var planning) ? planning.GetDouble() : null;
                plan.ExecutionMs = root.TryGetProperty("Execution Time", out var execution) ? execution.GetDouble() : null;
            }

            return;
        }

        explain.CommandText = "SET STATISTICS XML ON;\n" + command.CommandText + "\nSET STATISTICS XML OFF;";
        await using var reader = await explain.ExecuteReaderAsync(cancellationToken);
        do
        {
            var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (isPlan)
                {
                    plan.Text = reader.GetString(0);
                }
            }
        }
        while (await reader.NextResultAsync(cancellationToken));

        plan.Extension = "sqlplan";
        if (plan.Text is not null)
        {
            plan.RowsExamined = SqlServerRows(plan.Text, plan.Relation);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var stats = XDocument.Parse(plan.Text).Descendants(ns + "QueryTimeStats").LastOrDefault();
            plan.ExecutionMs = (double?)stats?.Attribute("ElapsedTime");

            // SQL Server caches the plan; applications do not pay compilation per execution.
            plan.PlanningMs = null;
        }
    }

    /// <summary>Rows the scans of <paramref name="relation"/> produced or discarded, summed over loops.</summary>
    internal static long? PostgresRows(string json, string relation)
    {
        using var document = JsonDocument.Parse(json);
        long? best = null;
        void Visit(JsonElement node)
        {
            if (node.TryGetProperty("Relation Name", out var name) && name.GetString() == relation)
            {
                var rows = node.GetProperty("Actual Rows").GetDouble();
                if (node.TryGetProperty("Rows Removed by Filter", out var removed))
                {
                    rows += removed.GetDouble();
                }

                var loops = node.TryGetProperty("Actual Loops", out var l) ? l.GetDouble() : 1;
                var examined = (long)Math.Round(rows * loops);
                best = best is null ? examined : Math.Max(best.Value, examined);
            }

            if (node.TryGetProperty("Plans", out var children))
            {
                foreach (var child in children.EnumerateArray())
                {
                    Visit(child);
                }
            }
        }

        foreach (var root in document.RootElement.EnumerateArray())
        {
            Visit(root.GetProperty("Plan"));
        }

        return best;
    }

    /// <summary>
    /// Rows the operators over the entity or its owned scope index read (ActualRowsRead, else ActualRows).
    /// Count the largest input, as for a nonclustered seek followed by key lookups; counting only the final
    /// application-table lookup would hide work done while finding candidates in the scope index.
    /// </summary>
    internal static long? SqlServerRows(string xml, string relation)
    {
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        var table = $"[{relation}]";
        // Benchmark application tables use the default schema, without a schema prefix in the index name.
        var scopeIndex = $"[SqlOSFgaScopeIndex_{relation}]";
        long? best = null;
        foreach (var relOp in XDocument.Parse(xml).Descendants(ns + "RelOp"))
        {
            var readsRelation = relOp.Elements()
                .SelectMany(operation => operation.Elements(ns + "Object"))
                .Any(o => (string?)o.Attribute("Table") is { } name && (name == table || name == scopeIndex));
            if (!readsRelation)
            {
                continue;
            }

            var counters = relOp.Element(ns + "RunTimeInformation")?.Elements(ns + "RunTimeCountersPerThread").ToList() ?? [];
            if (counters.Count == 0)
            {
                continue;
            }

            var examined = counters.Sum(c => (long?)c.Attribute("ActualRowsRead") ?? (long?)c.Attribute("ActualRows") ?? 0);
            best = best is null ? examined : Math.Max(best.Value, examined);
        }

        return best;
    }
}

internal sealed class CapturedPlan
{
    public bool Done { get; set; }

    /// <summary>The relation whose scanned rows count as "rows examined": the entity table.</summary>
    public string Relation { get; set; } = "Products";

    public string? Text { get; set; }
    public string Extension { get; set; } = "txt";
    public long? RowsExamined { get; set; }

    /// <summary>Server-side planning, paid on every execution by PostgreSQL when statements are not prepared.</summary>
    public double? PlanningMs { get; set; }

    /// <summary>Server-side execution of the plan, excluding planning, network, and EF Core.</summary>
    public double? ExecutionMs { get; set; }

    public string? Error { get; set; }
}
