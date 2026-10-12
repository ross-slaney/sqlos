using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using Microsoft.EntityFrameworkCore.Query.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;
using SqlOS.Fga.Models;

namespace SqlOS.Fga.Indexing;

internal static class SqlOSFgaNativeOptions
{
    internal const string SchemaAnnotation = "SqlOS:Fga:Schema";
    internal const string TablesAnnotation = "SqlOS:Fga:Tables";
    internal const string ResourceRelationshipAnnotation = "SqlOS:Fga:ResourceRelationship";
    private static readonly ConditionalWeakTable<IModel, ConcurrentDictionary<string, SqlOSFgaScopeDiscovery>> Installed = new();

    internal static void Configure(DbContextOptionsBuilder builder)
    {
        ((IDbContextOptionsBuilderInfrastructure)builder).AddOrUpdateExtension(new SqlOSFgaNativeOptionsExtension());
#pragma warning disable EF1001 // Rewrite before parameter extraction; EF still compiles and executes the resulting query.
        builder.ReplaceService<IQueryCompiler, SqlOSFgaNativeQueryCompiler>();
#pragma warning restore EF1001
        builder.ReplaceService<IModelCustomizer, SqlOSFgaNativeModelCustomizer>();
    }

    internal static bool Enabled(DbContext context)
        => context.GetService<IDbContextOptions>().FindExtension<SqlOSFgaNativeOptionsExtension>() is not null;

    internal static SqlOSFgaOptions FromModel(IModel model)
        => new()
        {
            Schema = model.FindAnnotation(SchemaAnnotation)?.Value as string ?? "dbo",
            TableNames = model.FindAnnotation(TablesAnnotation)?.Value is string json
                ? JsonSerializer.Deserialize<SqlOSFgaTableNames>(json)! : new(),
            MaxResourceHierarchyDepth = model.FindAnnotation(SqlOSFgaHierarchyDepth.ModelAnnotationName)?.Value is int depth
                ? depth : SqlOSFgaHierarchyDepth.Default
        };

    internal static SqlOSFgaScopeDiscovery? Discovery(DbContext context)
        => Installed.TryGetValue(context.Model, out var databases)
           && databases.TryGetValue(DatabaseKey(context), out var discovery) ? discovery : null;

    internal static void Register(DbContext context, SqlOSFgaScopeDiscovery discovery)
        => Installed.GetOrCreateValue(context.Model)[DatabaseKey(context)] = discovery;

    private static string DatabaseKey(DbContext context)
    {
        // SqlClient removes credentials after opening unless PersistSecurityInfo is enabled.
        // Opening the same database must not change whether its installed indexes are recognized.
        var connection = new DbConnectionStringBuilder { ConnectionString = context.Database.GetDbConnection().ConnectionString };
        connection.Remove("Password");
        connection.Remove("Pwd");
        var normalized = string.Join("\n", connection.Keys.Cast<string>().OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Select(key => key.ToUpperInvariant() + "=" + connection[key]));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((context.Database.ProviderName ?? "") + "\n" + normalized)));
    }
}

internal sealed class SqlOSFgaNativeOptionsExtension : IDbContextOptionsExtension
{
    private DbContextOptionsExtensionInfo? _info;
    public DbContextOptionsExtensionInfo Info => _info ??= new ExtensionInfo(this);
    public void ApplyServices(IServiceCollection services)
        => services.TryAddEnumerable(ServiceDescriptor.Scoped<IConventionSetPlugin, SqlOSFgaNativeConventionSetPlugin>());
    public void Validate(IDbContextOptions options) { }

    private sealed class ExtensionInfo(IDbContextOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;
        public override string LogFragment => "SqlOS native authorized indexes ";
        public override int GetServiceProviderHashCode() => 0;
        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;
        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) => debugInfo["SqlOS:NativeIndexes"] = "1";
    }
}

/// <summary>Runs after the application's model configuration, so normal SaveChanges works with managed triggers.</summary>
internal sealed class SqlOSFgaNativeModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        if (modelBuilder.Model.FindEntityType(typeof(SqlOSFgaResource)) is not null)
        {
            foreach (var entity in modelBuilder.Model.GetEntityTypes().ToArray())
            {
                if (!typeof(ISqlOSResourceEntity).IsAssignableFrom(entity.ClrType)) continue;
                if (entity.IsOwned() || entity.BaseType is not null || entity.GetDerivedTypes().Any()
                    || entity.FindPrimaryKey() is null || entity.GetTableName() is null
                    || entity.GetViewName() is not null || entity.GetSqlQuery() is not null
                    || entity.GetMappingFragments(StoreObjectType.Table).Any()
                    || modelBuilder.Model.GetEntityTypes().Any(other => other != entity
                        && other.GetTableName() == entity.GetTableName() && other.GetSchema() == entity.GetSchema())) continue;
                var resourceId = entity.FindProperty(nameof(IHasResourceId.ResourceId));
                if (resourceId?.ClrType != typeof(string) || resourceId.GetValueConverter() is not null
                    || resourceId.GetProviderClrType() is not null
                    || entity.GetForeignKeys().Any(fk => fk.Properties.Contains(resourceId))) continue;

                // SaveChanges must insert the synchronized resource before its owning app row.
                // Use the application's existing column and preserve any relationship it declared.
                // Mark first: removing a conventional FK index afterward would recreate it.
                entity.SetAnnotation(SqlOSFgaNativeOptions.ResourceRelationshipAnnotation, true);
                modelBuilder.Entity(entity.Name).HasOne(typeof(SqlOSFgaResource)).WithMany()
                    .HasForeignKey(resourceId.Name).OnDelete(DeleteBehavior.Restrict);
            }
        }
        if (!context.Database.IsSqlServer()) return;
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.IsOwned()) continue;
            if (typeof(IHasResourceId).IsAssignableFrom(entity.ClrType) || entity.ClrType == typeof(SqlOSFgaResource))
                modelBuilder.Entity(entity.Name).ToTable(table => table.UseSqlOutputClause(false));
        }
    }
}

/// <summary>Suppresses only the conventional app-table index for SqlOS's automatic resource relationship.</summary>
internal sealed class SqlOSFgaNativeConventionSetPlugin(ProviderConventionSetBuilderDependencies dependencies) : IConventionSetPlugin
{
    public ConventionSet ModifyConventions(ConventionSet conventionSet)
    {
        conventionSet.Replace<ForeignKeyIndexConvention>(new SqlOSFgaNativeForeignKeyIndexConvention(dependencies));
        return conventionSet;
    }
}

internal sealed class SqlOSFgaNativeForeignKeyIndexConvention(ProviderConventionSetBuilderDependencies dependencies)
    : ForeignKeyIndexConvention(dependencies)
{
    protected override IConventionIndex? CreateIndex(IReadOnlyList<IConventionProperty> properties, bool unique,
        IConventionEntityTypeBuilder entityTypeBuilder)
    {
        if (properties.Count == 1 && properties[0].Name == nameof(IHasResourceId.ResourceId)
            && entityTypeBuilder.Metadata.FindAnnotation(SqlOSFgaNativeOptions.ResourceRelationshipAnnotation)?.Value is true)
            return null;
        return base.CreateIndex(properties, unique, entityTypeBuilder);
    }
}
