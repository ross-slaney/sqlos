using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SqlOS.Database;
using SqlOS.Extensions;
using SqlOS.Fga;
using SqlOS.Fga.Configuration;
using SqlOS.Fga.Interfaces;

namespace SqlOS.Tests.Fga;

/// <summary>
/// The scope column pass: every application entity with a ResourceId gets the one database-owned shadow
/// column (an array on PostgreSQL, a byte string on SQL Server), the triggers declared, and the table listed
/// for the database routines with the orders it declared indexes for. The per-level indexes are not part of
/// the model: SqlOS creates them at startup, outside the application's migrations.
/// </summary>
[TestClass]
public class SqlOSFgaScopeColumnsTests
{
    [TestMethod]
    public void SqlServer_AddsTheByteColumnAndTriggersToEveryResourceBackedEntity()
    {
        using var context = Create<OnSqlServer>(SqlOSDatabase.SqlServerProviderName);
        var orders = context.Model.FindEntityType(typeof(Order))!;

        SqlOSFgaScopeColumns.Has(orders).Should().BeTrue();
        var scope = orders.FindProperty(SqlOSFgaLineage.ScopeColumn);
        scope.Should().NotBeNull();
        scope!.IsShadowProperty().Should().BeTrue();
        scope.ClrType.Should().Be(typeof(byte[]));
        scope.GetColumnType().Should().Be("varbinary(512)");
        scope.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        scope.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        orders.GetDeclaredTriggers().Select(t => t.GetDatabaseName())
            .Should().BeEquivalentTo(SqlOSFgaLineage.ScopeTriggerNames("Orders"));

        // Nothing else lands in the model: no ancestor columns, no per-level indexes.
        orders.GetProperties().Select(p => p.Name).Where(n => n.StartsWith(SqlOSFgaLineage.ScopePrefix, StringComparison.Ordinal))
            .Should().Equal(SqlOSFgaLineage.ScopeColumn);
        orders.GetIndexes().Select(i => i.GetDatabaseName()).Should().NotContain(n => n.Contains(SqlOSFgaLineage.ScopeColumn, StringComparison.Ordinal));

        // The resource id is indexed for the triggers (Orders declared its own; Notes gets SqlOS's), and a SqlOS
        // entity is never touched.
        orders.GetIndexes().Should().Contain(i => i.Properties.Count == 1 && i.Properties[0].Name == "ResourceId");
        context.Model.FindEntityType(typeof(Note))!.GetIndexes().Should().Contain(i => i.Name == "IX_Notes_SqlOSFgaResourceId");
        SqlOSFgaScopeColumns.Has(context.Model.FindEntityType(typeof(Store))!).Should().BeFalse("no ResourceId");

        // The tables for the database routines, with the orders to mirror per level: the declared PlacedAt
        // index (then the key); not the ResourceId lookup, the unique Number, or the StoreId foreign key's index.
        SqlOSFgaScopeColumns.Tables(context.Model).Should().BeEquivalentTo(
        [
            new SqlOSFgaScopeTable(null, "Notes", "ResourceId", ["Id"], []),
            new SqlOSFgaScopeTable("sales", "Orders", "ResourceId", ["Id"], [new SqlOSFgaScopeOrder("PlacedAt", ["PlacedAt", "Id"])]),
        ]);
    }

    [TestMethod]
    public void PostgreSql_AddsTheArrayColumn()
    {
        using var context = Create<OnPostgreSql>(SqlOSDatabase.PostgreSqlProviderName);
        var scope = context.Model.FindEntityType(typeof(Order))!.FindProperty(SqlOSFgaLineage.ScopeColumn);

        scope.Should().NotBeNull();
        scope!.ClrType.Should().Be(typeof(long?[]));
        scope.GetColumnType().Should().Be("bigint[]");
        scope.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
    }

    [TestMethod]
    public void WithoutAProvider_StillAddsTheColumn()
    {
        // In-memory and SQLite test contexts: the column exists so the model is the same shape everywhere.
        using var context = Create<OnNoProvider>(providerName: null);
        var scope = context.Model.FindEntityType(typeof(Order))!.FindProperty(SqlOSFgaLineage.ScopeColumn);

        scope.Should().NotBeNull();
        scope!.ClrType.Should().Be(typeof(byte[]));
    }

    [TestMethod]
    public void ADepthBeyondTheColumnsRoom_Fails()
    {
        var act = () => new ScopeModelDbContext<TooDeep>(Options<ScopeModelDbContext<TooDeep>>(SqlOSDatabase.SqlServerProviderName), SqlOSDatabase.SqlServerProviderName, depth: 70).Model;
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxResourceHierarchyDepth*");
    }

    private static DbContext Create<TMarker>(string? providerName)
        => new ScopeModelDbContext<TMarker>(Options<ScopeModelDbContext<TMarker>>(providerName), providerName, depth: 2);

    private static DbContextOptions<TContext> Options<TContext>(string? providerName) where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        if (providerName == SqlOSDatabase.PostgreSqlProviderName)
        {
            builder.UseNpgsql("Host=localhost;Database=sqlos_scope;Username=sqlos;Password=sqlos");
        }
        else
        {
            builder.UseSqlServer("Server=.;Database=SqlOS_Scope;Trusted_Connection=True;TrustServerCertificate=True");
        }

        return builder.Options;
    }

    private sealed class OnSqlServer;

    private sealed class OnPostgreSql;

    private sealed class OnNoProvider;

    private sealed class TooDeep;

    // One context type per configuration: EF Core caches the model by context type.
    private sealed class ScopeModelDbContext<TMarker>(DbContextOptions<ScopeModelDbContext<TMarker>> options, string? providerName, int depth) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Store>(store =>
            {
                store.ToTable("Stores");
                store.HasKey(s => s.Id);
            });
            modelBuilder.Entity<Order>(order =>
            {
                order.ToTable("Orders", "sales");
                order.HasKey(o => o.Id);
                order.HasIndex(o => o.ResourceId).IsUnique();
                order.HasIndex(o => o.Number).IsUnique();
                order.HasIndex(o => o.PlacedAt).HasDatabaseName("IX_Orders_PlacedAt");
                order.HasOne<Store>().WithMany().HasForeignKey(o => o.StoreId);
            });
            modelBuilder.Entity<Note>(note =>
            {
                note.ToTable("Notes");
                note.HasKey(n => n.Id);
            });

            // The application configured its entities first; the SqlOS model and the scope pass come last.
            modelBuilder.UseSqlOS(GetType(), providerName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = depth });
        }
    }

    private sealed class Store
    {
        public int Id { get; set; }
    }

    private sealed class Order : IHasResourceId
    {
        public int Id { get; set; }
        public int StoreId { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public DateTime PlacedAt { get; set; }
    }

    private sealed class Note : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
