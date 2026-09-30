using System.Data.Common;
using System.Globalization;
using System.Text;
using SqlOS.BehaviorLock.Host;
using SqlOS.BehaviorLock.Infrastructure.Database;

namespace SqlOS.BehaviorLock.Infrastructure.Schema;

/// <summary>
/// Renders the SqlOS schema (the <c>dbo</c> schema that SqlOS bootstrap owns) as normalized text:
/// the schema version and applied migrations, every table with its columns (type, nullability,
/// default, collation, identity) in ordinal order, primary keys, unique and check constraints,
/// foreign keys with their actions, indexes with key order, included columns, and filters, and
/// the definitions of functions and procedures (the FGA read path). Names SQL Server generates
/// for unnamed constraints end in random hex and render as <c>{system-named}</c>.
/// </summary>
public static class SchemaDump
{
    public const string SchemaName = "dbo";

    public static async Task<string> RenderAsync(string connectionString)
    {
        await using var connection = BehaviorLockDatabase.OpenConnection(connectionString);
        var builder = new StringBuilder();
        builder.Append("# SqlOS schema (").Append(BehaviorLockDatabase.ProviderName).Append(") after bootstrap\n");
        await RenderVersionsAsync(connection, builder);
        if (BehaviorLockDatabase.Provider == DatabaseProvider.SqlServer)
        {
            await RenderSqlServerAsync(connection, builder);
        }
        else
        {
            await RenderPostgreSqlAsync(connection, builder);
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    private static async Task RenderVersionsAsync(DbConnection connection, StringBuilder builder)
    {
        var quote = BehaviorLockDatabase.Provider == DatabaseProvider.SqlServer
            ? (Func<string, string>)(name => $"[{name}]")
            : name => $"\"{name}\"";
        var auth = await ScalarAsync(connection, $"SELECT {quote("Version")} FROM {quote(SchemaName)}.{quote("SqlOSSchema")}");
        var fga = await ScalarAsync(connection, $"SELECT {quote("Version")} FROM {quote(SchemaName)}.{quote("SqlOSFgaSchema")}");
        builder.Append("\n## Schema version\n");
        builder.Append("auth: ").Append(auth).Append('\n');
        builder.Append("fga: ").Append(fga).Append('\n');
        builder.Append("\n## Applied migrations\n");
        foreach (var row in await QueryAsync(
                     connection,
                     $"SELECT {quote("Version")}, {quote("ScriptName")} FROM {quote(SchemaName)}.{quote("SqlOSAppliedMigrations")}"))
        {
            builder.Append(Convert.ToInt32(row[0], CultureInfo.InvariantCulture).ToString("000", CultureInfo.InvariantCulture))
                .Append(' ').Append(row[1]).Append('\n');
        }

        SortLastSection(builder, "## Applied migrations\n");
    }

    private static async Task RenderSqlServerAsync(DbConnection connection, StringBuilder builder)
    {
        var databaseCollation = (string)(await ScalarAsync(connection, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))"))!;
        var columns = await QueryAsync(connection, """
            SELECT t.name, c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   c.collation_name, dc.definition, dc.name, dc.is_system_named, c.is_computed, cc.definition
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE s.name = 'dbo'
            ORDER BY t.name, c.column_id
            """);
        var keys = await QueryAsync(connection, """
            SELECT t.name, kc.name, kc.type_desc, kc.is_system_named, c.name, ic.key_ordinal, ic.is_descending_key
            FROM sys.key_constraints kc
            JOIN sys.tables t ON t.object_id = kc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = 'dbo'
            ORDER BY t.name, kc.type_desc, kc.name, ic.key_ordinal
            """);
        var checks = await QueryAsync(connection, """
            SELECT t.name, cc.name, cc.is_system_named, cc.definition
            FROM sys.check_constraints cc
            JOIN sys.tables t ON t.object_id = cc.parent_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'dbo'
            ORDER BY t.name, cc.definition, cc.name
            """);
        var foreignKeys = await QueryAsync(connection, """
            SELECT tp.name, fk.name, fk.is_system_named, cp.name, tr.name, cr.name,
                   fk.delete_referential_action_desc, fk.update_referential_action_desc, fkc.constraint_column_id
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables tp ON tp.object_id = fk.parent_object_id
            JOIN sys.schemas s ON s.schema_id = tp.schema_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables tr ON tr.object_id = fk.referenced_object_id
            JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE s.name = 'dbo'
            ORDER BY tp.name, fk.name, fkc.constraint_column_id
            """);
        var indexes = await QueryAsync(connection, """
            SELECT t.name, i.name, i.type_desc, i.is_unique, i.has_filter, i.filter_definition,
                   c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = 'dbo' AND i.type > 0 AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
            ORDER BY t.name, i.name, ic.is_included_column, ic.key_ordinal, c.name
            """);
        var routines = await QueryAsync(connection, """
            SELECT o.name, o.type_desc, m.definition
            FROM sys.sql_modules m
            JOIN sys.objects o ON o.object_id = m.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE s.name = 'dbo'
            ORDER BY o.name
            """);

        builder.Append("\n## Tables\n");
        foreach (var table in columns.GroupBy(row => (string)row[0]!))
        {
            builder.Append("\n### ").Append(table.Key).Append('\n');
            foreach (var row in table)
            {
                builder.Append("  ").Append(row[1]).Append(' ').Append(SqlServerType((string)row[2]!, Convert.ToInt32(row[3]), Convert.ToInt32(row[4]), Convert.ToInt32(row[5])));
                if (row[12] is true)
                {
                    builder.Append(" AS ").Append(row[13]);
                }

                builder.Append((bool)row[6]! ? " NULL" : " NOT NULL");
                if ((bool)row[7]!)
                {
                    builder.Append(" IDENTITY");
                }

                if (row[8] is string collation && !string.Equals(collation, databaseCollation, StringComparison.Ordinal))
                {
                    builder.Append(" COLLATE ").Append(collation);
                }

                if (row[9] is string defaultDefinition)
                {
                    builder.Append(" DEFAULT ").Append(defaultDefinition);
                    if (row[11] is false)
                    {
                        builder.Append(" [").Append(row[10]).Append(']');
                    }
                }

                builder.Append('\n');
            }

            foreach (var key in keys.Where(row => (string)row[0]! == table.Key).GroupBy(row => (string)row[1]!))
            {
                var first = key.First();
                builder.Append("  ").Append((string)first[2]! == "PRIMARY_KEY_CONSTRAINT" ? "PRIMARY KEY" : "UNIQUE")
                    .Append(' ').Append(ConstraintName(key.Key, first[3]))
                    .Append(" (").Append(string.Join(", ", key.Select(row => (string)row[4]! + ((bool)row[6]! ? " DESC" : string.Empty)))).Append(")\n");
            }

            foreach (var check in checks.Where(row => (string)row[0]! == table.Key))
            {
                builder.Append("  CHECK ").Append(ConstraintName((string)check[1]!, check[2])).Append(' ').Append(check[3]).Append('\n');
            }

            foreach (var foreignKey in foreignKeys.Where(row => (string)row[0]! == table.Key).GroupBy(row => (string)row[1]!))
            {
                var first = foreignKey.First();
                builder.Append("  FOREIGN KEY ").Append(ConstraintName(foreignKey.Key, first[2]))
                    .Append(" (").Append(string.Join(", ", foreignKey.Select(row => row[3]))).Append(") REFERENCES ")
                    .Append(first[4]).Append(" (").Append(string.Join(", ", foreignKey.Select(row => row[5]))).Append(')')
                    .Append(" ON DELETE ").Append(first[6]).Append(" ON UPDATE ").Append(first[7]).Append('\n');
            }

            foreach (var index in indexes.Where(row => (string)row[0]! == table.Key).GroupBy(row => (string)row[1]!))
            {
                var first = index.First();
                builder.Append("  INDEX ").Append(index.Key).Append((bool)first[3]! ? " UNIQUE" : string.Empty)
                    .Append(' ').Append(first[2])
                    .Append(" (").Append(string.Join(", ", index.Where(row => !(bool)row[9]!).Select(row => (string)row[6]! + ((bool)row[8]! ? " DESC" : string.Empty)))).Append(')');
                var included = index.Where(row => (bool)row[9]!).Select(row => (string)row[6]!).ToList();
                if (included.Count > 0)
                {
                    builder.Append(" INCLUDE (").Append(string.Join(", ", included)).Append(')');
                }

                if ((bool)first[4]!)
                {
                    builder.Append(" WHERE ").Append(first[5]);
                }

                builder.Append('\n');
            }
        }

        builder.Append("\n## Routines\n");
        foreach (var routine in routines)
        {
            builder.Append("\n### ").Append(routine[0]).Append(" (").Append(routine[1]).Append(")\n");
            builder.Append(NormalizeDefinition((string)routine[2]!)).Append('\n');
        }
    }

    private static async Task RenderPostgreSqlAsync(DbConnection connection, StringBuilder builder)
    {
        var columns = await QueryAsync(connection, """
            SELECT c.relname, a.attname, format_type(a.atttypid, a.atttypmod), a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid), co.collname, a.attidentity, a.attgenerated
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid = c.oid AND d.adnum = a.attnum
            LEFT JOIN pg_collation co ON co.oid = a.attcollation AND a.attcollation <> 0 AND co.collname <> 'default'
            WHERE n.nspname = 'dbo' AND c.relkind = 'r'
            ORDER BY c.relname, a.attnum
            """);
        var constraints = await QueryAsync(connection, """
            SELECT c.relname, con.conname, con.contype, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'dbo'
            ORDER BY c.relname, con.contype, con.conname
            """);
        var indexes = await QueryAsync(connection, """
            SELECT i.tablename, i.indexname, i.indexdef
            FROM pg_indexes i
            WHERE i.schemaname = 'dbo'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_constraint con
                  JOIN pg_class ic ON ic.oid = con.conindid
                  WHERE ic.relname = i.indexname AND con.contype IN ('p', 'u'))
            ORDER BY i.tablename, i.indexname
            """);
        var routines = await QueryAsync(connection, """
            SELECT p.proname, pg_get_function_identity_arguments(p.oid), pg_get_functiondef(p.oid)
            FROM pg_proc p
            JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'dbo'
            ORDER BY p.proname, pg_get_function_identity_arguments(p.oid)
            """);

        builder.Append("\n## Tables\n");
        foreach (var table in columns.GroupBy(row => (string)row[0]!))
        {
            builder.Append("\n### ").Append(table.Key).Append('\n');
            foreach (var row in table)
            {
                builder.Append("  ").Append(row[1]).Append(' ').Append(row[2]);
                if (row[7] is char generated && generated != '\0' || row[7] is string generatedText && generatedText.Length > 0 && generatedText != "\0")
                {
                    builder.Append(" GENERATED");
                }

                builder.Append((bool)row[3]! ? " NOT NULL" : " NULL");
                if (row[6] is char identity && identity != '\0' || row[6] is string identityText && identityText.Length > 0 && identityText != "\0")
                {
                    builder.Append(" IDENTITY");
                }

                if (row[5] is string collation)
                {
                    builder.Append(" COLLATE ").Append(collation);
                }

                if (row[4] is string defaultDefinition)
                {
                    builder.Append(" DEFAULT ").Append(defaultDefinition);
                }

                builder.Append('\n');
            }

            foreach (var constraint in constraints.Where(row => (string)row[0]! == table.Key))
            {
                var kind = constraint[2] switch
                {
                    char type => type.ToString(),
                    var value => value?.ToString() ?? string.Empty
                };
                builder.Append("  ").Append(kind switch
                {
                    "p" => "PRIMARY KEY",
                    "u" => "UNIQUE",
                    "f" => "FOREIGN KEY",
                    "c" => "CHECK",
                    _ => "CONSTRAINT"
                }).Append(' ').Append(constraint[1]).Append(' ').Append(constraint[3]).Append('\n');
            }

            foreach (var index in indexes.Where(row => (string)row[0]! == table.Key))
            {
                builder.Append("  INDEX ").Append(index[1]).Append(' ').Append(index[2]).Append('\n');
            }
        }

        builder.Append("\n## Routines\n");
        foreach (var routine in routines)
        {
            builder.Append("\n### ").Append(routine[0]).Append('(').Append(routine[1]).Append(")\n");
            builder.Append(NormalizeDefinition((string)routine[2]!)).Append('\n');
        }
    }

    private static string SqlServerType(string type, int maxLength, int precision, int scale)
        => type switch
        {
            "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{type}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
            _ => type
        };

    private static string ConstraintName(string name, object? isSystemNamed)
        => isSystemNamed is true ? "{system-named}" : name;

    /// <summary>Routine bodies keep their text; only line endings and trailing whitespace are normalized.</summary>
    private static string NormalizeDefinition(string definition)
        => string.Join('\n', definition.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Select(line => line.Length == 0 ? line : "    " + line)).TrimEnd();

    private static void SortLastSection(StringBuilder builder, string header)
    {
        var text = builder.ToString();
        var start = text.LastIndexOf(header, StringComparison.Ordinal) + header.Length;
        var lines = text[start..].Split('\n', StringSplitOptions.RemoveEmptyEntries).OrderBy(line => line, StringComparer.Ordinal);
        builder.Length = start;
        foreach (var line in lines)
        {
            builder.Append(line).Append('\n');
        }
    }

    private static async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<List<object?[]>> QueryAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(row);
        }

        return rows;
    }
}
