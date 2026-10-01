using Microsoft.Data.SqlClient;
using Npgsql;

namespace SqlOS.Benchmarks.Infrastructure;

/// <summary>
/// The row filter as the previous SqlOS release shipped it, created under the name
/// <c>fn_IsResourceAccessible_Reference</c> so the current function can be measured against it on the same
/// data in the same job. The SQL is the previous provider output for the default schema and table names.
/// </summary>
internal static class ReferenceFunction
{
    public const string Name = "fn_IsResourceAccessible_Reference";

    public static async Task CreateAsync(DatabaseProvider provider, string connectionString, CancellationToken cancellationToken)
    {
        var file = Path.Combine(
            AppContext.BaseDirectory,
            "Reference",
            provider == DatabaseProvider.PostgreSql
                ? "fn_IsResourceAccessible_Reference.postgresql.sql"
                : "fn_IsResourceAccessible_Reference.sqlserver.sql");
        var sql = await File.ReadAllTextAsync(file, cancellationToken);

        if (provider == DatabaseProvider.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var sqlConnection = new SqlConnection(connectionString);
        await sqlConnection.OpenAsync(cancellationToken);
        await using var sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.CommandText = sql;
        await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
