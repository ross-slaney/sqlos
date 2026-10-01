using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SqlOS.Benchmarks.Data;
using SqlOS.Benchmarks.Infrastructure;
using SqlOS.Database;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Models;

namespace SqlOS.Benchmarks.Scenarios;

/// <summary>
/// What the closure costs on writes, measured once at the first scale: single-row inserts with the triggers on
/// and off (the difference is the trigger), one multi-row insert, a multi-row delete, and the subtree
/// operations (reparent, deactivate, reactivate) on a region. Every row it adds is removed again, so the
/// closure is back to the loaded state when it finishes; the caller verifies that by checksum.
/// </summary>
internal sealed class MaintenancePass(
    Func<BenchDbContext> createContext,
    DatabaseProvider provider,
    SqlOSFgaOptions fga,
    RetailTree tree,
    IDatasetLoader loader,
    Log log)
{
    public const int SingleInserts = 2_000;
    public const int BatchRows = 2_000;
    private const string Prefix = "bench::maintenance::";

    public async Task<List<MaintenanceResult>> RunAsync(Principals people, long productCount, CancellationToken cancellationToken)
    {
        var results = new List<MaintenanceResult>();

        // Products under the store manager's store: depth 4, four ancestors, four closure rows per insert.
        var parent = people.StoreManager.ScopeResourceId;
        results.Add(await SingleInsertsAsync("insert.single", "Single-row inserts under a store, triggers on", parent, cancellationToken));
        results.Add(await DeleteAsync("delete.batch", $"One {SingleInserts:N0}-row delete", cancellationToken));

        await SetTriggersAsync(enabled: false, cancellationToken);
        try
        {
            results.Add(await SingleInsertsAsync("insert.single.no-triggers", "Single-row inserts, triggers disabled", parent, cancellationToken));
            await DeleteAsync("delete.no-triggers", "", cancellationToken);
        }
        finally
        {
            await SetTriggersAsync(enabled: true, cancellationToken);
        }

        results.Add(await BatchInsertAsync(parent, cancellationToken));
        await DeleteAsync("delete.after-batch", "", cancellationToken);

        // A region of chain 1 and everything beneath it: stores, and their products at this scale.
        var region = RetailTree.RegionId(1, 1);
        var (subtree, pairs) = SubtreeOf(region, productCount);
        results.Add(await ReparentAsync("reparent", $"Reparent a region subtree ({subtree:N0} resources) to another chain", region, RetailTree.ChainId(2), subtree, pairs, cancellationToken));
        results.Add(await ReparentAsync("reparent.back", "Reparent it back", region, RetailTree.ChainId(1), subtree, pairs, cancellationToken));
        results.Add(await SetActiveAsync("deactivate", "Deactivate the region (cuts every path through it)", region, false, subtree, pairs, cancellationToken));
        results.Add(await SetActiveAsync("reactivate", "Reactivate the region", region, true, subtree, pairs, cancellationToken));
        return results;
    }

    private async Task<MaintenanceResult> SingleInsertsAsync(string id, string title, string parent, CancellationToken cancellationToken)
    {
        var before = await loader.ClosureChecksumAsync(cancellationToken);
        await using var db = createContext();
        var clock = Stopwatch.StartNew();
        for (var i = 1; i <= SingleInserts; i++)
        {
            db.Set<SqlOSFgaResource>().Add(new SqlOSFgaResource
            {
                Id = $"{Prefix}{i:D5}",
                ParentId = parent,
                Name = $"Maintenance {i}",
                ResourceTypeId = "product",
            });
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
        }

        clock.Stop();
        var after = await loader.ClosureChecksumAsync(cancellationToken);
        return Report(id, title, SingleInserts, after.Rows - before.Rows, clock.Elapsed);
    }

    private async Task<MaintenanceResult> BatchInsertAsync(string parent, CancellationToken cancellationToken)
    {
        var before = await loader.ClosureChecksumAsync(cancellationToken);
        await using var db = createContext();
        var sql = SqlOSDatabase.Resolve(db.Database);
        var resources = Quote(fga.Schema) + "." + Quote(fga.TableNames.Resources);
        var statement = provider == DatabaseProvider.PostgreSql
            ? $"""
              INSERT INTO {resources} ("Id", "ParentId", "Name", "ResourceTypeId", "IsActive", "CreatedAt", "UpdatedAt")
              SELECT @prefix || lpad(i::text, 5, '0'), @parent, 'Maintenance ' || i, 'product', true,
                     now() at time zone 'utc', now() at time zone 'utc'
              FROM generate_series(1, @count) AS i
              """
            : $"""
              WITH n AS (
                  SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                  FROM sys.all_columns AS a CROSS JOIN sys.all_columns AS b)
              INSERT INTO {resources} ([Id], [ParentId], [Name], [ResourceTypeId], [IsActive], [CreatedAt], [UpdatedAt])
              SELECT CONCAT(@prefix, RIGHT('00000' + CAST(i AS varchar(10)), 5)), @parent, CONCAT('Maintenance ', i), 'product', 1,
                     SYSUTCDATETIME(), SYSUTCDATETIME()
              FROM n
              """;

        var clock = Stopwatch.StartNew();
        var rows = await db.Database.ExecuteSqlRawAsync(
            statement,
            [sql.CreateParameter("@prefix", Prefix), sql.CreateParameter("@parent", parent), sql.CreateParameter("@count", BatchRows)],
            cancellationToken);
        clock.Stop();
        if (rows != BatchRows)
        {
            throw new InvalidOperationException($"The batch insert wrote {rows} rows, expected {BatchRows}.");
        }

        var after = await loader.ClosureChecksumAsync(cancellationToken);
        return Report("insert.batch", $"One {BatchRows:N0}-row insert under a store", BatchRows, after.Rows - before.Rows, clock.Elapsed);
    }

    private async Task<MaintenanceResult> DeleteAsync(string id, string title, CancellationToken cancellationToken)
    {
        var before = await loader.ClosureChecksumAsync(cancellationToken);
        await using var db = createContext();
        var clock = Stopwatch.StartNew();
        var rows = await db.Set<SqlOSFgaResource>().Where(r => r.Id.StartsWith(Prefix)).ExecuteDeleteAsync(cancellationToken);
        clock.Stop();
        var after = await loader.ClosureChecksumAsync(cancellationToken);
        return Report(id, title, rows, after.Rows - before.Rows, clock.Elapsed);
    }

    private async Task<MaintenanceResult> ReparentAsync(string id, string title, string resourceId, string newParent, long subtree, long pairs, CancellationToken cancellationToken)
    {
        await using var db = createContext();
        var clock = Stopwatch.StartNew();
        var rows = await db.Set<SqlOSFgaResource>()
            .Where(r => r.Id == resourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ParentId, newParent), cancellationToken);
        clock.Stop();
        if (rows != 1)
        {
            throw new InvalidOperationException($"{resourceId} was not updated.");
        }

        return Report(id, title, subtree, pairs, clock.Elapsed);
    }

    private async Task<MaintenanceResult> SetActiveAsync(string id, string title, string resourceId, bool active, long subtree, long pairs, CancellationToken cancellationToken)
    {
        await using var db = createContext();
        var clock = Stopwatch.StartNew();
        var rows = await db.Set<SqlOSFgaResource>()
            .Where(r => r.Id == resourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsActive, active), cancellationToken);
        clock.Stop();
        if (rows != 1)
        {
            throw new InvalidOperationException($"{resourceId} was not updated.");
        }

        return Report(id, title, subtree, pairs, clock.Elapsed);
    }

    /// <summary>
    /// The resources at and beneath <paramref name="resourceId"/> at this scale, and the closure rows they
    /// hold (one per proper ancestor): the rows a subtree operation recomputes.
    /// </summary>
    private (long Resources, long Pairs) SubtreeOf(string resourceId, long productCount)
    {
        long resources = 1, pairs = tree.AncestorsOf(resourceId).Count();
        foreach (var node in tree.Nodes)
        {
            if (tree.AncestorsOf(node.Id).Contains(resourceId, StringComparer.Ordinal))
            {
                resources++;
                pairs += tree.AncestorsOf(node.Id).Count();
            }
        }

        for (long id = 1; id <= productCount; id++)
        {
            var leaf = tree.LeafOf(id);
            if (leaf.IsUnder(resourceId))
            {
                resources++;
                pairs += leaf.Ancestors.Length;
            }
        }

        return (resources, pairs);
    }

    private async Task SetTriggersAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var db = createContext();
        var resources = Quote(fga.Schema) + "." + Quote(fga.TableNames.Resources);
        var verb = enabled ? "ENABLE" : "DISABLE";
        var which = provider == DatabaseProvider.PostgreSql ? "USER" : "ALL";
        await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {resources} {verb} TRIGGER {which}", cancellationToken);
    }

    private string Quote(string identifier)
        => provider == DatabaseProvider.PostgreSql ? $"\"{identifier}\"" : $"[{identifier}]";

    private MaintenanceResult Report(string id, string title, long rows, long closureRows, TimeSpan elapsed)
    {
        var result = new MaintenanceResult(id, title, rows, closureRows, elapsed.TotalMilliseconds);
        if (title.Length > 0)
        {
            log.Info($"  {id,-28} {result.Milliseconds,9:F1} ms  rows {rows,8:N0}  closure rows {closureRows,10:N0}  ({result.MillisecondsPerRow:F3} ms/row)");
        }

        return result;
    }
}

/// <param name="Rows">Resources the operation wrote (or, for a subtree operation, the subtree's size).</param>
/// <param name="ClosureRows">Closure rows added (negative: removed), or for a subtree operation, recomputed.</param>
internal sealed record MaintenanceResult(string Id, string Title, long Rows, long ClosureRows, double Milliseconds)
{
    public double MillisecondsPerRow => Rows > 0 ? Milliseconds / Rows : 0;
}
