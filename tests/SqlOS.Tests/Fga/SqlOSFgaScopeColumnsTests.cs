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
    public void EveryResourceEntity_DeclaresTheColumn_AndSqlOSConfiguresIt_TheSameOnBothEngines()
    {
        foreach (var context in new DbContext[] { Create<OnSqlServer>(SqlOSDatabase.SqlServerProviderName), Create<OnPostgreSql>(SqlOSDatabase.PostgreSqlProviderName) })
        {
            using var _ = context;
            var orders = context.Model.FindEntityType(typeof(Order))!;

            // The entity's own property (IHasResourceId.FgaScope), not a shadow one; a byte string with room for
            // any depth, which EF Core never writes.
            var scope = orders.FindProperty(SqlOSFgaLineage.ScopeColumn)!;
            scope.IsShadowProperty().Should().BeFalse();
            scope.ClrType.Should().Be(typeof(byte[]));
            scope.GetMaxLength().Should().Be(SqlOSFgaLineage.ScopeMaxLength);
            scope.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
            scope.GetAfterSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
            orders.GetDeclaredTriggers().Select(t => t.GetDatabaseName())
                .Should().BeEquivalentTo(SqlOSFgaLineage.ScopeTriggerNames("Orders"));

            // No per-level indexes in the model: SqlOS creates them at startup, outside the migrations.
            orders.GetIndexes().Select(i => i.GetDatabaseName()).Should().NotContain(n => n!.Contains(SqlOSFgaLineage.ScopeColumn, StringComparison.Ordinal));

            // The resource id is indexed for the triggers (Orders declared its own; Notes gets SqlOS's).
            orders.GetIndexes().Should().Contain(i => i.Properties.Count == 1 && i.Properties[0].Name == "ResourceId");
            context.Model.FindEntityType(typeof(Note))!.GetIndexes().Should().Contain(i => i.Name == "IX_Notes_SqlOSFgaResourceId");

            // The tables for the database routines, with the orders to mirror per level: the declared PlacedAt
            // index (then the key); not the ResourceId lookup, the unique Number, or the StoreId foreign key's
            // index; and not Stores, which has no resource id.
            SqlOSFgaScopeColumns.Tables(context.Model).Should().BeEquivalentTo(
            [
                new SqlOSFgaScopeTable(null, "Notes", "ResourceId", ["Id"], []),
                new SqlOSFgaScopeTable("sales", "Orders", "ResourceId", ["Id"], [new SqlOSFgaScopeOrder("PlacedAt", ["PlacedAt", "Id"])]),
            ]);
        }
    }

    [TestMethod]
    public void AnEntityThatImplementsTheColumnExplicitly_GetsTheSameColumn()
    {
        // byte[]? IHasResourceId.FgaScope => null; keeps the column off the class's public surface (and out of
        // JSON). EF Core does not map an explicit implementation by convention, so SqlOS maps the column itself.
        using var context = new ExplicitScopeDbContext(new DbContextOptionsBuilder<ExplicitScopeDbContext>()
            .UseSqlServer("Server=unused;Database=unused;TrustServerCertificate=True").Options);
        var entity = context.Model.FindEntityType(typeof(ExplicitNote))!;

        var scope = entity.FindProperty(SqlOSFgaLineage.ScopeColumn)!;
        scope.IsShadowProperty().Should().BeTrue();
        scope.ClrType.Should().Be(typeof(byte[]));
        scope.GetMaxLength().Should().Be(SqlOSFgaLineage.ScopeMaxLength);
        scope.GetBeforeSaveBehavior().Should().Be(PropertySaveBehavior.Ignore);
        entity.GetDeclaredTriggers().Select(t => t.GetDatabaseName()).Should().BeEquivalentTo(SqlOSFgaLineage.ScopeTriggerNames("ExplicitNotes"));
        SqlOSFgaScopeColumns.Tables(context.Model).Should().ContainSingle(t => t.Table == "ExplicitNotes");
    }

    [TestMethod]
    public void WithoutAProvider_TheModelIsTheSame()
    {
        // ApplySqlOSFgaModel without a provider name, or an in-memory test context: nothing depends on it.
        using var context = Create<OnNoProvider>(providerName: null);
        var scope = context.Model.FindEntityType(typeof(Order))!.FindProperty(SqlOSFgaLineage.ScopeColumn)!;

        scope.ClrType.Should().Be(typeof(byte[]));
        scope.GetMaxLength().Should().Be(SqlOSFgaLineage.ScopeMaxLength);
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
            modelBuilder.UseSqlOS(providerName, new SqlOSFgaOptions { MaxResourceHierarchyDepth = depth });
        }
    }

    private sealed class ExplicitScopeDbContext(DbContextOptions<ExplicitScopeDbContext> options) : DbContext(options)
    {
        public DbSet<ExplicitNote> Notes => Set<ExplicitNote>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ExplicitNote>(note =>
            {
                note.ToTable("ExplicitNotes");
                note.HasKey(n => n.Id);
            });
            modelBuilder.UseSqlOS(SqlOSDatabase.SqlServerProviderName);
        }
    }

    private sealed class ExplicitNote : IHasResourceId
    {
        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;

        byte[]? IHasResourceId.FgaScope => null;
    }

    private sealed class Store
    {
        public int Id { get; set; }
    }

    private sealed class Order : IHasResourceId
    {
        public byte[]? FgaScope { get; private set; }

        public int Id { get; set; }
        public int StoreId { get; set; }
        public string ResourceId { get; set; } = string.Empty;
        public string Number { get; set; } = string.Empty;
        public DateTime PlacedAt { get; set; }
    }

    private sealed class Note : IHasResourceId
    {
        public byte[]? FgaScope { get; private set; }

        public int Id { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
