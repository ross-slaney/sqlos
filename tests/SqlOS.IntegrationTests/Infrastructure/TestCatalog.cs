using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace SqlOS.IntegrationTests.Infrastructure;

internal static class TestCatalog
{
    public static Task<bool> TableExistsAsync(DbContext context, string tableName)
        => ExistsAsync(
            context,
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'dbo' AND table_name = @name
            """,
            ("@name", tableName));

    public static Task<bool> ColumnExistsAsync(DbContext context, string tableName, string columnName)
        => ExistsAsync(
            context,
            """
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @tableName AND column_name = @columnName
            """,
            ("@tableName", tableName),
            ("@columnName", columnName));

    public static Task<bool> IndexExistsAsync(DbContext context, string tableName, string indexName)
        => ExistsAsync(
            context,
            TestDatabase.IsPostgreSql
                ? """
                  SELECT COUNT(*)
                  FROM pg_indexes
                  WHERE schemaname = 'dbo' AND tablename = @tableName AND indexname = @indexName
                  """
                : """
                  SELECT COUNT(*)
                  FROM sys.indexes i
                  INNER JOIN sys.tables t ON i.object_id = t.object_id
                  WHERE t.name = @tableName
                    AND i.name = @indexName
                    AND t.schema_id = SCHEMA_ID('dbo')
                  """,
            ("@tableName", tableName),
            ("@indexName", indexName));

    public static Task<bool> ForeignKeyExistsAsync(DbContext context, string tableName, string foreignKeyName)
        => ExistsAsync(
            context,
            """
            SELECT COUNT(*)
            FROM information_schema.table_constraints
            WHERE table_schema = 'dbo'
              AND table_name = @tableName
              AND constraint_name = @foreignKeyName
              AND constraint_type = 'FOREIGN KEY'
            """,
            ("@tableName", tableName),
            ("@foreignKeyName", foreignKeyName));

    public static Task<int> ColumnIsNullableAsync(DbContext context, string tableName, string columnName)
        => ScalarIntAsync(
            context,
            """
            SELECT CASE WHEN is_nullable = 'YES' THEN 1 ELSE 0 END
            FROM information_schema.columns
            WHERE table_schema = 'dbo' AND table_name = @tableName AND column_name = @columnName
            """,
            ("@tableName", tableName),
            ("@columnName", columnName));

    public static Task<int> IndexIsUniqueAsync(DbContext context, string tableName, string indexName)
        => ScalarIntAsync(
            context,
            TestDatabase.IsPostgreSql
                ? """
                  SELECT CASE WHEN i.indisunique THEN 1 ELSE 0 END
                  FROM pg_index i
                  JOIN pg_class t ON t.oid = i.indrelid
                  JOIN pg_class ix ON ix.oid = i.indexrelid
                  JOIN pg_namespace n ON n.oid = t.relnamespace
                  WHERE n.nspname = 'dbo' AND t.relname = @tableName AND ix.relname = @indexName
                  """
                : """
                  SELECT CAST(i.is_unique AS INT)
                  FROM sys.indexes i
                  INNER JOIN sys.tables t ON i.object_id = t.object_id
                  WHERE t.name = @tableName AND i.name = @indexName AND t.schema_id = SCHEMA_ID('dbo')
                  """,
            ("@tableName", tableName),
            ("@indexName", indexName));

    public static Task<int> IndexHasFilterAsync(DbContext context, string tableName, string indexName)
        => ScalarIntAsync(
            context,
            TestDatabase.IsPostgreSql
                ? """
                  SELECT CASE WHEN i.indpred IS NOT NULL THEN 1 ELSE 0 END
                  FROM pg_index i
                  JOIN pg_class t ON t.oid = i.indrelid
                  JOIN pg_class ix ON ix.oid = i.indexrelid
                  JOIN pg_namespace n ON n.oid = t.relnamespace
                  WHERE n.nspname = 'dbo' AND t.relname = @tableName AND ix.relname = @indexName
                  """
                : """
                  SELECT CAST(i.has_filter AS INT)
                  FROM sys.indexes i
                  INNER JOIN sys.tables t ON i.object_id = t.object_id
                  WHERE t.name = @tableName AND i.name = @indexName AND t.schema_id = SCHEMA_ID('dbo')
                  """,
            ("@tableName", tableName),
            ("@indexName", indexName));

    public static async Task<int> GetStringColumnMaxLengthAsync(
        DbContext context,
        string tableName,
        string columnName)
    {
        if (TestDatabase.IsPostgreSql)
        {
            var pgLength = await ScalarNullableIntAsync(
                context,
                """
                SELECT character_maximum_length
                FROM information_schema.columns
                WHERE table_schema = 'dbo' AND table_name = @tableName AND column_name = @columnName
                """,
                ("@tableName", tableName),
                ("@columnName", columnName));
            return pgLength ?? -1;
        }

        var sqlLength = await ScalarNullableIntAsync(
            context,
            "SELECT COL_LENGTH('dbo.' + @tableName, @columnName)",
            ("@tableName", tableName),
            ("@columnName", columnName));
        if (sqlLength is null or < 0)
        {
            return -1;
        }

        return sqlLength.Value / 2;
    }

    public static async Task<string> GetFunctionDefinitionAsync(DbContext context, string functionName)
    {
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = TestDatabase.IsPostgreSql
                ? """
                  SELECT pg_get_functiondef(p.oid)
                  FROM pg_proc p
                  JOIN pg_namespace n ON n.oid = p.pronamespace
                  WHERE n.nspname = 'dbo'
                    AND (p.proname = @name OR p.proname = lower(@name))
                  """
                : "SELECT OBJECT_DEFINITION(OBJECT_ID('[dbo].[' + @name + ']'))";
            AddParameter(command, "@name", functionName);
            return (await command.ExecuteScalarAsync())?.ToString() ?? string.Empty;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<int?> ScalarNullableIntAsync(
        DbContext context,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                AddParameter(command, name, value);
            }

            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : Convert.ToInt32(result);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<int> ScalarIntAsync(
        DbContext context,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                AddParameter(command, name, value);
            }

            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? 0 : Convert.ToInt32(result);
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<bool> ExistsAsync(
        DbContext context,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            await connection.OpenAsync();
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                AddParameter(command, name, value);
            }

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
