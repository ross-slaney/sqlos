using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SqlOS.Fga.Configuration;

namespace SqlOS.Database;

internal interface ISqlOSDatabaseProvider
{
    SqlOSDatabaseProviderKind Kind { get; }
    string EfProviderName { get; }
    string DisplayName { get; }
    string AuthMigrationResourcePrefix { get; }
    string FgaMigrationResourcePrefix { get; }

    string QuoteIdentifier(string identifier);
    string Qualify(string schema, string name);
    string FilteredIndexIsNotNull(string column);
    string FilteredIndexEqualsTrue(string column);
    string MaxStringStoreType { get; }

    IReadOnlyList<string> SplitBatches(string sql);
    DbParameter CreateParameter(string name, object? value);

    string BuildEnsureAuthVersionTablesSql(string schema);
    string BuildSelectVersionSql(string schema);
    string BuildSelectAppliedMigrationsSql(string schema);
    string BuildRecordAppliedMigrationSql(string schema);
    string BuildUpdateVersionSql(string schema);
    string BuildEnsureFgaVersionTableSql(string schema);
    string BuildSelectFgaVersionSql(string schema);
    string BuildIsResourceAccessibleFunctionSql(SqlOSFgaOptions options);

    /// <summary>The point check as a query over <c>fn_IsResourceAccessible</c>: <c>{0}</c> resource id, <c>{1}</c> subject ids JSON, <c>{2}</c> permission id.</summary>
    string BuildAccessMatchQuerySql(SqlOSFgaOptions options);

    /// <summary>The path from the top of a resource's tree down to it, read from its lineage: <c>{0}</c> resource id.</summary>
    string BuildResourcePathQuerySql(SqlOSFgaOptions options);

    /// <summary><c>fn_ActiveSubjects</c>: the caller's live principal set; the point check and the roots use it.</summary>
    string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_AccessRoots</c>: the caller's access roots (compact key and level), one per grant; <c>fn_ListVisible</c> is built on it.</summary>
    string BuildAccessRootsFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_ListVisible</c>: the resources the caller may see with a permission, listed root by root.</summary>
    string BuildListVisibleFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_VisibleSet</c>: the same resources, each once, materialized; the "list first" filter is an EXISTS over it.</summary>
    string BuildVisibleSetFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_ListFirst</c>: whether the caller sees fewer resources than a table's cap.</summary>
    string BuildListFirstFunctionSql(SqlOSFgaOptions options);

    /// <summary>A SELECT of <c>Value</c> over <c>fn_ListFirst({0}, {1}, {2}, {3})</c>: subject ids JSON, permission id, type id, quoted table name.</summary>
    string BuildListFirstQuerySql(SqlOSFgaOptions options);

    /// <summary>Idempotent batches adding the ancestor columns of the configured depth and the index of each level.</summary>
    IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options);

    /// <summary>Idempotent batches creating the lineage refresh and rebuild routines and the triggers on the resources table.</summary>
    IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options);

    /// <summary>A scalar query: 1 until a rebuild has finished in full (<c>LineageBuilt</c>), else 0.</summary>
    string BuildLineageNeedsBuildSql(SqlOSFgaOptions options);

    string BuildLineageRebuildSql(SqlOSFgaOptions options);

    /// <summary>
    /// A scalar query: the hash of the enforcement routines last applied, or NULL when none is stored or any
    /// routine, trigger, ancestor column, or ancestor index is missing.
    /// </summary>
    string BuildSelectRoutinesHashSql(SqlOSFgaOptions options);

    /// <summary>Stores the routines hash; parameter <c>@RoutinesHash</c>.</summary>
    string BuildStoreRoutinesHashSql(SqlOSFgaOptions options);

    string BuildLockedSelectSql(string schema, string table, string whereSql, string? orderBySql = null);

    string BuildRateLimitIncrementSql(string schema);
    string BuildRateLimitReservePairSql(string schema);
    string BuildRateLimitGetSql(string schema);
    string BuildRateLimitDeleteSql(string schema);
    string BuildRateLimitDecrementSql(string schema);
    string BuildRateLimitReleaseSql(string schema);
    string BuildRateLimitReserveManySql(string schema, int count);
    string BuildRateLimitReleaseManySql(string schema, int count);

    /// <summary>
    /// Takes an exclusive lock held by the connection rather than a transaction, so DDL batches can run in
    /// their own transactions while other processes are kept out. The connection must stay open until
    /// <see cref="ReleaseSessionLockAsync"/>. While the lock is held the session is the preferred deadlock
    /// victim where the engine has such a notion (SQL Server), so a query running beside the DDL never is;
    /// the holder retries a lost deadlock.
    /// </summary>
    Task AcquireSessionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken);

    Task ReleaseSessionLockAsync(DatabaseFacade database, string resource, CancellationToken cancellationToken);

    Task AcquireTransactionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken);
}
