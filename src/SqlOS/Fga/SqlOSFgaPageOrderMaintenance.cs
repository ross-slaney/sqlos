using System.Data.Common;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SqlOS.Database;
using SqlOS.Fga.Models;
using SqlOS.Fga.Paging;

namespace SqlOS.Fga;

/// <summary>
/// Keeps page-order entries equal to what a rebuild would write. <c>SqlOSDbContext&lt;TContext&gt;</c>
/// sends a save that changes a resource, or a row of an entity with a page order, through here: the
/// save, then the entries of the rows it changed, in one transaction, so rows and entries never disagree.
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
    private const string SavepointName = "SqlOSPageOrders";
    private static readonly TimeSpan RebuildLockTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The rows and resources one save changed.</summary>
    private sealed class Plan
    {
        /// <summary>The provider keys of the changed rows, by the root entity type that declares the orders.</summary>
        public Dictionary<IEntityType, List<object>> Keys { get; } = [];
        public HashSet<string> ChangedResources { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a save can change entries: it adds, modifies or deletes a resource or a row of an
    /// entity with a page order. Read before the save, so that the save and the entries can share a transaction.
    /// </summary>
    public static bool MayChange(DbContext context)
    {
        if (!context.Database.IsRelational() || SqlOSFgaPageOrders.All(context.Model).Count == 0)
        {
            return false;
        }

        return context.ChangeTracker.Entries().Any(entry =>
            entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && (entry.Entity is SqlOSFgaResource || SqlOSFgaPageOrders.For(entry.Metadata).Count > 0));
    }

    /// <summary>
    /// Collects, after the save and before its changes are accepted, the rows whose entries it
    /// changed: added and deleted rows; modified rows whose <c>ResourceId</c> or an order column
    /// changed, or that have an order column the database computes on update; and resources added,
    /// deleted, or whose parent or activity changed. Reading after the save sees what the save wrote,
    /// including changes made by <c>SaveChanges</c> interceptors and <c>SavingChanges</c> handlers,
    /// cascade deletes and generated keys. Returns <see langword="null"/> when there is nothing to do.
    /// </summary>
    private static Plan? Collect(DbContext context)
    {
        var tracker = context.ChangeTracker;
        var detectChanges = tracker.AutoDetectChangesEnabled;

        // The save has detected changes. Detecting again would pick up values set after it, which it did not write.
        tracker.AutoDetectChangesEnabled = false;
        try
        {
            Plan? plan = null;
            foreach (var entry in tracker.Entries())
            {
                if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                {
                    continue;
                }

                if (entry.Entity is SqlOSFgaResource resource)
                {
                    // An added resource can be the resource of rows saved before it, or of rows whose
                    // resource was deleted and is added again.
                    if (entry.State != EntityState.Modified
                        || entry.Property(nameof(SqlOSFgaResource.ParentId)).IsModified
                        || entry.Property(nameof(SqlOSFgaResource.IsActive)).IsModified)
                    {
                        (plan ??= new Plan()).ChangedResources.Add(resource.Id);
                    }

                    continue;
                }

                var orders = SqlOSFgaPageOrders.For(entry.Metadata);
                if (orders.Count == 0 || (entry.State == EntityState.Modified && !ChangesEntries(entry, orders)))
                {
                    continue;
                }

                plan ??= new Plan();
                var root = orders[0].EntityType;
                if (!plan.Keys.TryGetValue(root, out var keys))
                {
                    keys = [];
                    plan.Keys.Add(root, keys);
                }

                keys.Add(ProviderKey(entry, orders[0].Key));
            }

            return plan;
        }
        finally
        {
            tracker.AutoDetectChangesEnabled = detectChanges;
        }
    }

    /// <summary>
    /// Saves, then rewrites the entries of the rows the save changed, in one transaction: the
    /// caller's, inside a savepoint so that a failure leaves it as it was before the save (as EF Core
    /// does for the save alone); an ambient or enlisted one; or one owned here, inside the context's
    /// execution strategy so that retrying strategies keep working.
    /// </summary>
    public static int SaveChanges(DbContext context, Func<bool, int> saveChanges, bool acceptAllChangesOnSuccess)
    {
        var database = context.Database;
        int saved;
        if (database.CurrentTransaction is { } transaction)
        {
            saved = InSavepoint(database, transaction, () => SaveAndRewrite(context, saveChanges));
        }
        else if (HasSystemTransaction(database))
        {
            saved = SaveAndRewrite(context, saveChanges);
        }
        else
        {
            saved = database.CreateExecutionStrategy().Execute(
                (Context: context, Save: saveChanges),
                static (_, state) =>
                {
                    using var transaction = state.Context.Database.BeginTransaction();
                    var count = SaveAndRewrite(state.Context, state.Save);
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
        Func<bool, CancellationToken, Task<int>> saveChangesAsync,
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        var database = context.Database;
        int saved;
        if (database.CurrentTransaction is { } transaction)
        {
            saved = await InSavepointAsync(
                database,
                transaction,
                token => SaveAndRewriteAsync(context, saveChangesAsync, token),
                cancellationToken);
        }
        else if (HasSystemTransaction(database))
        {
            saved = await SaveAndRewriteAsync(context, saveChangesAsync, cancellationToken);
        }
        else
        {
            saved = await database.CreateExecutionStrategy().ExecuteAsync(
                (Context: context, Save: saveChangesAsync),
                static async (_, state, token) =>
                {
                    await using var transaction = await state.Context.Database.BeginTransactionAsync(token);
                    var count = await SaveAndRewriteAsync(state.Context, state.Save, token);
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
        => Changed(entry.Property(orders[0].ResourceId.Name))
            || orders.Any(order => order.SortColumns.Any(column => Changed(entry.Property(column.Name))));

    // A column the database computes on update can have a new value whenever the row is updated.
    private static bool Changed(PropertyEntry property)
        => property.IsModified
            || property.Metadata.ValueGenerated is ValueGenerated.OnUpdate or ValueGenerated.OnAddOrUpdate or ValueGenerated.OnUpdateSometimes;

    private static object ProviderKey(EntityEntry entry, IProperty key)
    {
        var value = entry.Property(key.Name).CurrentValue
            ?? throw new InvalidOperationException($"{entry.Metadata.DisplayName()} has no key value to keep its page-order entries.");
        var converter = key.GetTypeMapping().Converter;
        return converter == null ? value : converter.ConvertToProvider(value)!;
    }

    // EF Core does not begin its own transaction under these either.
    private static bool HasSystemTransaction(DatabaseFacade database)
        => Transaction.Current != null || database.GetEnlistedTransaction() != null;

    private static int SaveAndRewrite(DbContext context, Func<bool, int> saveChanges)
    {
        var saved = saveChanges(false);
        if (Command(context) is { } command)
        {
            context.Database.ExecuteSqlRaw(command.Sql, command.Parameters);
        }

        return saved;
    }

    private static async Task<int> SaveAndRewriteAsync(
        DbContext context,
        Func<bool, CancellationToken, Task<int>> saveChangesAsync,
        CancellationToken cancellationToken)
    {
        var saved = await saveChangesAsync(false, cancellationToken);
        if (Command(context) is { } command)
        {
            await context.Database.ExecuteSqlRawAsync(command.Sql, command.Parameters, cancellationToken);
        }

        return saved;
    }

    private static int InSavepoint(DatabaseFacade database, IDbContextTransaction transaction, Func<int> save)
    {
        if (!database.AutoSavepointsEnabled || !transaction.SupportsSavepoints)
        {
            return save();
        }

        transaction.CreateSavepoint(SavepointName);
        int saved;
        try
        {
            saved = save();
        }
        catch
        {
            try
            {
                transaction.RollbackToSavepoint(SavepointName);
            }
            catch (Exception)
            {
                // The transaction cannot return to the savepoint (SQL Server dooms it under XACT_ABORT,
                // or the connection broke); it can only roll back, and the save's own error says why.
            }

            throw;
        }

        transaction.ReleaseSavepoint(SavepointName);
        return saved;
    }

    private static async Task<int> InSavepointAsync(
        DatabaseFacade database,
        IDbContextTransaction transaction,
        Func<CancellationToken, Task<int>> save,
        CancellationToken cancellationToken)
    {
        if (!database.AutoSavepointsEnabled || !transaction.SupportsSavepoints)
        {
            return await save(cancellationToken);
        }

        await transaction.CreateSavepointAsync(SavepointName, cancellationToken);
        int saved;
        try
        {
            saved = await save(cancellationToken);
        }
        catch
        {
            try
            {
                // Not the caller's token: a cancelled save still has to leave the transaction as it was.
                await transaction.RollbackToSavepointAsync(SavepointName, CancellationToken.None);
            }
            catch (Exception)
            {
                // The transaction cannot return to the savepoint (SQL Server dooms it under XACT_ABORT,
                // or the connection broke); it can only roll back, and the save's own error says why.
            }

            throw;
        }

        await transaction.ReleaseSavepointAsync(SavepointName, cancellationToken);
        return saved;
    }

    /// <summary>
    /// The statements for what the save changed, as one command. For each order, the entries of the
    /// rows under the changed resources and of the changed rows are deleted, then written from the
    /// rows as they are now; the subtree insert skips the changed rows, which the key insert writes.
    /// An order whose entry table startup found missing is skipped: its rows are written, and the
    /// first start after the table exists fills it.
    /// </summary>
    private static (string Sql, DbParameter[] Parameters)? Command(DbContext context)
    {
        if (Collect(context) is not { } plan)
        {
            return null;
        }

        var provider = SqlOSDatabase.Resolve(context.Database);
        var maxDepth = SqlOSFgaHierarchyDepth.Resolve(context.Database, context);
        var resourceIds = plan.ChangedResources.Count > 0 ? ResourceIdsParameter : null;
        var keyParameters = new Dictionary<IEntityType, string>();
        var parameters = new List<DbParameter>();
        var statements = new List<string>();

        foreach (var order in SqlOSFgaPageOrders.All(context.Model))
        {
            if (SqlOSFgaPageOrders.IsTableMissing(context.Model, order))
            {
                continue;
            }

            string? keys = null;
            if (plan.Keys.TryGetValue(order.EntityType, out var rowKeys)
                && !keyParameters.TryGetValue(order.EntityType, out keys))
            {
                keys = $"{KeysParameter}{keyParameters.Count}";
                keyParameters.Add(order.EntityType, keys);
                parameters.Add(provider.CreatePageKeysParameter(keys, rowKeys));
            }

            if (resourceIds == null && keys == null)
            {
                continue;
            }

            var store = order.Store(maxDepth);
            if (resourceIds != null)
            {
                statements.Add(provider.BuildDeletePageEntriesBySubtreeSql(store, resourceIds));
            }

            if (keys != null)
            {
                // Added rows too: a stale entry left by a write that bypassed EF must not make the
                // insert of a reused key fail.
                statements.Add(provider.BuildDeletePageEntriesByKeysSql(store, keys));
            }

            if (resourceIds != null)
            {
                statements.Add(provider.BuildInsertPageEntriesBySubtreeSql(store, resourceIds, keys));
            }

            if (keys != null)
            {
                statements.Add(provider.BuildInsertPageEntriesByKeysSql(store, keys));
            }
        }

        if (statements.Count == 0)
        {
            return null;
        }

        if (resourceIds != null)
        {
            parameters.Add(provider.CreatePageResourceIdsParameter(resourceIds, plan.ChangedResources));
        }

        return (string.Join("\n", statements), parameters.ToArray());
    }

    private static async Task<int?> RebuildOrderAsync(
        DbContext context,
        SqlOSFgaPageOrder order,
        bool onlyIfEmpty,
        CancellationToken cancellationToken)
    {
        var database = context.Database;
        var store = order.Store(SqlOSFgaHierarchyDepth.Resolve(database, context));
        if (database.CurrentTransaction != null || HasSystemTransaction(database))
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
        var database = context.Database;
        var provider = SqlOSDatabase.Resolve(database);

        // A rebuild writes an entry per row per level and can take minutes on a large table, as can
        // waiting for another instance's rebuild. It runs without a command timeout; the lock has its
        // own, and the cancellation token stops it.
        var commandTimeout = database.GetCommandTimeout();
        database.SetCommandTimeout(0);
        try
        {
            await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(
                database,
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

            await database.ExecuteSqlRawAsync(provider.BuildDeleteAllPageEntriesSql(store), cancellationToken);
            return await database.ExecuteSqlRawAsync(provider.BuildInsertAllPageEntriesSql(store), cancellationToken);
        }
        finally
        {
            database.SetCommandTimeout(commandTimeout);
        }
    }

    private static async Task<bool> IsTrueAsync(DbContext context, string sql, CancellationToken cancellationToken)
        => (await context.Database.SqlQueryRaw<int>(sql).ToListAsync(cancellationToken)).SingleOrDefault() == 1;
}
