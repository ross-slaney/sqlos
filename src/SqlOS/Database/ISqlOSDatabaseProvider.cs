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
    string BuildResourceAncestorsFunctionSql(SqlOSFgaOptions options);
    string BuildPageNodesFunctionSql(SqlOSFgaOptions options);
    string BuildLockedSelectSql(string schema, string table, string whereSql, string? orderBySql = null);

    // Page-order maintenance. A key list is one parameter from CreatePageKeysParameter and a resource
    // list one from CreatePageResourceIdsParameter; the builders take the parameters' names.
    string BuildDeletePageEntriesByKeysSql(SqlOSFgaPageOrderStore order, string keys);
    string BuildInsertPageEntriesByKeysSql(SqlOSFgaPageOrderStore order, string keys);
    string BuildDeletePageEntriesBySubtreeSql(SqlOSFgaPageOrderStore order, SqlOSFgaChangedResources resources);
    string BuildInsertPageEntriesBySubtreeSql(SqlOSFgaPageOrderStore order, SqlOSFgaChangedResources resources, string? exceptKeys);
    string BuildDeleteAllPageEntriesSql(SqlOSFgaPageOrderStore order);
    string BuildInsertAllPageEntriesSql(SqlOSFgaPageOrderStore order);

    // One save's page-order statements, run in the save's transaction, wrapped for the engine.
    string WrapPageMaintenanceSql(string statements);
    string BuildTableExistsSql(string? schema, string table);
    string BuildTableHasRowsSql(string? schema, string table);
    DbParameter CreatePageKeysParameter(string name, IReadOnlyList<object> keys);
    DbParameter CreatePageResourceIdsParameter(string name, IReadOnlyCollection<string> resourceIds);

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
}
