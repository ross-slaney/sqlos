using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SqlOS.Database;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// Keeps page-order entries equal to what a rebuild would write. <c>SqlOSDbContext&lt;TContext&gt;</c>
/// collects the rows and resources a save changes, saves, and then rewrites their entries in the
/// same transaction, so rows and entries never disagree.
/// </summary>
/// <remarks>
/// An entry <c>(NodeId = X, RowKey = r)</c> exists when X is the row's own resource or an ancestor
/// of it, every resource from the row's resource up to X is active, and the chain is within
/// <c>MaxResourceHierarchyDepth</c> with no cycle: exactly the nodes a grant could sit on and reach
/// the row under <c>fn_IsResourceAccessible</c>. <c>fn_ResourceAncestors</c> computes that set.
/// </remarks>
internal static class SqlOSFgaPageOrderMaintenance
{
    private const string KeysParameter = "@keys";
    private const string ResourceIdsParameter = "@resourceIds";
    private static readonly TimeSpan RebuildLockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The rows and resources one save changes.</summary>
    internal sealed class Plan
    {
        public Dictionary<IEntityType, RowChanges> Rows { get; } = [];
        public HashSet<string> ChangedResources { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>The changed rows of one entity hierarchy.</summary>
    internal sealed class RowChanges(IReadOnlyList<SqlOSFgaPageOrder> orders)
    {
        public IReadOnlyList<SqlOSFgaPageOrder> Orders { get; } = orders;
        public IProperty Key => Orders[0].Key;
        public List<EntityEntry> Added { get; } = [];
        public List<object> Modified { get; } = [];
        public List<object> Deleted { get; } = [];
    }

    /// <summary>
    /// Collects, before the save, the rows whose entries the save changes: added rows; modified rows
    /// whose <c>ResourceId</c> or an order column changed; deleted rows; and resources whose parent
    /// or activity changed, or that are deleted. Returns <see langword="null"/> when there is nothing to do.
    /// </summary>
    public static Plan? Collect(DbContext context)
    {
        if (!context.Database.IsRelational() || SqlOSFgaPageOrders.All(context.Model).Count == 0)
        {
            return null;
        }

        Plan? plan = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            if (entry.Entity is SqlOSFgaResource resource)
            {
                if (entry.State == EntityState.Deleted
                    || (entry.State == EntityState.Modified
                        && (entry.Property(nameof(SqlOSFgaResource.ParentId)).IsModified
                            || entry.Property(nameof(SqlOSFgaResource.IsActive)).IsModified)))
                {
                    (plan ??= new Plan()).ChangedResources.Add(resource.Id);
                }

                continue;
            }

            var orders = SqlOSFgaPageOrders.For(entry.Metadata);
            if (orders.Count == 0)
            {
                continue;
            }

            if (entry.State == EntityState.Modified && !ChangesEntries(entry, orders))
            {
                continue;
            }

            plan ??= new Plan();
            var root = orders[0].EntityType;
            if (!plan.Rows.TryGetValue(root, out var rows))
            {
                rows = new RowChanges(orders);
                plan.Rows.Add(root, rows);
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    // Store-generated keys exist only after the save; read them then.
                    rows.Added.Add(entry);
                    break;
                case EntityState.Modified:
                    rows.Modified.Add(ProviderKey(entry, rows.Key));
                    break;
                default:
                    rows.Deleted.Add(ProviderKey(entry, rows.Key));
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// Saves and rewrites the plan's entries in one transaction: the caller's, an ambient one, or one
    /// owned here (inside the context's execution strategy, so retrying strategies keep working).
    /// </summary>
    public static int SaveChanges(
        DbContext context,
        Plan plan,
        Func<bool, int> saveChanges,
        bool acceptAllChangesOnSuccess)
    {
        var database = context.Database;
        int saved;
        if (database.CurrentTransaction != null || System.Transactions.Transaction.Current != null)
        {
            saved = saveChanges(false);
            Run(context, plan);
        }
        else
        {
            saved = database.CreateExecutionStrategy().Execute(
                (Context: context, Plan: plan, Save: saveChanges),
                static (_, state) =>
                {
                    using var transaction = state.Context.Database.BeginTransaction();
                    var count = state.Save(false);
                    Run(state.Context, state.Plan);
                    transaction.Commit();
                    return count;
                },
                verifySucceeded: null);
        }

        if (acceptAllChangesOnSuccess)
        {
            context.ChangeTracker.AcceptAllChanges();
        }

        return saved;
    }

    /// <inheritdoc cref="SaveChanges"/>
    public static async Task<int> SaveChangesAsync(
        DbContext context,
        Plan plan,
        Func<bool, CancellationToken, Task<int>> saveChangesAsync,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        var database = context.Database;
        int saved;
        if (database.CurrentTransaction != null || System.Transactions.Transaction.Current != null)
        {
            saved = await saveChangesAsync(false, cancellationToken);
            await RunAsync(context, plan, cancellationToken);
        }
        else
        {
            saved = await database.CreateExecutionStrategy().ExecuteAsync(
                (Context: context, Plan: plan, Save: saveChangesAsync),
                static async (_, state, token) =>
                {
                    await using var transaction = await state.Context.Database.BeginTransactionAsync(token);
                    var count = await state.Save(false, token);
                    await RunAsync(state.Context, state.Plan, token);
                    await transaction.CommitAsync(token);
                    return count;
                },
                verifySucceeded: null,
                cancellationToken);
        }

        if (acceptAllChangesOnSuccess)
        {
            context.ChangeTracker.AcceptAllChanges();
        }

        return saved;
    }

    /// <summary>
    /// Rewrites every declared order: all entries deleted, then inserted from the application table
    /// and the resource graph in one statement, one transaction per order.
    /// </summary>
    public static async Task RebuildAsync(DbContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return;
        }

        foreach (var order in SqlOSFgaPageOrders.All(context.Model))
        {
            var written = await RebuildOrderAsync(context, order, onlyIfEmpty: false, cancellationToken);
            SqlOSFgaPageOrders.SetTableMissing(context.Model, order, missing: false);
            logger.LogInformation(
                "Rebuilt page order {PageOrder}: {Entries} entries in {Table}.",
                order.Description,
                written,
                order.Table);
        }
    }

    /// <summary>
    /// Startup step after the FGA functions are installed: fills the entry table of an order whose
    /// table exists and is empty while its application table has rows (the first start after the
    /// order was declared). A missing entry table is logged and skipped; pages for that order run
    /// without it until it exists.
    /// </summary>
    public static async Task ReconcileAsync(DbContext context, ILogger logger, CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational())
        {
            return;
        }

        var orders = SqlOSFgaPageOrders.All(context.Model);
        if (orders.Count == 0)
        {
            return;
        }

        var provider = SqlOSDatabase.Resolve(context.Database);
        var maxDepth = SqlOSFgaHierarchyDepth.Resolve(context.Database, context);
        foreach (var order in orders)
        {
            var store = order.Store(maxDepth);
            if (!await IsTrueAsync(context, provider.BuildTableExistsSql(store.EntrySchema, store.EntryTable), cancellationToken))
            {
                SqlOSFgaPageOrders.SetTableMissing(context.Model, order, missing: true);
                logger.LogWarning(
                    "Page order {PageOrder}: table {Table} does not exist. Apply the migration that creates it. Until it exists, ToPageAsync pages {Entity} by this order without it, reading the table in order and checking each row.",
                    order.Description,
                    order.Table,
                    order.EntityType.ClrType.Name);
                continue;
            }

            SqlOSFgaPageOrders.SetTableMissing(context.Model, order, missing: false);
            if (await IsTrueAsync(context, provider.BuildTableHasRowsSql(store.EntrySchema, store.EntryTable), cancellationToken)
                || !await IsTrueAsync(context, provider.BuildTableHasRowsSql(store.AppSchema, store.AppTable), cancellationToken))
            {
                continue;
            }

            var written = await RebuildOrderAsync(context, order, onlyIfEmpty: true, cancellationToken);
            if (written is { } entries)
            {
                logger.LogInformation(
                    "Page order {PageOrder}: filled {Table} with {Entries} entries for the rows already in {AppTable}.",
                    order.Description,
                    order.Table,
                    entries,
                    store.AppTable);
            }
        }
    }

    private static bool ChangesEntries(EntityEntry entry, IReadOnlyList<SqlOSFgaPageOrder> orders)
        => entry.Property(orders[0].ResourceId.Name).IsModified
            || orders.Any(order => order.SortColumns.Any(column => entry.Property(column.Name).IsModified));

    private static object ProviderKey(EntityEntry entry, IProperty key)
    {
        var value = entry.Property(key.Name).CurrentValue
            ?? throw new InvalidOperationException($"{entry.Metadata.DisplayName()} has no key value to keep its page-order entries.");
        var converter = key.GetTypeMapping().Converter;
        return converter == null ? value : converter.ConvertToProvider(value)!;
    }

    private static void Run(DbContext context, Plan plan)
    {
        foreach (var (sql, parameters) in Statements(context, plan))
        {
            context.Database.ExecuteSqlRaw(sql, parameters);
        }
    }

    private static async Task RunAsync(DbContext context, Plan plan, CancellationToken cancellationToken)
    {
        foreach (var (sql, parameters) in Statements(context, plan))
        {
            await context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
        }
    }

    /// <summary>
    /// Subtree refreshes first, then rows by key: a row that is both under a changed resource and
    /// changed itself is rewritten last by key, so it ends with exactly one set of entries.
    /// </summary>
    private static IEnumerable<(string Sql, DbParameter[] Parameters)> Statements(DbContext context, Plan plan)
    {
        var provider = SqlOSDatabase.Resolve(context.Database);
        var maxDepth = SqlOSFgaHierarchyDepth.Resolve(context.Database, context);

        if (plan.ChangedResources.Count > 0)
        {
            foreach (var order in SqlOSFgaPageOrders.All(context.Model))
            {
                var store = order.Store(maxDepth);
                yield return (
                    provider.BuildDeletePageEntriesBySubtreeSql(store),
                    [provider.CreatePageResourceIdsParameter(ResourceIdsParameter, plan.ChangedResources)]);
                yield return (
                    provider.BuildInsertPageEntriesBySubtreeSql(store),
                    [provider.CreatePageResourceIdsParameter(ResourceIdsParameter, plan.ChangedResources)]);
            }
        }

        foreach (var rows in plan.Rows.Values)
        {
            // Added rows are deleted by key too: a stale entry left by a write that bypassed EF must
            // not make the insert of a reused key fail.
            var refresh = rows.Added.Select(entry => ProviderKey(entry, rows.Key)).Concat(rows.Modified).ToList();
            var delete = refresh.Concat(rows.Deleted).ToList();
            foreach (var order in rows.Orders)
            {
                var store = order.Store(maxDepth);
                if (delete.Count > 0)
                {
                    yield return (
                        provider.BuildDeletePageEntriesByKeysSql(store),
                        [provider.CreatePageKeysParameter(KeysParameter, delete)]);
                }

                if (refresh.Count > 0)
                {
                    yield return (
                        provider.BuildInsertPageEntriesByKeysSql(store),
                        [provider.CreatePageKeysParameter(KeysParameter, refresh)]);
                }
            }
        }
    }

    private static async Task<int?> RebuildOrderAsync(
        DbContext context,
        SqlOSFgaPageOrder order,
        bool onlyIfEmpty,
        CancellationToken cancellationToken)
    {
        var database = context.Database;
        var store = order.Store(SqlOSFgaHierarchyDepth.Resolve(database, context));
        if (database.CurrentTransaction != null || System.Transactions.Transaction.Current != null)
        {
            return await RebuildStoreAsync(context, store, onlyIfEmpty, cancellationToken);
        }

        return await database.CreateExecutionStrategy().ExecuteAsync(
            (Context: context, Store: store, OnlyIfEmpty: onlyIfEmpty),
            static async (_, state, token) =>
            {
                await using var transaction = await state.Context.Database.BeginTransactionAsync(token);
                var written = await RebuildStoreAsync(state.Context, state.Store, state.OnlyIfEmpty, token);
                await transaction.CommitAsync(token);
                return written;
            },
            verifySucceeded: null,
            cancellationToken);
    }

    private static async Task<int?> RebuildStoreAsync(
        DbContext context,
        SqlOSFgaPageOrderStore store,
        bool onlyIfEmpty,
        CancellationToken cancellationToken)
    {
        var provider = SqlOSDatabase.Resolve(context.Database);
        await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
            context.Database,
            $"SqlOS:FgaPageOrder:{store.EntryTable}",
            RebuildLockTimeout,
            $"Could not acquire the SqlOS page order lock for {store.EntryTable}.",
            cancellationToken);

        // Another instance may have filled the table while this one waited for the lock.
        if (onlyIfEmpty
            && await IsTrueAsync(context, provider.BuildTableHasRowsSql(store.EntrySchema, store.EntryTable), cancellationToken))
        {
            return null;
        }

        await context.Database.ExecuteSqlRawAsync(provider.BuildDeleteAllPageEntriesSql(store), cancellationToken);
        return await context.Database.ExecuteSqlRawAsync(provider.BuildInsertAllPageEntriesSql(store), cancellationToken);
    }

    private static async Task<bool> IsTrueAsync(DbContext context, string sql, CancellationToken cancellationToken)
        => (await context.Database.SqlQueryRaw<int>(sql).ToListAsync(cancellationToken)).SingleOrDefault() == 1;
}
