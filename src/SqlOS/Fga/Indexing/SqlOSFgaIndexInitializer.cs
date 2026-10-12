using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SqlOS.Database;
using SqlOS.Fga.Configuration;

namespace SqlOS.Fga.Indexing;

internal static class SqlOSFgaIndexInitializer
{
    /// <remarks>
    /// Run through normal SqlOS startup, outside application-owned or ambient transactions.
    /// Installation owns and commits its transaction before enabling indexed query execution
    /// in other contexts; an uncommitted or rolled-back installation must never be advertised.
    /// </remarks>
    internal static async Task EnsureAsync(DbContext context, SqlOSFgaOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        if (!SqlOSFgaNativeOptions.Enabled(context) || !context.Database.IsRelational()) return;
        var providerName = context.Database.ProviderName!;
        if (!SqlOSDatabase.IsPostgreSql(providerName) && !SqlOSDatabase.IsSqlServer(providerName)) return;
        var postgres = SqlOSDatabase.IsPostgreSql(providerName);
        var provider = SqlOSDatabase.Resolve(providerName);
        if (context.Database.CurrentTransaction is not null || System.Transactions.Transaction.Current is not null)
            throw new InvalidOperationException(
                "SqlOS native FGA index initialization must run outside an application transaction. "
                + "Use normal SqlOS startup, or call initialization before beginning the application's transaction.");
        await using var transaction = await context.Database.BeginTransactionAsync(
            SqlOSDatabase.ExclusiveWorkIsolationLevel(context.Database), cancellationToken);
        await SqlOSDatabase.AcquireExclusiveTransactionLockAsync(context.Database,
            $"SqlOS:FgaIndexes:{options.Schema}", TimeSpan.FromSeconds(30),
            "Could not acquire the SqlOS FGA index initialization lock.", cancellationToken);

        var discovery = await SqlOSFgaScopeModel.ResolveAsync(context, options, cancellationToken);
        foreach (var reason in discovery.Skipped)
            logger.LogWarning("SqlOS native FGA index: {Reason}", reason);
        if (discovery.Entities.Count == 0 && !await CatalogExistsAsync(context, options, postgres, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            SqlOSFgaNativeOptions.Register(context, discovery);
            return;
        }

        var core = SqlOSFgaClosureSql.Build(options, postgres);
        foreach (var sql in core) await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        var catalog = provider.Qualify(options.Schema, SqlOSFgaClosureSql.CatalogTable);
        var owners = provider.Qualify(options.Schema, SqlOSFgaClosureSql.OwnersTable);
        // Remains stable across model/schema changes; never includes assembly version or model hash.
        var ownerKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            context.GetType().Assembly.GetName().Name + ":" + (context.GetType().FullName ?? context.GetType().Name))));
        string Quote(string name) => postgres ? $"\"{name}\"" : $"[{name}]";
        var installed = new Dictionary<string, InstalledIndex>(StringComparer.Ordinal);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"SELECT {Quote("Id")}, {Quote("ModelHash")}, {Quote("DropSql")} FROM {catalog}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                installed.Add(reader.GetString(0), new(reader.GetString(1), reader.GetString(2)));
        }
        var claims = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        await using (var command = context.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
            command.CommandText = $"SELECT {Quote("ProjectionId")}, {Quote("OwnerKey")} FROM {owners}";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var projectionId = reader.GetString(0);
                if (!claims.TryGetValue(projectionId, out var projectionOwners))
                    claims.Add(projectionId, projectionOwners = new(StringComparer.Ordinal));
                projectionOwners.Add(reader.GetString(1));
            }
        }
        var currentIds = discovery.Entities.Select(e => e.ProjectionTable).ToHashSet(StringComparer.Ordinal);
        foreach (var (projectionId, projectionOwners) in claims)
        {
            if (!projectionOwners.Contains(ownerKey) || currentIds.Contains(projectionId)) continue;
            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {owners} WHERE {Quote("OwnerKey")} = @owner AND {Quote("ProjectionId")} = @projection",
                [provider.CreateParameter("@owner", ownerKey), provider.CreateParameter("@projection", projectionId)], cancellationToken);
            projectionOwners.Remove(ownerKey);
            if (projectionOwners.Count != 0) continue;
            if (!installed.TryGetValue(projectionId, out var obsolete) || string.IsNullOrWhiteSpace(obsolete.DropSql))
                throw new InvalidOperationException($"SqlOS cannot remove obsolete index '{projectionId}' because its generated removal statement is missing.");
            await context.Database.ExecuteSqlRawAsync(obsolete.DropSql, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                $"DELETE FROM {catalog} WHERE {Quote("Id")} = @projection",
                [provider.CreateParameter("@projection", projectionId)], cancellationToken);
            installed.Remove(projectionId);
        }
        var coreHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", core))));
        var coreChanged = !installed.TryGetValue("__core", out var previousCore) || previousCore.ModelHash != coreHash;
        var changed = discovery.Entities.Where(entity =>
            !installed.TryGetValue(entity.ProjectionTable, out var index) || index.ModelHash != entity.ModelHash).ToArray();
        foreach (var entity in changed)
        {
            if (claims.TryGetValue(entity.ProjectionTable, out var projectionOwners)
                && projectionOwners.Any(existingOwner => existingOwner != ownerKey))
                throw new InvalidOperationException(
                    $"SqlOS table '{entity.SourceSchema}.{entity.SourceTable}' is already indexed by another DbContext with a different model. "
                    + "Contexts sharing a table must agree on its native FGA index columns and declared orders.");
            // Refresh callbacks must refer to the current schema before a closure rebuild runs.
            if (installed.ContainsKey(entity.ProjectionTable))
                await context.Database.ExecuteSqlRawAsync(SqlOSFgaProjectionSql.BuildDrop(entity, options, providerName), cancellationToken);
            await context.Database.ExecuteSqlRawAsync(SqlOSFgaProjectionSql.BuildCreate(entity, options, providerName), cancellationToken);
            foreach (var sql in SqlOSFgaProjectionSql.BuildTriggers(entity, options, providerName))
                await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            await context.Database.ExecuteSqlRawAsync(SqlOSFgaProjectionSql.BuildCatalogRegistration(entity, options, providerName), cancellationToken);
            // Resource-side rebuild callbacks use these indexed row bindings. Populate
            // new/changed maps before rebuilding closure or any projection through them.
            await context.Database.ExecuteSqlRawAsync(SqlOSFgaProjectionSql.BuildRebuildBindings(entity, options, providerName), cancellationToken);
        }
        foreach (var entity in discovery.Entities)
        {
            if (claims.TryGetValue(entity.ProjectionTable, out var projectionOwners) && projectionOwners.Contains(ownerKey)) continue;
            await context.Database.ExecuteSqlRawAsync(
                $"INSERT INTO {owners} ({Quote("OwnerKey")}, {Quote("ProjectionId")}) VALUES (@owner, @projection)",
                [provider.CreateParameter("@owner", ownerKey), provider.CreateParameter("@projection", entity.ProjectionTable)], cancellationToken);
        }
        if (coreChanged)
        {
            foreach (var sql in SqlOSFgaClosureSql.Rebuild(options, postgres))
                await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            var catalogSql = postgres
                ? $"INSERT INTO {catalog} (\"Id\", \"ModelHash\", \"RefreshSql\") VALUES ('__core', '{{0}}', '') ON CONFLICT (\"Id\") DO UPDATE SET \"ModelHash\" = EXCLUDED.\"ModelHash\""
                : $"UPDATE {catalog} SET [ModelHash] = '{{0}}' WHERE [Id] = '__core'; IF @@ROWCOUNT = 0 INSERT INTO {catalog} ([Id], [ModelHash], [RefreshSql]) VALUES ('__core', '{{0}}', '')";
            // The digest is library-generated hexadecimal, not application input.
            await context.Database.ExecuteSqlRawAsync(catalogSql.Replace("{0}", coreHash, StringComparison.Ordinal), cancellationToken);
        }
        else
        {
            foreach (var entity in changed)
                await context.Database.ExecuteSqlRawAsync(SqlOSFgaProjectionSql.BuildRebuild(entity, options, providerName), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        SqlOSFgaNativeOptions.Register(context, discovery);
        logger.LogInformation("SqlOS native FGA indexes ready for {Tables} application tables ({Rebuilt} rebuilt).",
            discovery.Entities.Count, changed.Length);
    }

    private sealed record InstalledIndex(string ModelHash, string DropSql);

    private static async Task<bool> CatalogExistsAsync(DbContext context, SqlOSFgaOptions options, bool postgres, CancellationToken cancellationToken)
    {
        var provider = SqlOSDatabase.Resolve(context.Database);
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = postgres
            ? "SELECT pg_catalog.to_regclass(@table) IS NOT NULL"
            : "SELECT CASE WHEN OBJECT_ID(@table, 'U') IS NULL THEN 0 ELSE 1 END";
        command.Parameters.Add(provider.CreateParameter("@table", provider.Qualify(options.Schema, SqlOSFgaClosureSql.CatalogTable)));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }
}
