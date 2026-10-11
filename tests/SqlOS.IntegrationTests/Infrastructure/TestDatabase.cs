using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using SqlOS.Database;

namespace SqlOS.IntegrationTests.Infrastructure;

internal static class TestDatabase
{
    public const string ProviderEnvironmentVariable = "SQLOS_TEST_PROVIDER";

    public static bool IsPostgreSql { get; } = IsPostgreSqlProvider(
        Environment.GetEnvironmentVariable(ProviderEnvironmentVariable));

    public static bool IsSqlServer => !IsPostgreSql;

    public static string BinaryCollation => IsPostgreSql ? "C" : "Latin1_General_100_BIN2";

    public static bool IsPostgreSqlProvider(string? value)
        => value?.Trim().ToLowerInvariant() is "postgresql" or "postgres" or "npgsql";

    public static DbContextOptionsBuilder UseTestProvider(
        this DbContextOptionsBuilder builder,
        string connectionString,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null)
    {
        if (IsPostgreSql)
        {
            SqlOSDatabase.EnablePostgreSqlTimestampCompatibility();
            return sqlServer is null
                ? builder.UseNpgsql(connectionString)
                : builder.UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure());
        }

        return sqlServer is null
            ? builder.UseSqlServer(connectionString)
            : builder.UseSqlServer(connectionString, sqlServer);
    }

    public static DbContextOptionsBuilder<TContext> UseTestProvider<TContext>(
        this DbContextOptionsBuilder<TContext> builder,
        string connectionString,
        Action<SqlServerDbContextOptionsBuilder>? sqlServer = null)
        where TContext : DbContext
    {
        UseTestProvider((DbContextOptionsBuilder)builder, connectionString, sqlServer);
        return builder;
    }

    public static string CreateIsolatedConnectionString(string baseConnectionString, string databaseName)
    {
        if (IsPostgreSql)
        {
            return new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = databaseName
            }.ConnectionString;
        }

        return new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }

    public static string CreateAdminConnectionString(string baseConnectionString)
        => CreateIsolatedConnectionString(baseConnectionString, IsPostgreSql ? "postgres" : "master");

    public static async Task CreateDatabaseAsync(string baseConnectionString, string databaseName, CancellationToken cancellationToken = default)
    {
        if (IsPostgreSql)
        {
            await using var connection = new NpgsqlConnection(CreateAdminConnectionString(baseConnectionString));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""CREATE DATABASE "{databaseName.Replace("\"", "\"\"", StringComparison.Ordinal)}";""";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var sqlConnection = new SqlConnection(CreateAdminConnectionString(baseConnectionString));
        await sqlConnection.OpenAsync(cancellationToken);
        await using var sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.CommandText = $"CREATE DATABASE [{databaseName}]";
        await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task DropDatabaseAsync(string baseConnectionString, string databaseName, CancellationToken cancellationToken = default)
    {
        if (IsPostgreSql)
        {
            await using var connection = new NpgsqlConnection(CreateAdminConnectionString(baseConnectionString));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            var quoted = databaseName.Replace("\"", "\"\"", StringComparison.Ordinal);
            command.CommandText = $"""
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{quoted}' AND pid <> pg_backend_pid();
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"""DROP DATABASE IF EXISTS "{quoted}";""";
            await drop.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var sqlConnection = new SqlConnection(CreateAdminConnectionString(baseConnectionString));
        await sqlConnection.OpenAsync(cancellationToken);
        await using var sqlCommand = sqlConnection.CreateCommand();
        sqlCommand.CommandText = $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await sqlCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    public static string Rewrite(string sql)
    {
        if (!IsPostgreSql)
        {
            return sql;
        }

        if (Regex.IsMatch(sql, @"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase)
            && !sql.Contains("CREATE SCHEMA", StringComparison.OrdinalIgnoreCase))
        {
            sql = """CREATE SCHEMA IF NOT EXISTS "dbo";""" + Environment.NewLine + sql;
        }

        sql = Regex.Replace(sql, @"\bNVARCHAR\s*\(\s*MAX\s*\)", "text", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bVARCHAR\s*\(\s*MAX\s*\)", "text", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bNVARCHAR\s*\(", "varchar(", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bDATETIME2\b", "timestamp", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bBIT\b", "boolean", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bSYSUTCDATETIME\s*\(\s*\)", "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bGETUTCDATE\s*\(\s*\)", "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')", RegexOptions.IgnoreCase);
        sql = Regex.Replace(
            sql,
            @"DATEADD\s*\(\s*(day|hour|minute|second)s?\s*,\s*(-?\d+)\s*,\s*([^)]+)\)",
            "($3 + INTERVAL '$2 $1')",
            RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bLEN\s*\(", "LENGTH(", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bCOLLATE\s+Latin1_General_100_BIN2\b", "COLLATE \"C\"", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"\bCOLLATE\s+Latin1_General_100_CI_AS\b", "", RegexOptions.IgnoreCase);
        sql = Regex.Replace(
            sql,
            @"IF\s+OBJECT_ID\s*\(\s*'[^']+'\s*,\s*'U'\s*\)\s+IS\s+NULL\s+BEGIN\s*(CREATE\s+TABLE)([\s\S]*?)\s*END",
            "CREATE TABLE IF NOT EXISTS$2",
            RegexOptions.IgnoreCase);
        sql = Regex.Replace(
            sql,
            @"OBJECT_ID\s*\(\s*'\[?dbo\]?\.\[?([^\]']+)\]?'\s*(?:,\s*'U')?\s*\)",
            "to_regclass('dbo.$1')",
            RegexOptions.IgnoreCase);
        sql = Regex.Replace(
            sql,
            @"SELECT COUNT\(\*\) FROM sys\.tables WHERE \[name\] = '([^']+)'",
            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'dbo' AND table_name = '$1'",
            RegexOptions.IgnoreCase);
        var hadTopOne = Regex.IsMatch(sql, @"\bSELECT TOP\s+\(?1\)?\s+", RegexOptions.IgnoreCase);
        if (hadTopOne)
        {
            sql = Regex.Replace(sql, @"\bSELECT TOP\s+\(?1\)?\s+", "SELECT ", RegexOptions.IgnoreCase);
        }

        sql = Regex.Replace(sql, @"\[((?:[^\]]|\]\])+)\]", m => "\"" + m.Groups[1].Value.Replace("]]", "]", StringComparison.Ordinal) + "\"");
        sql = Regex.Replace(sql, @"""IsActive""\s*=\s*1\b", "\"IsActive\" = TRUE");
        sql = Regex.Replace(sql, @"""IsEnabled""\s*=\s*1\b", "\"IsEnabled\" = TRUE");
        sql = Regex.Replace(sql, @"""IsActive""\s*=\s*0\b", "\"IsActive\" = FALSE");
        sql = Regex.Replace(sql, @"""IsEnabled""\s*=\s*0\b", "\"IsEnabled\" = FALSE");
        sql = Regex.Replace(sql, @",\s*1\s*,\s*'", ", TRUE, '");
        sql = Regex.Replace(sql, @",\s*0\s*,\s*'", ", FALSE, '");
        sql = Regex.Replace(sql, @",\s*1\s*,\s*\(", ", TRUE, (");
        sql = Regex.Replace(sql, @",\s*0\s*,\s*\(", ", FALSE, (");
        if (hadTopOne && !sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase))
        {
            sql += " LIMIT 1";
        }

        return sql;
    }

    public static void ClearPools()
    {
        if (IsPostgreSql)
        {
            NpgsqlConnection.ClearAllPools();
            return;
        }

        SqlConnection.ClearAllPools();
    }

    public static string Quote(string identifier)
        => IsPostgreSql
            ? "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Qualify(string table)
        => $"{Quote("dbo")}.{Quote(table)}";

    public static DbConnection CreateConnection(string connectionString)
        => IsPostgreSql
            ? new NpgsqlConnection(connectionString)
            : new SqlConnection(connectionString);

    public static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
