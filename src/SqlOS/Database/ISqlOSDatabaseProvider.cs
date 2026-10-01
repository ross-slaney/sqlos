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

    /// <summary><c>fn_ActiveSubjects</c>: the caller's live principal set; the row filter and the roots use it.</summary>
    string BuildActiveSubjectsFunctionSql(SqlOSFgaOptions options);

    /// <summary><c>fn_AccessRoots</c>: the caller's granted resources, for the authorized page only.</summary>
    string BuildAccessRootsFunctionSql(SqlOSFgaOptions options);

    /// <summary>Idempotent batches that create the closure's apply and rebuild routines and its triggers.</summary>
    IReadOnlyList<string> BuildResourceClosureMaintenanceSql(SqlOSFgaOptions options);

    /// <summary>A scalar query: 1 when the closure is empty although resources with parents exist, else 0.</summary>
    string BuildResourceClosureNeedsBuildSql(SqlOSFgaOptions options);
    string BuildResourceClosureRebuildSql(SqlOSFgaOptions options);

    /// <summary>
    /// One authorized page of resources: columns <c>ResourceId</c> and <c>Seq</c>, ordered by <c>Seq</c>, as a
    /// single composable SELECT (no CTE). Parameters <c>@SubjectIds</c>, <c>@PermissionId</c>,
    /// <c>@ResourceTypeId</c>, <c>@Cursor</c>, <c>@PageSize</c>.
    /// </summary>
    string BuildVisibleResourcesPageSql(SqlOSFgaOptions options);

    /// <summary>
    /// A scalar query: the hash of the enforcement routines last applied, or NULL when none is stored or any
    /// routine (the two functions, the closure's apply and rebuild routines, its three triggers) is missing.
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

    Task AcquireTransactionLockAsync(
        DatabaseFacade database,
        string resource,
        TimeSpan timeout,
        string failureMessage,
        CancellationToken cancellationToken);

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
}
