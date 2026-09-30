using System.Data.Common;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// Captures the actual execution plan of the next query EF Core runs in the current async flow, by executing
/// the exact command EF generated (text and parameters) once more under <c>EXPLAIN (ANALYZE, BUFFERS)</c> or
/// <c>SET STATISTICS XML ON</c>. Used for one untimed execution per scenario, never inside a measurement.
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
        // capture happen once even if the query issues more than one command.
        if (Pending.Value is { Done: false } plan)
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

    private async Task CaptureAsync(DbCommand command, CapturedPlan plan, CancellationToken cancellationToken)
    {
        await using var explain = command.Connection!.CreateCommand();
        explain.Transaction = command.Transaction;
        explain.CommandTimeout = 0;
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
            plan.ProductRowsExamined = PostgresProductRows(json);
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
            plan.ProductRowsExamined = SqlServerProductRows(plan.Text);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var stats = XDocument.Parse(plan.Text).Descendants(ns + "QueryTimeStats").LastOrDefault();
            plan.ExecutionMs = (double?)stats?.Attribute("ElapsedTime");

            // SQL Server caches the plan; applications do not pay compilation per execution.
            plan.PlanningMs = null;
        }
    }

    /// <summary>Rows the Products scan produced or discarded, summed over loops: the candidates examined.</summary>
    internal static long? PostgresProductRows(string json)
    {
        using var document = JsonDocument.Parse(json);
        long? best = null;
        void Visit(JsonElement node)
        {
            if (node.TryGetProperty("Relation Name", out var relation) && relation.GetString() == "Products")
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

    /// <summary>Rows the operators over <c>[Products]</c> read (<c>ActualRowsRead</c>, else <c>ActualRows</c>).</summary>
    internal static long? SqlServerProductRows(string xml)
    {
        XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        long? best = null;
        foreach (var relOp in XDocument.Parse(xml).Descendants(ns + "RelOp"))
        {
            var readsProducts = relOp.Elements()
                .SelectMany(operation => operation.Elements(ns + "Object"))
                .Any(o => (string?)o.Attribute("Table") == "[Products]");
            if (!readsProducts)
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
    public string? Text { get; set; }
    public string Extension { get; set; } = "txt";
    public long? ProductRowsExamined { get; set; }

    /// <summary>Server-side planning, paid on every execution by PostgreSQL when statements are not prepared.</summary>
    public double? PlanningMs { get; set; }

    /// <summary>Server-side execution of the plan, excluding planning, network, and EF Core.</summary>
    public double? ExecutionMs { get; set; }
    public string? Error { get; set; }
}
