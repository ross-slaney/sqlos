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

    /// <summary>The point check as a query over <c>fn_IsResourceAccessible</c>: <c>{0}</c> resource id, <c>{1}</c> subject ids JSON, <c>{2}</c> permission id.</summary>
    string BuildAccessMatchQuerySql(SqlOSFgaOptions options);

    /// <summary>The path from the top of a resource's tree down to it, read from its lineage: <c>{0}</c> resource id.</summary>
    string BuildResourcePathQuerySql(SqlOSFgaOptions options);

    /// <summary><c>fn_ActiveSubjects</c>: the caller's live principal set; the point check and the roots use it.</summary>
    string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_AccessRoots</c>: the caller's access roots (compact key and level), for the row filter.</summary>
    string BuildAccessRootsFunctionSql(SqlOSFgaOptions options);

    /// <summary>A SELECT of <c>ResourceSeq, Depth</c> over <c>fn_AccessRoots({0}, {1})</c>.</summary>
    string BuildAccessRootsQuerySql(SqlOSFgaOptions options);

    /// <summary>Idempotent batches adding the ancestor columns of the configured depth.</summary>
    IReadOnlyList<string> BuildEnsureLineageColumnsSql(SqlOSFgaOptions options);

    /// <summary>
    /// Idempotent batches creating the lineage refresh and rebuild routines, the triggers on the resources
    /// table, and the triggers on each application table that carries the scope column.
    /// </summary>
    IReadOnlyList<string> BuildLineageMaintenanceSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>A scalar query: 1 until a rebuild has finished in full (<c>LineageBuilt</c>), else 0.</summary>
    string BuildLineageNeedsBuildSql(SqlOSFgaOptions options);

    string BuildLineageRebuildSql(SqlOSFgaOptions options);

    /// <summary>Runs the scope fill routine: every application row without a scope gets its resource's.</summary>
    string BuildScopeFillSql(SqlOSFgaOptions options);

    /// <summary>A scalar query: 1 when the application table exists with its scope column, else 0.</summary>
    string BuildScopeTableReadySql(SqlOSFgaScopeTable table);

    /// <summary>One idempotent batch dropping SqlOS's stale objects (of renamed or no longer protected tables, or of orders no longer declared) from every table.</summary>
    string BuildScopeCleanupSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>
    /// Idempotent batches creating, per application table, the per-level indexes on its projection's scope
    /// column (and on SQL Server the computed columns they are built on), the type statistics, and on the
    /// application table itself the index on rows without a scope.
    /// </summary>
    IReadOnlyList<string> BuildEnsureScopeIndexesSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>
    /// A scalar query: the hash of the enforcement routines last applied, or NULL when none is stored or any
    /// routine, trigger, or ancestor column is missing.
    /// </summary>
    string BuildSelectRoutinesHashSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>Stores the routines hash; parameter <c>@RoutinesHash</c>.</summary>
    string BuildStoreRoutinesHashSql(SqlOSFgaOptions options);

    /// <summary>
    /// Idempotent batches creating what a page needs beyond the lineage: the grant-count routines, the direct
    /// index of each application table with its rebuild routine, the triggers on the grants table, and the
    /// routine that rebuilds all of it (see <see cref="SqlOS.Fga.Paging.SqlOSFgaPageIndex"/>).
    /// </summary>
    IReadOnlyList<string> BuildPageIndexSql(SqlOSFgaOptions options, IReadOnlyList<SqlOSFgaScopeTable> scopeTables);

    /// <summary>Rebuilds the grant counts, every projection and every direct index from the grants, the lineage and the rows.</summary>
    string BuildPageIndexRebuildSql(SqlOSFgaOptions options);

    /// <summary>
    /// The query a planned statement reads an application table through: its projection joined to the row,
    /// the scope from the projection and every other column from the row (see <see cref="SqlOS.Fga.SqlOSFgaScopeIndex"/>).
    /// </summary>
    string BuildScopeIndexQuerySql(SqlOSFgaOptions options, SqlOSFgaScopeTable table);

    /// <summary>Rebuilds the counts of the principals whose grants crossed a validity boundary in <c>(@From, @To]</c>; returns how many.</summary>
    string BuildCountsRefreshSql(SqlOSFgaOptions options);

    /// <summary>Rebuilds the counts of the principals in <c>@Subjects</c> (a JSON array of subject ids).</summary>
    string BuildCountsRebuildSql(SqlOSFgaOptions options);

    /// <summary>A scalar query: the next moment after <c>@Now</c> a grant's window opens or closes, or NULL.</summary>
    string BuildNextValidityBoundarySql(SqlOSFgaOptions options);

    /// <summary>
    /// Four result sets: the caller's live subjects, the roles carrying the permission, the permission's type
    /// with the root's key, and the live subjects whose grant counts fell behind the clock (a grant's window
    /// opened or closed since they were built). Parameters <c>@SubjectIds</c>, <c>@PermissionId</c>, <c>@RootId</c>.
    /// </summary>
    string BuildPagePreludeSql(SqlOSFgaOptions options);

    /// <summary>The two statements of a page round (see the SQL Server provider).</summary>
    string BuildPageRoundSql(SqlOSFgaOptions options, SqlOS.Fga.Paging.SqlOSFgaPageSpec spec);
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
