using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SqlOS.Fga;
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

    /// <summary><c>fn_ActiveSubjects</c>: the caller's live principal set; the point check and the roots use it.</summary>
    string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_AccessRoots</c>: the caller's access roots (compact key and level), for the row filter.</summary>
    string BuildAccessRootsFunctionSql(SqlOSFgaOptions options);

    /// <summary>A composable SELECT of <c>SubjectId</c> over <c>fn_ActiveSubjects({0})</c>.</summary>
    string BuildActiveSubjectsQuerySql(SqlOSFgaOptions options);

    /// <summary>A composable SELECT of <c>ResourceSeq, Depth</c> over <c>fn_AccessRoots({0}, {1})</c>.</summary>
    string BuildAccessRootsQuerySql(SqlOSFgaOptions options);

    /// <summary>Idempotent batches adding the ancestor columns of the configured depth and their indexes.</summary>
    IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options);

    /// <summary>
    /// Idempotent batches creating the lineage refresh and rebuild routines, the triggers on the resources
    /// table, and the triggers on each application table that carries the scope columns.
    /// </summary>
    IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>A scalar query: 1 when the lineage was never built (a root without a depth), else 0.</summary>
    string BuildLineageNeedsBuildSql(SqlOSFgaOptions options);

    string BuildLineageRebuildSql(SqlOSFgaOptions options);

    /// <summary>Fills the scope columns of every row of one application table from the lineage.</summary>
    string BuildScopeFillSql(SqlOSFgaOptions options, SqlOSFgaScopeTable table);

    /// <summary>
    /// A scalar query: the hash of the enforcement routines last applied, or NULL when none is stored or any
    /// routine, trigger, or ancestor column is missing.
    /// </summary>
    string BuildSelectRoutinesHashSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

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
    /// <see cref="ReleaseSessionLockAsync"/>.
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
