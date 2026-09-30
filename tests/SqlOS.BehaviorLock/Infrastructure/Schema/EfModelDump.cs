using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SqlOS.BehaviorLock.Infrastructure.Schema;

/// <summary>
/// Renders the EF Core mapping of the SqlOS model: for every entity, its table, each property's
/// column name, store type, nullability, length, default, collation, and value converter, then
/// keys, foreign keys with delete behavior, indexes with filters, and mapped database functions.
/// SqlOS excludes its tables from EF migrations, so this mapping (not generated DDL) is what hosts'
/// LINQ queries depend on.
/// </summary>
public static class EfModelDump
{
    public static string Render(DbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var builder = new StringBuilder();
        builder.Append("# SqlOS EF Core model (").Append(context.Database.ProviderName).Append(")\n");
        foreach (var entity in model.GetEntityTypes().OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            var table = entity.GetTableName();
            var schema = entity.GetSchema();
            builder.Append("\n## ").Append(entity.ClrType.Name);
            builder.Append(table == null ? " (no table)" : $" -> {(schema == null ? string.Empty : schema + ".")}{table}");
            if (entity.IsTableExcludedFromMigrations())
            {
                builder.Append(" [excluded from migrations]");
            }

            builder.Append('\n');
            var store = table == null ? (StoreObjectIdentifier?)null : StoreObjectIdentifier.Table(table, schema);
            foreach (var property in entity.GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                builder.Append("  ").Append(property.Name).Append(": ")
                    .Append(store is { } storeObject ? property.GetColumnName(storeObject) : property.GetColumnName())
                    .Append(' ').Append(property.GetColumnType())
                    .Append(property.IsNullable ? " NULL" : " NOT NULL");
                if (property.GetMaxLength() is { } maxLength)
                {
                    builder.Append(" maxLength=").Append(maxLength);
                }

                if (property.GetDefaultValueSql() is { } defaultSql)
                {
                    builder.Append(" defaultSql=").Append(defaultSql);
                }

                if (property.GetCollation() is { } collation)
                {
                    builder.Append(" collation=").Append(collation);
                }

                if (property.GetValueConverter() is { } converter)
                {
                    builder.Append(" converter=").Append(converter.ModelClrType.Name).Append("->").Append(converter.ProviderClrType.Name);
                }

                if (property.IsConcurrencyToken)
                {
                    builder.Append(" concurrency");
                }

                builder.Append('\n');
            }

            foreach (var key in entity.GetKeys().OrderBy(key => key.IsPrimaryKey() ? 0 : 1).ThenBy(key => string.Join(',', key.Properties.Select(p => p.Name)), StringComparer.Ordinal))
            {
                builder.Append("  ").Append(key.IsPrimaryKey() ? "PRIMARY KEY " : "ALTERNATE KEY ")
                    .Append(key.GetName() ?? string.Empty)
                    .Append(" (").Append(string.Join(", ", key.Properties.Select(property => property.Name))).Append(")\n");
            }

            foreach (var foreignKey in entity.GetForeignKeys().OrderBy(foreignKey => foreignKey.GetConstraintName(), StringComparer.Ordinal))
            {
                builder.Append("  FOREIGN KEY ").Append(foreignKey.GetConstraintName() ?? string.Empty)
                    .Append(" (").Append(string.Join(", ", foreignKey.Properties.Select(property => property.Name))).Append(") -> ")
                    .Append(foreignKey.PrincipalEntityType.ClrType.Name)
                    .Append(" (").Append(string.Join(", ", foreignKey.PrincipalKey.Properties.Select(property => property.Name))).Append(")")
                    .Append(" delete=").Append(foreignKey.DeleteBehavior)
                    .Append(foreignKey.IsRequired ? " required" : " optional")
                    .Append(foreignKey.IsUnique ? " unique" : string.Empty)
                    .Append('\n');
            }

            foreach (var index in entity.GetIndexes().OrderBy(index => index.GetDatabaseName(), StringComparer.Ordinal))
            {
                builder.Append("  INDEX ").Append(index.GetDatabaseName() ?? string.Empty)
                    .Append(index.IsUnique ? " UNIQUE" : string.Empty)
                    .Append(" (").Append(string.Join(", ", index.Properties.Select(property => property.Name))).Append(')');
                if (index.GetFilter() is { } filter)
                {
                    builder.Append(" WHERE ").Append(filter);
                }

                builder.Append('\n');
            }
        }

        foreach (var function in model.GetDbFunctions().OrderBy(function => function.ModelName, StringComparer.Ordinal))
        {
            builder.Append("\n## function ").Append(function.Schema == null ? string.Empty : function.Schema + ".").Append(function.Name)
                .Append('(').Append(string.Join(", ", function.Parameters.Select(parameter => $"{parameter.Name} {parameter.StoreType}"))).Append(')')
                .Append(" returns ").Append(function.ReturnType.Name).Append('\n');
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }
}
